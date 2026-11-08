using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PartitionManager.PrivilegedHelper.Engine;

/// <summary>Sector-addressed access to a whole disk. All offsets and counts are multiples of <see cref="SectorSize"/>.</summary>
public interface IRawDisk : IDisposable
{
    int SectorSize { get; }
    long Length { get; }
    void Read(long offset, byte[] buffer, int count);
    void Write(long offset, byte[] buffer, int count);
    /// <summary>Waits until every write so far has reached the device.</summary>
    void Flush();
}

public sealed class RelocationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The copy schedule of a block-level partition move.
/// Chunks are copied <b>descending</b> when the destination is after the source and <b>ascending</b> when it is
/// before, so a chunk is never written over source data that has not been copied yet. The chunk size is also capped
/// at the shift distance, so no chunk overlaps its own source. That keeps every not-yet-copied source chunk intact at
/// all times, which is the property a future resume-after-interruption depends on.
/// </summary>
public sealed record RelocationPlan(long SourceOffset, long DestinationOffset, long Length, int ChunkBytes, bool Descending)
{
    public const int DefaultMaxChunk = 4 * 1024 * 1024;

    public long ChunkCount => (Length + ChunkBytes - 1) / ChunkBytes;
    public bool Overlaps => Math.Abs(DestinationOffset - SourceOffset) < Length;

    public static RelocationPlan Create(long source, long destination, long length, int sectorSize, int maxChunk = DefaultMaxChunk)
    {
        if (sectorSize <= 0 || (sectorSize & (sectorSize - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(sectorSize));
        if (source < 0 || destination < 0 || length <= 0) throw new ArgumentOutOfRangeException(nameof(length), "Offsets must be non-negative and the length positive.");
        if (source % sectorSize != 0 || destination % sectorSize != 0 || length % sectorSize != 0)
            throw new ArgumentException("Offsets and length must be whole sectors.");
        if (source == destination) throw new ArgumentException("The destination equals the source.");
        var shift = Math.Abs(destination - source);
        var chunk = Math.Min(Math.Min((long)maxChunk, shift), length);
        chunk -= chunk % sectorSize;
        if (chunk < sectorSize) throw new ArgumentException("The shift is smaller than one sector.");
        return new RelocationPlan(source, destination, length, (int)chunk, destination > source);
    }

    /// <summary>Byte offsets (relative to the partition start) in copy order.</summary>
    public IEnumerable<long> ChunkOrder()
    {
        if (Descending) for (var i = ChunkCount - 1; i >= 0; i--) yield return i * ChunkBytes;
        else for (long i = 0; i < ChunkCount; i++) yield return i * ChunkBytes;
    }
}

public sealed record RelocationResult(string SourceSha256, string DestinationSha256, long BytesCopied, long Chunks, string BootSectorUpdate);

/// <summary>Test-only fault injection: called before each chunk is written; may throw to simulate a device failure.</summary>
internal delegate void ChunkFault(long chunkOffset, long copiedSoFar);

public static class BlockRelocator
{
    /// <summary>SHA-256 of a disk extent, read in bounded chunks.</summary>
    public static string HashExtent(IRawDisk disk, long offset, long length, int bufferBytes = RelocationPlan.DefaultMaxChunk)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[bufferBytes - bufferBytes % disk.SectorSize];
        for (long done = 0; done < length;)
        {
            var n = (int)Math.Min(buffer.Length, length - done);
            disk.Read(offset + done, buffer, n);
            sha.AppendData(buffer, 0, n);
            done += n;
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>
    /// Copies the extent, verifying every chunk by reading it back, then verifies the whole destination against the
    /// SHA-256 of the source taken before the first write. Does not touch the partition table.
    /// </summary>
    /// <param name="checkpoint">Called after each chunk is written, flushed and verified, with (chunk offset, bytes copied).
    /// Re-copying an already-completed chunk can be unsafe in an overlapping move (later chunks may have overwritten its
    /// source), so a resume must start exactly after the last durable checkpoint.</param>
    public static RelocationResult Copy(IRawDisk disk, RelocationPlan plan, Action<long, long>? checkpoint = null, RelocationProgress? state = null) =>
        Copy(disk, plan, checkpoint, null, state);

    internal static RelocationResult Copy(IRawDisk disk, RelocationPlan plan, Action<long, long>? progress, ChunkFault? fault, RelocationProgress? state = null)
    {
        if (plan.SourceOffset + plan.Length > disk.Length || plan.DestinationOffset + plan.Length > disk.Length)
            throw new RelocationException("OUT_OF_RANGE", "The source or destination extends past the end of the disk.");
        if (plan.ChunkBytes % disk.SectorSize != 0) throw new RelocationException("BAD_PLAN", "The chunk size is not a whole number of sectors.");

        // Pre-pass (no writes): whole-extent SHA-256 plus one SHA-256 per chunk on the plan's own boundaries, so every
        // source read during the copy is checked BEFORE it is written over data that may be the only other copy.
        var buffer = new byte[plan.ChunkBytes];
        var check = new byte[plan.ChunkBytes];
        var chunkHashes = new Dictionary<long, byte[]>();
        string sourceHash;
        using (var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            for (long at = 0; at < plan.Length; at += plan.ChunkBytes)
            {
                var n = (int)Math.Min(plan.ChunkBytes, plan.Length - at);
                disk.Read(plan.SourceOffset + at, buffer, n);
                whole.AppendData(buffer, 0, n);
                chunkHashes[at] = SHA256.HashData(buffer.AsSpan(0, n));
            }
            sourceHash = Convert.ToHexString(whole.GetHashAndReset());
        }
        if (state is not null) state.SourceSha256 = sourceHash;

        long copied = 0, chunks = 0;
        foreach (var at in plan.ChunkOrder())
        {
            var n = (int)Math.Min(plan.ChunkBytes, plan.Length - at);
            disk.Read(plan.SourceOffset + at, buffer, n);
            if (!SHA256.HashData(buffer.AsSpan(0, n)).AsSpan().SequenceEqual(chunkHashes[at]))
                throw new RelocationException("SOURCE_READ_MISMATCH", $"The source chunk at +{at} read differently than in the verification pass; stopped before writing it.");
            fault?.Invoke(at, copied);
            if (state is not null) state.FirstWriteIssued = true;
            disk.Write(plan.DestinationOffset + at, buffer, n);
            disk.Flush();
            disk.Read(plan.DestinationOffset + at, check, n);
            if (!buffer.AsSpan(0, n).SequenceEqual(check.AsSpan(0, n)))
                throw new RelocationException("CHUNK_VERIFY_FAILED", $"Read-back of the chunk at +{at} did not match what was written.");
            copied += n; chunks++;
            if (state is not null) { state.LastChunkOffset = at; state.BytesCopied = copied; }
            progress?.Invoke(at, copied);
        }
        if (state is not null) state.CopyVerified = false;
        var destinationHash = HashExtent(disk, plan.DestinationOffset, plan.Length);
        if (destinationHash != sourceHash)
            throw new RelocationException("EXTENT_VERIFY_FAILED", $"The destination SHA-256 {destinationHash} differs from the source SHA-256 {sourceHash} taken before copying.");
        if (state is not null) state.CopyVerified = true;
        return new RelocationResult(sourceHash, destinationHash, copied, chunks, "");
    }
}

/// <summary>How far a relocation got. Filled in as it runs so a failure can be described by fact, not by message text.</summary>
public sealed class RelocationProgress
{
    public bool Overlapping { get; init; }
    public string? SourceSha256 { get; set; }
    /// <summary>A destination write was issued (for an overlapping move the original may now be partly overwritten).</summary>
    public bool FirstWriteIssued { get; set; }
    public long? LastChunkOffset { get; set; }
    public long BytesCopied { get; set; }
    /// <summary>The whole destination was read back and its SHA-256 equals the source's.</summary>
    public bool CopyVerified { get; set; }
    /// <summary>The partition was given a non-mountable type before the first overlapping write.</summary>
    public bool MountGuardSet { get; set; }
    /// <summary>IOCTL_DISK_SET_DRIVE_LAYOUT_EX for the new start was issued (its result may be unknown).</summary>
    public bool TableUpdateIssued { get; set; }
    public bool TableUpdated { get; set; }

    public RelocationPhase Phase =>
        TableUpdated ? RelocationPhase.TableUpdated
        : TableUpdateIssued ? RelocationPhase.TableUpdateUncertain
        : CopyVerified ? RelocationPhase.CopiedNotSwitched
        : FirstWriteIssued ? RelocationPhase.Copying
        : RelocationPhase.NothingWritten;
}

public enum RelocationPhase { NothingWritten, Copying, CopiedNotSwitched, TableUpdateUncertain, TableUpdated }

/// <summary>A relocation failure with the phase it reached, so callers never infer data state from message text.</summary>
public sealed class RelocationFailedException(RelocationProgress progress, Exception inner)
    : Exception(inner.Message, inner)
{
    public RelocationProgress Progress { get; } = progress;
}

/// <summary>
/// After a move, a volume's boot sector still records the old partition start ("hidden sectors" for NTFS/FAT, the
/// PartitionOffset field for exFAT). Windows mounts data volumes regardless, but boot code, chkdsk and other tools
/// read it, so it is rewritten in the primary and backup boot sectors. Only well-identified boot sectors are changed.
/// </summary>
public static class BootSectorRelocation
{
    public static string Update(IRawDisk disk, long partitionOffset, long partitionLength)
    {
        var ss = disk.SectorSize;
        var sector = new byte[ss];
        disk.Read(partitionOffset, sector, ss);
        if (BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(510)) != 0xAA55 && !IsExFat(sector))
            return "No boot signature; boot sector left unchanged.";
        var newStartSector = partitionOffset / ss;

        if (Oem(sector, 3) == "NTFS    ")
        {
            var bps = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(0x0B));
            if (bps != ss) return $"NTFS bytes-per-sector {bps} differs from the disk's {ss}; boot sector left unchanged.";
            if (newStartSector > uint.MaxValue) return "Partition start exceeds the 32-bit hidden-sectors field; left unchanged.";
            var total = BinaryPrimitives.ReadInt64LittleEndian(sector.AsSpan(0x28));
            var old = PatchHidden(sector, (uint)newStartSector);
            disk.Write(partitionOffset, sector, ss);
            var note = $"NTFS hidden sectors {old} -> {newStartSector}";
            var backupAt = partitionOffset + total * ss;
            if (total > 0 && backupAt + ss <= partitionOffset + partitionLength)
            {
                var backup = new byte[ss];
                disk.Read(backupAt, backup, ss);
                if (Oem(backup, 3) == "NTFS    ") { PatchHidden(backup, (uint)newStartSector); disk.Write(backupAt, backup, ss); note += " (primary and backup)"; }
                else note += " (backup boot sector not found)";
            }
            disk.Flush();
            return note;
        }
        if (Oem(sector, 0x52) == "FAT32   ")
        {
            if (newStartSector > uint.MaxValue) return "Partition start exceeds the 32-bit hidden-sectors field; left unchanged.";
            var old = PatchHidden(sector, (uint)newStartSector);
            disk.Write(partitionOffset, sector, ss);
            var note = $"FAT32 hidden sectors {old} -> {newStartSector}";
            var backupSector = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(0x32));
            if (backupSector is > 0 and < 32)
            {
                var backupAt = partitionOffset + (long)backupSector * ss;
                var backup = new byte[ss];
                disk.Read(backupAt, backup, ss);
                if (Oem(backup, 0x52) == "FAT32   ") { PatchHidden(backup, (uint)newStartSector); disk.Write(backupAt, backup, ss); note += " (primary and backup)"; }
            }
            disk.Flush();
            return note;
        }
        if (Oem(sector, 0x36) is "FAT16   " or "FAT12   " or "FAT     ")
        {
            if (newStartSector > uint.MaxValue) return "Partition start exceeds the 32-bit hidden-sectors field; left unchanged.";
            var old = PatchHidden(sector, (uint)newStartSector);
            disk.Write(partitionOffset, sector, ss);
            disk.Flush();
            return $"FAT hidden sectors {old} -> {newStartSector}";
        }
        if (IsExFat(sector))
        {
            var shift = sector[0x6C];
            if (1 << shift != ss) return $"exFAT bytes-per-sector 2^{shift} differs from the disk's {ss}; boot sector left unchanged.";
            var note = $"exFAT PartitionOffset {BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(0x40))} -> {newStartSector}";
            foreach (var region in new[] { 0L, 12L })
            {
                var at = partitionOffset + region * ss;
                var boot = new byte[12 * ss];
                disk.Read(at, boot, boot.Length);
                if (!IsExFat(boot)) { note += region == 0 ? "" : " (backup boot region not found)"; continue; }
                BinaryPrimitives.WriteUInt64LittleEndian(boot.AsSpan(0x40), (ulong)newStartSector);
                var sum = ExFatBootChecksum(boot, ss);
                for (var i = 11 * ss; i < 12 * ss; i += 4) BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(i), sum);
                disk.Write(at, boot, boot.Length);
            }
            disk.Flush();
            return note + " (boot checksum recomputed)";
        }
        return "File system not recognised; boot sector left unchanged.";
    }

    private static bool IsExFat(byte[] sector) => Oem(sector, 3) == "EXFAT   ";

    /// <summary>
    /// True if the partition's first sector carries the BitLocker signature ("-FVE-FS-"), whether the volume is locked or
    /// unlocked. Independent of WMI, which is not available on every edition and does not report letterless volumes.
    /// </summary>
    public static bool IsBitLocker(IRawDisk disk, long partitionOffset)
    {
        var sector = new byte[disk.SectorSize];
        disk.Read(partitionOffset, sector, sector.Length);
        return Oem(sector, 3) == "-FVE-FS-";
    }
    private static string Oem(byte[] sector, int at) => Encoding.ASCII.GetString(sector, at, 8);

    private static uint PatchHidden(byte[] sector, uint value)
    {
        var old = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(0x1C));
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(0x1C), value);
        return old;
    }

    /// <summary>exFAT boot checksum over sectors 0–10, skipping VolumeFlags (106–107) and PercentInUse (112).</summary>
    public static uint ExFatBootChecksum(byte[] region, int sectorSize)
    {
        uint sum = 0;
        for (var i = 0; i < 11 * sectorSize; i++)
        {
            if (i is 106 or 107 or 112) continue;
            sum = ((sum & 1) != 0 ? 0x80000000u : 0u) + (sum >> 1) + region[i];
        }
        return sum;
    }
}
