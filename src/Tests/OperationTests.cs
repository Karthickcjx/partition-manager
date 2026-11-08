using PartitionManager.Core.Layout;
using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;
using Xunit;
using static PartitionManager.Tests.Fixtures;

namespace PartitionManager.Tests;

public class OperationTests
{
    private static long FreeStart => 1 * MiB + 30 * GiB;

    [Fact]
    public void Create_in_free_space_adds_partition()
    {
        var s = new CreatePartitionOperation(1, FreeStart, 10 * GiB, "NTFS", "New", 'G').Apply(State(DataDisk()));
        var p = s.Disk(1).Partitions.Last();
        Assert.Equal(3, p.PartitionNumber);
        Assert.Equal('G', p.DriveLetter);
        Assert.Equal(PartitionTypes.BasicData, p.GptType);
        Assert.True(LayoutValidator.IsValid(s.Disk(1)));
    }

    [Fact]
    public void Create_overlapping_existing_partition_fails()
    {
        var ex = Assert.Throws<OperationValidationException>(() =>
            new CreatePartitionOperation(1, 5 * GiB, 1 * GiB, "NTFS", "", null).Apply(State(DataDisk())));
        Assert.Contains("unallocated", ex.Message);
    }

    [Fact]
    public void Create_misaligned_fails() =>
        Assert.Throws<OperationValidationException>(() =>
            new CreatePartitionOperation(1, FreeStart + 512, 1 * GiB, "NTFS", "", null).Apply(State(DataDisk())));

    [Fact]
    public void Create_with_used_letter_fails()
    {
        var ex = Assert.Throws<OperationValidationException>(() =>
            new CreatePartitionOperation(1, FreeStart, 1 * GiB, "NTFS", "", 'E').Apply(State(DataDisk())));
        Assert.Contains("in use", ex.Message);
    }

    [Fact]
    public void Create_letter_conflict_detected_across_disks()
    {
        Assert.Throws<OperationValidationException>(() =>
            new CreatePartitionOperation(1, FreeStart, 1 * GiB, "NTFS", "", 'C').Apply(State(DataDisk(), SystemDisk())));
    }

    [Fact]
    public void Create_large_fat32_fails()
    {
        var ex = Assert.Throws<OperationValidationException>(() =>
            new CreatePartitionOperation(1, FreeStart, 33 * GiB, "FAT32", "", null).Apply(State(DataDisk())));
        Assert.Contains("FAT32", ex.Message);
    }

    [Fact]
    public void Create_on_raw_disk_fails_until_initialized()
    {
        var raw = new DiskInfo { Number = 2, Model = "raw", SizeBytes = 8 * GiB, Style = PartitionStyle.Raw };
        Assert.Throws<OperationValidationException>(() => new CreatePartitionOperation(2, 1 * MiB, 1 * GiB, "NTFS", "", null).Apply(State(raw)));
        var s = new InitializeDiskOperation(2, PartitionStyle.Gpt).Apply(State(raw));
        s = new CreatePartitionOperation(2, 1 * MiB, 1 * GiB, "NTFS", "", null).Apply(s);
        Assert.Single(s.Disk(2).Partitions);
    }

    [Fact]
    public void Mbr_fifth_primary_is_refused()
    {
        var d = new DiskInfo { Number = 7, Model = "m", SizeBytes = 16 * GiB, Style = PartitionStyle.Mbr };
        d = d.WithPartitions(Enumerable.Range(0, 4).Select(i => Part(7, i + 1, (1 + i * 1024L) * MiB, 1 * GiB)));
        var ex = Assert.Throws<OperationValidationException>(() => new CreatePartitionOperation(7, 8 * GiB, 1 * GiB, "NTFS", "", null).Apply(State(d)));
        Assert.Contains("4 primary", ex.Message);
    }

    [Fact]
    public void Delete_removes_partition_and_frees_space()
    {
        var before = DiskLayout.UnallocatedBytes(DataDisk());
        var s = new DeletePartitionOperation(1, 2).Apply(State(DataDisk()));
        Assert.Null(s.Disk(1).FindPartition(2));
        Assert.Equal(before + 20 * GiB, DiskLayout.UnallocatedBytes(s.Disk(1)));
    }

    [Fact]
    public void Extend_into_free_space()
    {
        var s = new ResizeMovePartitionOperation(1, 2, 1 * MiB + 10 * GiB, 30 * GiB).Apply(State(DataDisk()));
        var p = s.Disk(1).FindPartition(2)!;
        Assert.Equal(30 * GiB, p.SizeBytes);
        Assert.Equal(25 * GiB, p.FreeBytes);
    }

    [Fact]
    public void Shrink_below_used_space_fails()
    {
        var ex = Assert.Throws<OperationValidationException>(() =>
            new ResizeMovePartitionOperation(1, 2, 1 * MiB + 10 * GiB, 4 * GiB).Apply(State(DataDisk())));
        Assert.Contains("Not enough space", ex.Message);
    }

    [Fact]
    public void Resize_into_neighbour_fails() =>
        Assert.Throws<OperationValidationException>(() =>
            new ResizeMovePartitionOperation(1, 1, 1 * MiB, 15 * GiB).Apply(State(DataDisk())));

    [Fact]
    public void Shrink_fat32_is_refused()
    {
        var d = DataDisk().WithPartitions([Part(1, 1, 1 * MiB, 10 * GiB, "FAT32", free: 9 * GiB)]);
        Assert.Throws<OperationValidationException>(() => new ResizeMovePartitionOperation(1, 1, 1 * MiB, 5 * GiB).Apply(State(d)));
    }

    [Fact]
    public void Resize_esp_is_refused() =>
        Assert.Throws<OperationValidationException>(() =>
            new ResizeMovePartitionOperation(0, 1, 1 * MiB, 50 * MiB).Apply(State(SystemDisk())));

    [Fact]
    public void Move_steps_include_data_move()
    {
        var state = State(DataDisk());
        var op = new ResizeMovePartitionOperation(1, 2, 2 * MiB + 10 * GiB, 20 * GiB);
        Assert.Contains("Moving file data...", op.GetSteps(state));
        op.Apply(state);
    }

    [Fact]
    public void Split_creates_second_partition_after_first()
    {
        var s = new SplitPartitionOperation(1, 2, 10 * GiB, "NTFS", "Split", 'H').Apply(State(DataDisk()));
        var parts = s.Disk(1).Partitions;
        Assert.Equal(3, parts.Count);
        var second = s.Disk(1).FindPartition(3)!;
        Assert.Equal(1 * MiB + 20 * GiB, second.OffsetBytes);
        Assert.Equal(10 * GiB, second.SizeBytes);
        Assert.True(LayoutValidator.IsValid(s.Disk(1)));
    }

    [Fact]
    public void Split_smaller_than_used_fails() =>
        Assert.Throws<OperationValidationException>(() =>
            new SplitPartitionOperation(1, 2, 2 * GiB, "NTFS", "", null).Apply(State(DataDisk())));

    [Fact]
    public void Merge_adjacent_partitions()
    {
        var s = new MergePartitionsOperation(1, 1, 2).Apply(State(DataDisk()));
        var merged = Assert.Single(s.Disk(1).Partitions);
        Assert.Equal(30 * GiB, merged.SizeBytes);
        Assert.Equal(30 * GiB - 2 * GiB - 5 * GiB, merged.FreeBytes);
        Assert.Equal('E', merged.DriveLetter);
    }

    [Fact]
    public void Merge_non_adjacent_fails()
    {
        var d = DataDisk();
        d = d.WithPartitions(d.Partitions.Append(Part(1, 3, 1 * MiB + 30 * GiB, 5 * GiB, letter: 'G')));
        Assert.Throws<OperationValidationException>(() => new MergePartitionsOperation(1, 1, 3).Apply(State(d)));
    }

    [Fact]
    public void Format_resets_free_space()
    {
        var s = new FormatPartitionOperation(1, 1, "exFAT", "X", 0, true).Apply(State(DataDisk()));
        var p = s.Disk(1).FindPartition(1)!;
        Assert.Equal("exFAT", p.FileSystem);
        Assert.Equal(p.SizeBytes, p.FreeBytes);
    }

    [Fact]
    public void Format_with_bad_cluster_size_fails() =>
        Assert.Throws<OperationValidationException>(() => new FormatPartitionOperation(1, 1, "NTFS", "", 1234, true).Apply(State(DataDisk())));

    [Fact]
    public void Change_letter_and_label()
    {
        var s = new ChangeDriveLetterOperation(1, 1, 'Q').Apply(State(DataDisk()));
        s = new ChangeLabelOperation(1, 1, "Archive").Apply(s);
        var p = s.Disk(1).FindPartition(1)!;
        Assert.Equal('Q', p.DriveLetter);
        Assert.Equal("Archive", p.Label);
    }

    [Fact]
    public void Fat32_label_too_long_fails()
    {
        var d = DataDisk().WithPartitions([Part(1, 1, 1 * MiB, 10 * GiB, "FAT32")]);
        Assert.Throws<OperationValidationException>(() => new ChangeLabelOperation(1, 1, "TWELVECHARSX").Apply(State(d)));
    }

    [Fact]
    public void Hide_then_show()
    {
        var s = new SetPartitionHiddenOperation(1, 1, true).Apply(State(DataDisk()));
        Assert.True(s.Disk(1).FindPartition(1)!.IsHidden);
        Assert.Null(s.Disk(1).FindPartition(1)!.DriveLetter);
        s = new SetPartitionHiddenOperation(1, 1, false, 'E').Apply(s);
        Assert.Equal('E', s.Disk(1).FindPartition(1)!.DriveLetter);
    }

    [Fact]
    public void Available_letters_exclude_used_and_external()
    {
        var s = State(DataDisk(), SystemDisk()) with { ExternallyUsedLetters = new HashSet<char> { 'Z' } };
        var letters = s.AvailableLetters;
        Assert.DoesNotContain('C', letters);
        Assert.DoesNotContain('E', letters);
        Assert.DoesNotContain('Z', letters);
        Assert.Contains('G', letters);
    }

    [Fact]
    public void Operation_on_missing_disk_fails() =>
        Assert.Throws<OperationValidationException>(() => new DeletePartitionOperation(42, 1).Apply(State(DataDisk())));
}
