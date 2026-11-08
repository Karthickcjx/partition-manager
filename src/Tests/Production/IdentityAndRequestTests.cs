using PartitionManager.Core.Execution;
using PartitionManager.Core.Operations;
using PartitionManager.Core.Safety;
using Xunit;
using static PartitionManager.Tests.Fixtures;
using static PartitionManager.Tests.Production.ExecutionFixtures;

namespace PartitionManager.Tests.Production;

public class DiskIdentityTests
{
    [Fact]
    public void Fingerprint_ignores_labels_and_letters()
    {
        var d = VirtualDisk();
        var relabelled = d.WithPartitions(d.Partitions.Select(p => p with { Label = "Other", DriveLetter = 'Z', FreeBytes = 1 }));
        Assert.Equal(LayoutFingerprints.Compute(d), LayoutFingerprints.Compute(relabelled));
    }

    [Fact]
    public void Fingerprint_changes_with_geometry_or_partition_guid()
    {
        var d = VirtualDisk();
        var fp = LayoutFingerprints.Compute(d);
        Assert.NotEqual(fp, LayoutFingerprints.Compute(d.WithPartitions(d.Partitions.Select(p => p with { SizeBytes = p.SizeBytes - MiB }))));
        Assert.NotEqual(fp, LayoutFingerprints.Compute(d.WithPartitions(d.Partitions.Select(p => p with { UniqueGuid = Guid.NewGuid() }))));
        Assert.NotEqual(fp, LayoutFingerprints.Compute(d.WithPartitions([])));
        Assert.Equal(64, fp.Length);
    }

    [Fact]
    public void Same_disk_matches()
    {
        var d = VirtualDisk();
        var m = DiskIdentityMatcher.Match(DiskIdentity.From(d), [PhysicalDisk(), d]);
        Assert.True(m.IsMatch);
        Assert.Same(d, m.Disk);
    }

    [Fact]
    public void Disconnected_disk_is_not_found() =>
        Assert.Equal(IdentityStatus.NotFound, DiskIdentityMatcher.Match(DiskIdentity.From(VirtualDisk()), [PhysicalDisk()]).Status);

    [Fact]
    public void Duplicate_ids_are_ambiguous()
    {
        var a = VirtualDisk(3);
        var b = VirtualDisk(4, @"D:\pm-testdisks\clone.vhdx");
        Assert.Equal(IdentityStatus.Ambiguous, DiskIdentityMatcher.Match(DiskIdentity.From(a), [a, b]).Status);
    }

    [Fact]
    public void Renumbered_disk_is_refused()
    {
        var d = VirtualDisk(3);
        var renumbered = d with { Number = 5 };
        Assert.Equal(IdentityStatus.DiskNumberChanged, DiskIdentityMatcher.Match(DiskIdentity.From(d), [renumbered]).Status);
    }

    [Fact]
    public void Changed_layout_is_refused()
    {
        var d = VirtualDisk();
        var changed = d.WithPartitions(d.Partitions.Select(p => p with { OffsetBytes = p.OffsetBytes + MiB }));
        Assert.Equal(IdentityStatus.LayoutChanged, DiskIdentityMatcher.Match(DiskIdentity.From(d), [changed]).Status);
    }

    [Fact]
    public void Resized_disk_is_refused()
    {
        var d = VirtualDisk();
        Assert.Equal(IdentityStatus.SizeChanged, DiskIdentityMatcher.Match(DiskIdentity.From(d), [d with { SizeBytes = d.SizeBytes + GiB }]).Status);
    }

    [Fact]
    public void Disk_without_stable_id_is_refused()
    {
        var d = VirtualDisk() with { UniqueId = "", SerialNumber = "" };
        Assert.Equal(IdentityStatus.InsufficientIdentity, DiskIdentityMatcher.Match(DiskIdentity.From(d), [d]).Status);
    }

    [Fact]
    public void Substituted_disk_with_same_number_is_not_matched()
    {
        // Attacker or hot-swap scenario: disk 3 is now a different device.
        var original = VirtualDisk(3);
        var substitute = VirtualDisk(3, @"D:\pm-testdisks\other.vhdx", uniqueId: "VHD-OTHER");
        Assert.Equal(IdentityStatus.NotFound, DiskIdentityMatcher.Match(DiskIdentity.From(original), [substitute]).Status);
    }

    [Fact]
    public void Serial_mismatch_under_same_unique_id_is_not_matched()
    {
        var d = PhysicalDisk();
        var forged = d with { SerialNumber = "DIFFERENT" };
        Assert.Equal(IdentityStatus.NotFound, DiskIdentityMatcher.Match(DiskIdentity.From(d), [forged]).Status);
    }
}

public class WriteRequestTests
{
    [Fact]
    public void Valid_requests_pass()
    {
        var d = VirtualDisk();
        Assert.True(WriteRequestValidator.Validate(LabelRequest(d)).IsValid);
        Assert.True(WriteRequestValidator.Validate(ChangeLetterRequest(d)).IsValid);
    }

    [Fact]
    public void Undefined_operation_kind_is_not_allowed() =>
        Assert.Equal("NOT_ALLOWED", WriteRequestValidator.Validate(LabelRequest(VirtualDisk()) with { Kind = (WriteOperationKind)99 }).Code);

    [Theory]
    [InlineData('A')]
    [InlineData('B')]
    [InlineData('1')]
    [InlineData('c')]
    public void Invalid_letters_are_rejected(char letter) =>
        Assert.Equal("BAD_LETTER", WriteRequestValidator.Validate(ChangeLetterRequest(VirtualDisk(), letter)).Code);

    [Fact]
    public void Smuggled_fields_are_rejected()
    {
        var d = VirtualDisk();
        Assert.Equal("UNEXPECTED_FIELD", WriteRequestValidator.Validate(LabelRequest(d) with { NewDriveLetter = 'X' }).Code);
        Assert.Equal("UNEXPECTED_FIELD", WriteRequestValidator.Validate(ChangeLetterRequest(d) with { NewLabel = "x" }).Code);
    }

    [Fact]
    public void Geometry_overflow_is_rejected()
    {
        var r = LabelRequest(VirtualDisk());
        Assert.Equal("BAD_PARTITION", WriteRequestValidator.Validate(r with { Partition = r.Partition! with { OffsetBytes = long.MaxValue - 10, SizeBytes = 100 } }).Code);
        Assert.Equal("BAD_PARTITION", WriteRequestValidator.Validate(r with { Partition = r.Partition! with { SizeBytes = -1 } }).Code);
    }

    [Fact]
    public void Malformed_fingerprint_is_rejected() =>
        Assert.Equal("BAD_FINGERPRINT", WriteRequestValidator.Validate(LabelRequest(VirtualDisk()) with { Disk = DiskIdentityFor() with { LayoutFingerprint = "zz" } }).Code);

    private static DiskIdentity DiskIdentityFor() => DiskIdentity.From(VirtualDisk());

    [Fact]
    public void Control_characters_in_label_are_rejected() =>
        Assert.Equal("BAD_LABEL", WriteRequestValidator.Validate(LabelRequest(VirtualDisk(), "bad\u0007label")).Code);

    [Fact]
    public void Factory_maps_letter_changes_to_the_right_kind()
    {
        var d = VirtualDisk();
        var s = State(d);
        Assert.Equal(WriteOperationKind.ChangeDriveLetter, WriteRequestFactory.TryCreate(new ChangeDriveLetterOperation(3, 2, 'R'), s)!.Kind);
        Assert.Equal(WriteOperationKind.RemoveDriveLetter, WriteRequestFactory.TryCreate(new ChangeDriveLetterOperation(3, 2, null), s)!.Kind);
        var noLetter = d.WithPartitions(d.Partitions.Select(p => p with { DriveLetter = null }));
        Assert.Equal(WriteOperationKind.AssignDriveLetter, WriteRequestFactory.TryCreate(new ChangeDriveLetterOperation(3, 2, 'r'), State(noLetter))!.Kind);
        Assert.Equal('R', WriteRequestFactory.TryCreate(new ChangeDriveLetterOperation(3, 2, 'r'), State(noLetter))!.NewDriveLetter);
    }

    [Fact]
    public void Factory_refuses_non_allowlisted_operations()
    {
        // Updated for 0.4: move, merge and initialize are allowlisted too. These still are not.
        var d = VirtualDisk();
        var s = State(d);
        var p = d.Partitions[0];
        Assert.Null(WriteRequestFactory.TryCreate(new ResizeMovePartitionOperation(3, 2, p.OffsetBytes + MiB, p.SizeBytes - MiB), s)); // move + resize at once
        Assert.Null(WriteRequestFactory.TryCreate(new DeleteAllPartitionsOperation(3), s));
        Assert.Null(WriteRequestFactory.TryCreate(new SetPartitionHiddenOperation(3, 2, true), s));
        Assert.Null(WriteRequestFactory.TryCreate(new ChangeLabelOperation(42, 2, "x"), s));
    }
}

public class HelperPolicyTests
{
    [Theory]
    [InlineData(@"D:\pm-testdisks\disk1.vhdx", true)]
    [InlineData(@"D:\pm-testdisks\sub\disk.VHD", true)]
    [InlineData(@"d:\PM-TESTDISKS\disk.vhdx", true)]
    [InlineData(@"D:\pm-testdisks-evil\disk.vhdx", false)]
    [InlineData(@"D:\pm-testdisks\..\Windows\disk.vhdx", false)]
    [InlineData(@"D:\pm-testdisks\disk.iso", false)]
    [InlineData(@"\\server\share\pm-testdisks\disk.vhdx", false)]
    [InlineData(@"\\?\D:\pm-testdisks\disk.vhdx", false)]
    [InlineData(@"C:\disk.vhdx", false)]
    [InlineData(@"pm-testdisks\disk.vhdx", false)]
    [InlineData("", false)]
    public void Image_paths_are_confined_to_approved_folders(string path, bool allowed) =>
        Assert.Equal(allowed, VirtualOnly.AllowsImage(path));

    [Fact]
    public void Disabled_policy_allows_nothing() => Assert.False(HelperPolicy.Disabled.AllowsImage(ImagePath));

    [Fact]
    public void Whole_drive_is_not_an_acceptable_root() =>
        Assert.False(new HelperPolicy(WriteMode.VirtualDisksOnly, [@"D:\"]).AllowsImage(@"D:\disk.vhdx"));

    [Fact]
    public void Virtual_image_path_only_for_virtual_disks()
    {
        Assert.Equal(ImagePath, VirtualDisk().VirtualImagePath);
        Assert.Equal("", (VirtualDisk() with { IsVirtual = false }).VirtualImagePath);
        Assert.Equal("", (VirtualDisk() with { Location = "PCI Slot 3" }).VirtualImagePath);
    }
}

public class EligibilityTests
{
    private static IReadOnlyList<QueuedOperation> Queue(PreviewState s, params DiskOperation[] ops)
    {
        var q = new OperationQueue(s, new SafetyPolicy());
        foreach (var op in ops) Assert.True(q.TryAdd(op).Success);
        return q.Items;
    }

    [Fact]
    public void Metadata_change_on_approved_virtual_disk_is_eligible()
    {
        var s = State(VirtualDisk());
        var e = RealExecutionEligibility.Evaluate(Queue(s, new ChangeLabelOperation(3, 2, "A"), new ChangeDriveLetterOperation(3, 2, 'R')), s, VirtualOnly, true);
        Assert.True(e.IsEligible, e.Reason);
    }

    [Fact]
    public void Physical_disk_is_not_writable_under_the_virtual_disks_policy_but_is_under_all_disks()
    {
        var s = State(PhysicalDisk());
        var e = RealExecutionEligibility.Evaluate(Queue(s, new ChangeLabelOperation(1, 1, "A")), s, VirtualOnly, true);
        Assert.False(e.IsEligible);
        Assert.Contains("not writable under the current policy", e.Reason);
        var all = RealExecutionEligibility.Evaluate(Queue(s, new ChangeLabelOperation(1, 1, "A")), s, VirtualOnly with { Mode = WriteMode.AllDisks }, true);
        Assert.True(all.IsEligible, all.Reason);
    }

    [Fact]
    public void Mixed_queue_is_simulated_entirely()
    {
        var s = State(VirtualDisk(), PhysicalDisk());
        var e = RealExecutionEligibility.Evaluate(Queue(s, new ChangeLabelOperation(3, 2, "A"), new ChangeLabelOperation(1, 1, "B")), s, VirtualOnly, true);
        Assert.False(e.IsEligible);
    }

    [Fact]
    public void Structural_operation_is_eligible_only_as_the_last_task_on_its_disk()
    {
        // Updated for 0.3: a single structural change on an approved virtual disk is eligible...
        var s = State(VirtualDisk());
        Assert.True(RealExecutionEligibility.Evaluate(Queue(s, new DeletePartitionOperation(3, 2)), s, VirtualOnly, true).IsEligible);
        // ...but nothing may follow it on the same disk (Windows renumbers partitions unpredictably).
        var e = RealExecutionEligibility.Evaluate(Queue(s, new ChangeLabelOperation(3, 2, "A"), new SplitPartitionOperation(3, 2, 2 * GiB, "NTFS", "", null),
            new ChangeLabelOperation(3, 2, "B")), s, VirtualOnly, true);
        Assert.False(e.IsEligible);
        Assert.Contains("follows a structural change", e.Reason);
    }

    [Fact]
    public void Move_on_an_approved_virtual_disk_is_eligible_but_merge_is_not()
    {
        var d = VirtualDisk();
        var s = State(d);
        var p = d.Partitions[0];
        var move = RealExecutionEligibility.Evaluate(Queue(s, new ResizeMovePartitionOperation(3, 2, p.OffsetBytes + MiB, p.SizeBytes)), s, VirtualOnly, true);
        Assert.True(move.IsEligible, move.Reason);
        // Move combined with a size change is not a single block move and stays simulated.
        Assert.False(RealExecutionEligibility.Evaluate(Queue(s, new ResizeMovePartitionOperation(3, 2, p.OffsetBytes + MiB, p.SizeBytes - MiB)), s, VirtualOnly, true).IsEligible);
    }

    [Theory]
    [InlineData("RAW")]   // ReFS moves cannot even be queued (the simulator refuses them)
    public void Move_the_helper_would_refuse_is_shown_as_simulated_with_the_reason(string fs)
    {
        var d = VirtualDisk();
        d = d.WithPartitions(d.Partitions.Select(x => x with { FileSystem = fs == "RAW" ? "" : fs }));
        var s = State(d);
        var p = d.Partitions[0];
        var e = RealExecutionEligibility.Evaluate(Queue(s, new ResizeMovePartitionOperation(3, 2, p.OffsetBytes + MiB, p.SizeBytes)), s, VirtualOnly, true);
        Assert.False(e.IsEligible, e.Reason);
        if (fs == "RAW") Assert.Contains("cannot run", e.Reason);   // ReFS is refused earlier, by the request factory
    }

    [Fact]
    public void Move_on_a_physical_disk_is_never_eligible()
    {
        var d = VirtualDisk() with { IsVirtual = false, BusType = "NVMe" };
        var s = State(d);
        var p = d.Partitions[0];
        Assert.False(RealExecutionEligibility.Evaluate(Queue(s, new ResizeMovePartitionOperation(3, 2, p.OffsetBytes + MiB, p.SizeBytes)), s, VirtualOnly, true).IsEligible);
    }

    [Fact]
    public void Disabled_policy_or_missing_helper_is_not_eligible()
    {
        var s = State(VirtualDisk());
        var q = Queue(s, new ChangeLabelOperation(3, 2, "A"));
        Assert.False(RealExecutionEligibility.Evaluate(q, s, HelperPolicy.Disabled, true).IsEligible);
        Assert.False(RealExecutionEligibility.Evaluate(q, s, VirtualOnly, false).IsEligible);
    }

    [Fact]
    public void Image_outside_approved_folder_is_not_eligible()
    {
        var s = State(VirtualDisk(image: @"E:\elsewhere\disk.vhdx"));
        Assert.False(RealExecutionEligibility.Evaluate(Queue(s, new ChangeLabelOperation(3, 2, "A")), s, VirtualOnly, true).IsEligible);
    }
}
