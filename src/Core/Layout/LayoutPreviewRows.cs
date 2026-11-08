using PartitionManager.Core.Models;

namespace PartitionManager.Core.Layout;

public static class LayoutPreviewRows
{
    public static IReadOnlyList<string> Describe(DiskInfo before,DiskInfo after)
    {
        var rows=new List<string> { $"Disk {before.Number}: complete before/after layout (byte offsets; end exclusive)" };
        foreach(var (title,disk) in new[] {("BEFORE",before),("AFTER — proposed",after)})
        {
            rows.Add(title);
            foreach(var segment in DiskLayout.Segments(disk))
            {
                var p=segment.Partition;
                rows.Add(p is null ? $"Unallocated [{segment.OffsetBytes:N0}, {segment.EndBytes:N0})" :
                    $"#{p.PartitionNumber} {p.DisplayName} · [{p.OffsetBytes:N0}, {p.EndBytes:N0}) · {p.SizeBytes:N0} bytes · {p.FileSystem} · GUID {p.UniqueGuid?.ToString() ?? "MBR"}"+
                    (p.IsProtectedRole || p.IsBoot || p.IsSystem ? " · PROTECTED" : ""));
            }
        }
        return rows;
    }
}
