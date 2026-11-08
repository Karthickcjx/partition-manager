using System.Windows;
using PartitionManager.Core.FileSystems;
using PartitionManager.Core.Layout;
using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;

namespace PartitionManager.Desktop.ViewModels.Dialogs;

internal static class DialogHelpers
{
    public const long MiB = ByteSize.MiB;

    public static List<LetterOption> Letters(PreviewState state, char? current = null, bool includeNone = true)
    {
        var list = new List<LetterOption>();
        if (includeNone) list.Add(new LetterOption(null));
        var letters = state.AvailableLetters.ToList();
        if (current is { } c && !letters.Contains(c)) letters.Add(c);
        list.AddRange(letters.OrderBy(x => x).Select(x => new LetterOption(x)));
        return list;
    }

    public static string Mb(double mib) => ByteSize.Format((long)Math.Round(mib) * MiB);

    public static long Whole(double mib, string field)
    {
        if (double.IsNaN(mib) || mib < 0) throw new FormatException($"{field} must be a positive number of MB.");
        return (long)Math.Round(mib);
    }
}

public sealed class CreatePartitionDialogViewModel : OperationDialogViewModel
{
    private readonly int _disk;
    private readonly long _start;
    private readonly long _end;
    private double _sizeMiB;
    private double _frontMiB;
    private string _fileSystem = FileSystemRules.Ntfs;
    private string _label = "New Volume";
    private LetterOption? _letter;

    public CreatePartitionDialogViewModel(DiskInfo disk, LayoutSegment free, PreviewState state, Func<DiskOperation, QueueResult> submit) : base(submit)
    {
        _disk = disk.Number;
        _start = DiskLayout.AlignUp(free.OffsetBytes);
        _end = free.EndBytes;
        MaxMiB = Math.Max(0, (_end - _start) / DialogHelpers.MiB);
        _sizeMiB = MaxMiB;
        Letters = DialogHelpers.Letters(state);
        _letter = Letters.Skip(1).FirstOrDefault() ?? Letters[0];
        DiskText = $"{disk.Title} · {disk.Model} · {ByteSize.Format(disk.SizeBytes)}";
    }

    public override string Title => "Create partition";
    public string DiskText { get; }
    public double MaxMiB { get; }
    public string FreeText => $"Unallocated space: {DialogHelpers.Mb(MaxMiB)}";
    public IReadOnlyList<string> FileSystems => FileSystemRules.Formattable;
    public IReadOnlyList<LetterOption> Letters { get; }

    public double SizeMiB
    {
        get => _sizeMiB;
        set { if (Set(ref _sizeMiB, Math.Clamp(Math.Round(value), 1, MaxMiB))) { ClampFront(); Raise(nameof(SizeText)); ClearError(); } }
    }

    public double FrontMiB
    {
        get => _frontMiB;
        set { if (Set(ref _frontMiB, Math.Clamp(Math.Round(value), 0, MaxMiB - _sizeMiB))) ClearError(); }
    }

    public double MaxFrontMiB => Math.Max(0, MaxMiB - _sizeMiB);
    public string SizeText => DialogHelpers.Mb(_sizeMiB);

    public string FileSystem { get => _fileSystem; set { if (Set(ref _fileSystem, value)) ClearError(); } }
    public string Label { get => _label; set { if (Set(ref _label, value)) ClearError(); } }
    public LetterOption? Letter { get => _letter; set => Set(ref _letter, value); }

    private void ClampFront()
    {
        Raise(nameof(MaxFrontMiB));
        if (_frontMiB > MaxFrontMiB) FrontMiB = MaxFrontMiB;
    }

    protected override DiskOperation Build()
    {
        var size = DialogHelpers.Whole(SizeMiB, "Size") * DialogHelpers.MiB;
        var offset = _start + DialogHelpers.Whole(FrontMiB, "Space before") * DialogHelpers.MiB;
        if (offset + size > _end) throw new FormatException("The partition does not fit in the selected unallocated space.");
        return new CreatePartitionOperation(_disk, offset, size, FileSystem, Label.Trim(), Letter?.Letter);
    }
}

public sealed class ResizePartitionDialogViewModel : OperationDialogViewModel
{
    private readonly PartitionInfo _p;
    private readonly long _min;
    private readonly long _max;
    private readonly double _initialSizeMiB;
    private double _front;
    private double _size;

    public ResizePartitionDialogViewModel(DiskInfo disk, PartitionInfo partition, Func<DiskOperation, QueueResult> submit) : base(submit)
    {
        _p = partition;
        (_min, _max) = DiskLayout.MovableRange(disk, partition);
        TotalMiB = Math.Floor((_max - _min) / (double)DialogHelpers.MiB);
        _front = Math.Max(0, Math.Floor((partition.OffsetBytes - _min) / (double)DialogHelpers.MiB));
        _size = Math.Min(TotalMiB - _front, Math.Floor(partition.SizeBytes / (double)DialogHelpers.MiB));
        _initialSizeMiB = _size;

        var shrinkable = FileSystemRules.CanShrink(partition.FileSystem);
        MinSizeMiB = shrinkable
            ? Math.Ceiling(((partition.UsedBytes ?? 0) + (partition.HasFileSystem ? FileSystemRules.ShrinkHeadroomBytes : DialogHelpers.MiB)) / (double)DialogHelpers.MiB)
            : _size;
        MinSizeMiB = Math.Min(MinSizeMiB, _size);
        CanGrow = FileSystemRules.CanExtend(partition.FileSystem);
        CanMove = FileSystemRules.CanRelocateData(partition.FileSystem) && !(partition.IsBoot || partition.IsSystem);
        PartitionText = $"{partition.DisplayName} · {partition.FileSystem} · {ByteSize.Format(partition.SizeBytes)} on {disk.Title}";
        UsedText = partition.UsedBytes is { } u ? $"Used: {ByteSize.Format(u)}" : "Used: unknown";
    }

    public override string Title => "Resize/Move partition";
    public string PartitionText { get; }
    public string UsedText { get; }
    public double TotalMiB { get; }
    public double MinSizeMiB { get; }
    public double MaxSizeMiB => CanGrow ? TotalMiB - (CanMove ? 0 : _front) : _initialSizeMiB;
    public bool CanGrow { get; }
    public bool CanMove { get; }

    public double FrontMiB
    {
        get => _front;
        set
        {
            if (!CanMove) return;
            var v = Math.Clamp(Math.Round(value), 0, TotalMiB - _size);
            if (Set(ref _front, v)) Changed();
        }
    }

    public double SizeMiB
    {
        get => _size;
        set
        {
            var v = Math.Clamp(Math.Round(value), MinSizeMiB, Math.Min(MaxSizeMiB, TotalMiB - _front));
            if (Set(ref _size, v)) Changed();
        }
    }

    public double RearMiB
    {
        get => TotalMiB - _front - _size;
        set
        {
            var rear = Math.Clamp(Math.Round(value), 0, TotalMiB - _front - MinSizeMiB);
            SizeMiB = TotalMiB - _front - rear;
        }
    }

    public GridLength FrontColumn => new(Math.Max(_front, 0), GridUnitType.Star);
    public GridLength SizeColumn => new(Math.Max(_size, 1), GridUnitType.Star);
    public GridLength RearColumn => new(Math.Max(RearMiB, 0), GridUnitType.Star);

    public string FrontText => DialogHelpers.Mb(_front);
    public string SizeText => DialogHelpers.Mb(_size);
    public string RearText => DialogHelpers.Mb(RearMiB);

    private void Changed()
    {
        ClearError();
        foreach (var n in new[] { nameof(FrontMiB), nameof(SizeMiB), nameof(RearMiB), nameof(FrontColumn), nameof(SizeColumn), nameof(RearColumn),
                     nameof(FrontText), nameof(SizeText), nameof(RearText) })
            Raise(n);
    }

    protected override DiskOperation Build()
    {
        var offset = _min + DialogHelpers.Whole(_front, "Space before") * DialogHelpers.MiB;
        var unchangedOffset = Math.Floor((_p.OffsetBytes - _min) / (double)DialogHelpers.MiB) == _front;
        if (unchangedOffset) offset = _p.OffsetBytes;
        var size = _size == _initialSizeMiB ? _p.SizeBytes : DialogHelpers.Whole(_size, "Partition size") * DialogHelpers.MiB;
        if (offset + size > _max) size = DiskLayout.AlignDown(_max - offset);
        return new ResizeMovePartitionOperation(_p.DiskNumber, _p.PartitionNumber, offset, size);
    }
}

public sealed class SplitPartitionDialogViewModel : OperationDialogViewModel
{
    private readonly PartitionInfo _p;
    private double _first;
    private string _fileSystem = FileSystemRules.Ntfs;
    private string _label = "New Volume";
    private LetterOption? _letter;

    public SplitPartitionDialogViewModel(DiskInfo disk, PartitionInfo partition, PreviewState state, Func<DiskOperation, QueueResult> submit) : base(submit)
    {
        _p = partition;
        TotalMiB = Math.Floor(partition.SizeBytes / (double)DialogHelpers.MiB);
        MinFirstMiB = Math.Min(TotalMiB - 1, Math.Ceiling(((partition.UsedBytes ?? 0) + FileSystemRules.ShrinkHeadroomBytes) / (double)DialogHelpers.MiB));
        MaxFirstMiB = TotalMiB - 1;
        _first = Math.Clamp(Math.Round(TotalMiB / 2), MinFirstMiB, MaxFirstMiB);
        Letters = DialogHelpers.Letters(state);
        _letter = Letters.Skip(1).FirstOrDefault() ?? Letters[0];
        PartitionText = $"{partition.DisplayName} · {ByteSize.Format(partition.SizeBytes)} on {disk.Title}";
    }

    public override string Title => "Split partition";
    public string PartitionText { get; }
    public double TotalMiB { get; }
    public double MinFirstMiB { get; }
    public double MaxFirstMiB { get; }
    public IReadOnlyList<string> FileSystems => FileSystemRules.Formattable;
    public IReadOnlyList<LetterOption> Letters { get; }

    public double FirstMiB
    {
        get => _first;
        set
        {
            if (Set(ref _first, Math.Clamp(Math.Round(value), MinFirstMiB, MaxFirstMiB)))
            {
                ClearError();
                Raise(nameof(SecondMiB)); Raise(nameof(FirstText)); Raise(nameof(SecondText)); Raise(nameof(FirstColumn)); Raise(nameof(SecondColumn));
            }
        }
    }

    public double SecondMiB => TotalMiB - _first;
    public string FirstText => DialogHelpers.Mb(_first);
    public string SecondText => DialogHelpers.Mb(SecondMiB);
    public GridLength FirstColumn => new(_first, GridUnitType.Star);
    public GridLength SecondColumn => new(Math.Max(SecondMiB, 0), GridUnitType.Star);

    public string FileSystem { get => _fileSystem; set { if (Set(ref _fileSystem, value)) ClearError(); } }
    public string Label { get => _label; set { if (Set(ref _label, value)) ClearError(); } }
    public LetterOption? Letter { get => _letter; set => Set(ref _letter, value); }

    protected override DiskOperation Build() =>
        new SplitPartitionOperation(_p.DiskNumber, _p.PartitionNumber, DialogHelpers.Whole(_first, "First part") * DialogHelpers.MiB,
            FileSystem, Label.Trim(), Letter?.Letter);
}

public sealed class MergePartitionsDialogViewModel : OperationDialogViewModel
{
    private readonly PartitionInfo _target;
    private ChoiceOption<PartitionInfo>? _source;

    public MergePartitionsDialogViewModel(DiskInfo disk, PartitionInfo target, Func<DiskOperation, QueueResult> submit) : base(submit)
    {
        _target = target;
        Candidates = new[] { DiskLayout.NeighbourBefore(disk, target), DiskLayout.NeighbourAfter(disk, target) }
            .OfType<PartitionInfo>()
            .Where(p => !p.IsProtectedRole && !p.IsExtendedContainer)
            .Select(p => new ChoiceOption<PartitionInfo>(p, $"{p.DisplayName} · {p.FileSystem} · {ByteSize.Format(p.SizeBytes)} · used {ByteSize.Format(p.UsedBytes)}"))
            .ToList();
        _source = Candidates.FirstOrDefault();
        TargetText = $"{target.DisplayName} · {ByteSize.Format(target.SizeBytes)}";
    }

    public override string Title => "Merge partitions";
    public string TargetText { get; }
    public IReadOnlyList<ChoiceOption<PartitionInfo>> Candidates { get; }
    public bool HasCandidates => Candidates.Count > 0;

    public ChoiceOption<PartitionInfo>? Source
    {
        get => _source;
        set { if (Set(ref _source, value)) { ClearError(); Raise(nameof(FolderNote)); } }
    }

    public string FolderNote => _source is null ? "" :
        $"Files from {_source.Value.DisplayName} will be moved into a folder on {_target.DisplayName}, then {_source.Value.DisplayName} is removed.";

    protected override DiskOperation Build()
    {
        if (_source is null) throw new FormatException("There is no adjacent partition that can be merged.");
        return new MergePartitionsOperation(_target.DiskNumber, _target.PartitionNumber, _source.Value.PartitionNumber);
    }
}

public sealed class FormatPartitionDialogViewModel : OperationDialogViewModel
{
    private readonly PartitionInfo _p;
    private string _fileSystem;
    private string _label;
    private ChoiceOption<int> _cluster;
    private bool _quick = true;

    public FormatPartitionDialogViewModel(DiskInfo disk, PartitionInfo partition, Func<DiskOperation, QueueResult> submit) : base(submit)
    {
        _p = partition;
        _fileSystem = FileSystemRules.IsFormattable(partition.FileSystem) ? FileSystemRules.Normalize(partition.FileSystem) : FileSystemRules.Ntfs;
        _label = partition.Label;
        Clusters = BuildClusters(_fileSystem);
        _cluster = Clusters[0];
        PartitionText = $"{partition.DisplayName} · {ByteSize.Format(partition.SizeBytes)} on {disk.Title}";
    }

    public override string Title => "Format partition";
    public override bool IsPrimaryDanger => true;
    public string PartitionText { get; }
    public IReadOnlyList<string> FileSystems => FileSystemRules.Formattable;
    public IReadOnlyList<ChoiceOption<int>> Clusters { get; private set; }

    public string FileSystem
    {
        get => _fileSystem;
        set
        {
            if (!Set(ref _fileSystem, value)) return;
            Clusters = BuildClusters(value);
            Raise(nameof(Clusters));
            Cluster = Clusters[0];
            ClearError();
        }
    }

    public string Label { get => _label; set { if (Set(ref _label, value)) ClearError(); } }
    public ChoiceOption<int> Cluster { get => _cluster; set => Set(ref _cluster, value); }
    public bool QuickFormat { get => _quick; set => Set(ref _quick, value); }

    private static List<ChoiceOption<int>> BuildClusters(string fs) =>
        FileSystemRules.ClusterSizes(fs).Select(c => new ChoiceOption<int>(c, c == 0 ? "Default" : ByteSize.Format(c))).ToList();

    protected override DiskOperation Build() =>
        new FormatPartitionOperation(_p.DiskNumber, _p.PartitionNumber, FileSystem, Label.Trim(), Cluster?.Value ?? 0, QuickFormat);
}

public sealed class ChangeLetterDialogViewModel : OperationDialogViewModel
{
    private readonly PartitionInfo _p;
    private LetterOption? _letter;

    public ChangeLetterDialogViewModel(PartitionInfo partition, PreviewState state, Func<DiskOperation, QueueResult> submit) : base(submit)
    {
        _p = partition;
        Letters = DialogHelpers.Letters(state, partition.DriveLetter);
        _letter = Letters.FirstOrDefault(l => l.Letter == partition.DriveLetter) ?? Letters[0];
        CurrentText = partition.DriveLetter is { } c ? $"{c}:" : "None";
    }

    public override string Title => "Change drive letter";
    public string CurrentText { get; }
    public IReadOnlyList<LetterOption> Letters { get; }
    public LetterOption? Letter { get => _letter; set { if (Set(ref _letter, value)) ClearError(); } }

    protected override DiskOperation Build() => new ChangeDriveLetterOperation(_p.DiskNumber, _p.PartitionNumber, Letter?.Letter);
}

public sealed class ChangeLabelDialogViewModel : OperationDialogViewModel
{
    private readonly PartitionInfo _p;
    private string _label;

    public ChangeLabelDialogViewModel(PartitionInfo partition, Func<DiskOperation, QueueResult> submit) : base(submit)
    {
        _p = partition;
        _label = partition.Label;
        MaxLength = FileSystemRules.MaxLabelLength(partition.FileSystem);
    }

    public override string Title => "Change volume label";
    public int MaxLength { get; }
    public string Hint => $"{_p.FileSystem} labels can be up to {MaxLength} characters.";
    public string Label { get => _label; set { if (Set(ref _label, value)) ClearError(); } }

    protected override DiskOperation Build() => new ChangeLabelOperation(_p.DiskNumber, _p.PartitionNumber, Label.Trim());
}

public sealed class InitializeDiskDialogViewModel : OperationDialogViewModel
{
    private readonly DiskInfo _disk;
    private bool _gpt = true;

    public InitializeDiskDialogViewModel(DiskInfo disk, Func<DiskOperation, QueueResult> submit) : base(submit)
    {
        _disk = disk;
        DiskText = $"{disk.Title} · {disk.Model} · {ByteSize.Format(disk.SizeBytes)}";
    }

    public override string Title => $"Initialize {_disk.Title}";
    public string DiskText { get; }
    public bool IsGpt { get => _gpt; set { if (Set(ref _gpt, value)) Raise(nameof(IsMbr)); } }
    public bool IsMbr { get => !_gpt; set => IsGpt = !value; }
    public bool LargeDisk => _disk.SizeBytes > LayoutValidator.MbrMaxAddressableBytes;

    protected override DiskOperation Build() => new InitializeDiskOperation(_disk.Number, IsGpt ? PartitionStyle.Gpt : PartitionStyle.Mbr);
}
