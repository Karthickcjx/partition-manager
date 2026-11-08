using System.Management;

namespace PartitionManager.PrivilegedHelper.Engine;

/// <summary>
/// Writes through the documented Windows Storage Management API (root\Microsoft\Windows\Storage):
/// MSFT_Volume.SetFileSystemLabel / Format, MSFT_Partition.AddAccessPath / RemoveAccessPath / GetSupportedSize /
/// Resize / DeleteObject, MSFT_Disk.CreatePartition. These are the same calls Disk Management and the Storage
/// PowerShell cmdlets use; the partition table is never edited directly.
/// </summary>
public sealed class WmiStorageWriteBackend : IStorageWriteBackend
{
    public IDisposable PinBackingImage(PartitionManager.Core.Models.DiskInfo disk) => BackingImageLease.Open(disk);
    private static readonly ManagementScope Scope = new(@"root\Microsoft\Windows\Storage");

    public void SetVolumeLabel(int diskNumber, int partitionNumber, string label)
    {
        using var volume = GetVolume(diskNumber, partitionNumber, TimeSpan.Zero);
        Invoke(volume, "SetFileSystemLabel", ("FileSystemLabel", label));
    }

    public void AddDriveLetter(int diskNumber, int partitionNumber, char letter)
    {
        using var partition = GetPartition(diskNumber, partitionNumber);
        // AccessPath and AssignDriveLetter are mutually exclusive (status 5, invalid parameter, if both are passed).
        Invoke(partition, "AddAccessPath", ("AccessPath", $"{char.ToUpperInvariant(letter)}:\\"));
    }

    public void RemoveDriveLetter(int diskNumber, int partitionNumber, char letter)
    {
        using var partition = GetPartition(diskNumber, partitionNumber);
        Invoke(partition, "RemoveAccessPath", ("AccessPath", $"{char.ToUpperInvariant(letter)}:\\"));
    }

    public (long Min, long Max) GetSupportedSize(int diskNumber, int partitionNumber)
    {
        using var partition = GetPartition(diskNumber, partitionNumber);
        using var result = Invoke(partition, "GetSupportedSize");
        return ((long)Convert.ToUInt64(result["SizeMin"]), (long)Convert.ToUInt64(result["SizeMax"]));
    }

    public void Resize(int diskNumber, int partitionNumber, long sizeBytes)
    {
        using var partition = GetPartition(diskNumber, partitionNumber);
        using var _ = Invoke(partition, "Resize", ("Size", (ulong)sizeBytes));
    }

    public int CreatePartition(int diskNumber, long offsetBytes, long sizeBytes)
    {
        using var disk = GetSingle($"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}", $"disk {diskNumber}");
        using var result = Invoke(disk, "CreatePartition", ("Offset", (ulong)offsetBytes), ("Size", (ulong)sizeBytes), ("AssignDriveLetter", false));
        var created = result["CreatedPartition"] as ManagementBaseObject
                      ?? throw new StorageWriteException("CreatePartition did not return the new partition.");
        return Convert.ToInt32(created["PartitionNumber"]);
    }

    public void Format(int diskNumber, int partitionNumber, string fileSystem, string label, int clusterSize, bool full)
    {
        // A freshly created partition's volume can take a moment to appear.
        using var volume = GetVolume(diskNumber, partitionNumber, TimeSpan.FromSeconds(15));
        var args = new List<(string, object)> { ("FileSystem", fileSystem), ("FileSystemLabel", label), ("Full", full) };
        if (clusterSize > 0) args.Add(("AllocationUnitSize", (uint)clusterSize));
        using var _ = Invoke(volume, "Format", args.ToArray());
    }

    public void DeletePartition(int diskNumber, int partitionNumber)
    {
        using var partition = GetPartition(diskNumber, partitionNumber);
        using var _ = Invoke(partition, "DeleteObject");
    }

    public void InitializeDisk(int diskNumber, PartitionManager.Core.Models.PartitionStyle style)
    {
        using var disk = GetSingle($"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}", $"disk {diskNumber}");
        ushort code = style switch
        {
            PartitionManager.Core.Models.PartitionStyle.Mbr => 1,
            PartitionManager.Core.Models.PartitionStyle.Gpt => 2,
            _ => throw new StorageWriteException("Only MBR and GPT are supported."),
        };
        using var _ = Invoke(disk, "Initialize", ("PartitionStyle", code));
    }

    public void SetPartitionReadOnly(int diskNumber, int partitionNumber, bool readOnly)
    {
        using (var partition = GetPartition(diskNumber, partitionNumber))
        using (Invoke(partition, "SetAttributes", ("IsReadOnly", readOnly))) { }
        // Remount so the mounted volume honours the new attribute.
        using (var partition = GetPartition(diskNumber, partitionNumber))
        using (Invoke(partition, "Offline")) { }
        using (var partition = GetPartition(diskNumber, partitionNumber))
        using (Invoke(partition, "Online")) { }
    }

    public RelocationResult RelocatePartition(int diskNumber, PartitionManager.Core.Execution.PartitionTarget source, long newOffsetBytes, Action<long, long>? checkpoint, bool physicalTestDisk = false)
    {
        string volumePath;
        using (var partition = GetPartition(diskNumber, source.PartitionNumber))
        {
            var offset = (long)Convert.ToUInt64(partition["Offset"]);
            var size = (long)Convert.ToUInt64(partition["Size"]);
            var guid = Guid.TryParse(partition["Guid"] as string, out var g) ? g : (Guid?)null;
            if (offset != source.OffsetBytes || size != source.SizeBytes || (source.UniqueGuid is { } want && guid != want))
                throw new StorageWriteException("The partition no longer matches the validated target. Nothing was changed.");
            volumePath = (partition["AccessPaths"] as string[] ?? []).FirstOrDefault(a => a.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
                         ?? throw new StorageWriteException("The partition has no volume to lock, so it cannot be moved safely. Nothing was changed.");
        }
        var src = source;
        using var disk = new Win32RawDisk(diskNumber);
        // Bind the opened device handle itself to the validated target (disk numbers can be reassigned between checks).
        var identity = disk.VerifyTarget(src.PartitionNumber, src.OffsetBytes, src.SizeBytes, src.UniqueGuid, newOffsetBytes, expectVirtual: !physicalTestDisk);
        var plan = RelocationPlan.Create(src.OffsetBytes, newOffsetBytes, src.SizeBytes, disk.SectorSize);
        var progress = new RelocationProgress { Overlapping = plan.Overlaps };
        using var volumeLock = Win32VolumeLock.Acquire(volumePath, TimeSpan.FromSeconds(10));
        try
        {
            disk.VerifyTarget(src.PartitionNumber, src.OffsetBytes, src.SizeBytes, src.UniqueGuid, newOffsetBytes, identity, expectVirtual: !physicalTestDisk);
            if (BootSectorRelocation.IsBitLocker(disk, src.OffsetBytes))
                throw new IOException("The partition is BitLocker-encrypted; moving BitLocker volumes is not supported yet. Nothing was changed.");
            byte[]? originalType = null;
            if (plan.Overlaps)
            {
                // Before the first write that destroys original data, make the partition unmountable in a way that
                // survives a crash, so Windows can never remount (and write to) a half-moved volume.
                originalType = disk.SetPartitionEntry(src.PartitionNumber, src.OffsetBytes, src.SizeBytes, src.UniqueGuid, src.OffsetBytes, newOffsetBytes,
                    identity, disk.MoveInProgressType());
                progress.MountGuardSet = true;
            }
            var result = BlockRelocator.Copy(disk, plan, checkpoint, progress);
            // Patch the copy's boot-sector start fields while the table still points at the original extent, then switch
            // the table (restoring the original type in the same update).
            var boot = BootSectorRelocation.Update(disk, newOffsetBytes, src.SizeBytes);
            disk.SetPartitionEntry(src.PartitionNumber, src.OffsetBytes, src.SizeBytes, src.UniqueGuid, newOffsetBytes, newOffsetBytes,
                identity, originalType, progress);
            return result with { BootSectorUpdate = boot };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Closing the lock handle releases the lock regardless; the non-mountable type is what keeps Windows away.
            throw new RelocationFailedException(progress, ex);
        }
    }

    private static ManagementObject GetPartition(int diskNumber, int partitionNumber) =>
        GetSingle($"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}", $"partition {partitionNumber} on disk {diskNumber}");

    private static ManagementObject GetVolume(int diskNumber, int partitionNumber, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            using (var partition = GetPartition(diskNumber, partitionNumber))
            {
                var paths = partition["AccessPaths"] as string[] ?? [];
                using var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery("SELECT * FROM MSFT_Volume"));
                using var volumes = searcher.Get();
                var volume = volumes.Cast<ManagementObject>().FirstOrDefault(v => paths.Contains(v["Path"] as string, StringComparer.OrdinalIgnoreCase));
                if (volume is not null) return volume;
            }
            if (DateTime.UtcNow >= deadline) throw new StorageWriteException("No volume is present on the partition.");
            Thread.Sleep(500);
        }
    }

    private static ManagementObject GetSingle(string wql, string what)
    {
        Scope.Connect();
        using var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery(wql));
        using var results = searcher.Get();
        var list = results.Cast<ManagementObject>().ToList();
        if (list.Count != 1) throw new StorageWriteException($"Expected exactly one {what}, found {list.Count}.");
        return list[0];
    }

    private static ManagementBaseObject Invoke(ManagementObject target, string method, params (string Name, object Value)[] args)
    {
        using var inParams = target.GetMethodParameters(method);
        foreach (var (name, value) in args) inParams[name] = value;
        var result = target.InvokeMethod(method, inParams, null);
        var code = Convert.ToUInt32(result?["ReturnValue"] ?? 1u);
        if (code != 0)
        {
            var extended = (result?["ExtendedStatus"] as ManagementBaseObject)?["Message"] as string;
            result?.Dispose();
            throw new StorageWriteException($"{method} failed with Storage Management API status {code}{(extended is null ? "" : ": " + extended)}");
        }
        return result!;
    }
}
