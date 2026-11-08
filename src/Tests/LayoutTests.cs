using PartitionManager.Core.Layout;
using PartitionManager.Core.Models;
using Xunit;
using static PartitionManager.Tests.Fixtures;

namespace PartitionManager.Tests;

public class LayoutTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(512110190592L, "476.94 GB")]
    [InlineData(1099511627776L, "1 TB")]
    public void ByteSize_formats_binary_units(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));

    [Theory]
    [InlineData("512", 512L)]
    [InlineData("1 KB", 1024L)]
    [InlineData("1.5GB", 1610612736L)]
    [InlineData("100 MiB", 104857600L)]
    [InlineData("2t", 2199023255552L)]
    public void ByteSize_parses(string text, long expected)
    {
        Assert.True(ByteSize.TryParse(text, out var v));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5 GB")]
    [InlineData("5 XB")]
    public void ByteSize_rejects_garbage(string text) => Assert.False(ByteSize.TryParse(text, out _));

    [Fact]
    public void Segments_include_trailing_unallocated_space()
    {
        var disk = DataDisk();
        var segs = DiskLayout.Segments(disk);
        Assert.Equal(3, segs.Count);
        Assert.True(segs[2].IsUnallocated);
        Assert.Equal(1 * MiB + 30 * GiB, segs[2].OffsetBytes);
        Assert.Equal(DiskLayout.UsableEnd(disk), segs[2].EndBytes);
        Assert.Equal(segs.Sum(s => s.SizeBytes), DiskLayout.UsableEnd(disk) - DiskLayout.UsableStart(disk));
    }

    [Fact]
    public void Segments_ignore_sub_megabyte_gaps()
    {
        var d = new DiskInfo { Number = 9, Model = "x", SizeBytes = 2 * GiB, Style = PartitionStyle.Mbr };
        d = d.WithPartitions([Part(9, 1, 1 * MiB, 1 * GiB), Part(9, 2, 1 * MiB + 1 * GiB + 4096, 1 * GiB - 2 * MiB)]);
        Assert.DoesNotContain(DiskLayout.Segments(d).Take(2), s => s.IsUnallocated);
    }

    [Fact]
    public void Raw_disk_is_one_unallocated_segment()
    {
        var d = new DiskInfo { Number = 3, Model = "blank", SizeBytes = 8 * GiB, Style = PartitionStyle.Raw };
        var seg = Assert.Single(DiskLayout.Segments(d));
        Assert.True(seg.IsUnallocated);
        Assert.Equal(8 * GiB, seg.SizeBytes);
    }

    [Fact]
    public void Movable_range_is_bounded_by_neighbours()
    {
        var disk = DataDisk();
        var (min, max) = DiskLayout.MovableRange(disk, disk.FindPartition(2)!);
        Assert.Equal(1 * MiB + 10 * GiB, min);
        Assert.Equal(DiskLayout.UsableEnd(disk), max);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1048576L)]
    [InlineData(1048576L, 1048576L)]
    [InlineData(1048577L, 2097152L)]
    public void AlignUp_rounds_to_megabyte(long input, long expected) => Assert.Equal(expected, DiskLayout.AlignUp(input));

    [Fact]
    public void Valid_layout_has_no_errors() => Assert.True(LayoutValidator.IsValid(DataDisk()));

    [Fact]
    public void Overlap_is_an_error()
    {
        var d = DataDisk().WithPartitions([Part(1, 1, 1 * MiB, 10 * GiB), Part(1, 2, 5 * GiB, 10 * GiB)]);
        Assert.Contains(LayoutValidator.Validate(d), i => i.Code == "OVERLAP" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Out_of_bounds_is_an_error()
    {
        var d = DataDisk().WithPartitions([Part(1, 1, 1 * MiB, 64 * GiB)]);
        var codes = LayoutValidator.Validate(d).Select(i => i.Code).ToList();
        Assert.Contains("OUT_OF_BOUNDS", codes);
        Assert.Contains("BACKUP_GPT_OVERLAP", codes);
    }

    [Fact]
    public void Legacy_63_sector_alignment_is_a_warning()
    {
        var d = new DiskInfo { Number = 4, Model = "old", SizeBytes = 8 * GiB, Style = PartitionStyle.Mbr };
        d = d.WithPartitions([Part(4, 1, 63 * 512, 1 * GiB)]);
        var issue = Assert.Single(LayoutValidator.Validate(d));
        Assert.Equal("NOT_1MIB_ALIGNED", issue.Code);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void Mbr_five_primaries_is_an_error()
    {
        var d = new DiskInfo { Number = 5, Model = "m", SizeBytes = 16 * GiB, Style = PartitionStyle.Mbr };
        d = d.WithPartitions(Enumerable.Range(0, 5).Select(i => Part(5, i + 1, (1 + i * 1024L) * MiB, 1 * GiB)));
        Assert.Contains(LayoutValidator.Validate(d), i => i.Code == "MBR_TOO_MANY_PRIMARY");
    }

    [Fact]
    public void Mbr_over_2tb_warns()
    {
        var d = new DiskInfo { Number = 6, Model = "big", SizeBytes = 4 * ByteSize.TiB, Style = PartitionStyle.Mbr };
        Assert.Contains(LayoutValidator.Validate(d), i => i.Code == "MBR_OVER_2TB");
    }
}
