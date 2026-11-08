using System.Security.Cryptography;
using System.Text.Json;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Models;

namespace PartitionManager.Core.Safety;

/// <summary>Design evidence only. This type is never consulted by the write engine.</summary>
public sealed record EScopeProposal(DiskIdentity Disk, PartitionTarget Target, string MetadataFingerprint,
    IReadOnlyList<PartitionTarget> ProtectedPartitions)
{
    public bool EnablesPhysicalWrites => false;
}

public sealed record RehearsalConfirmation(Guid Nonce, string ProposalHash, DateTimeOffset CreatedUtc);

/// <summary>Pure, read-only identity inspection and authorization rehearsal. No execution capability.</summary>
public sealed class EScopeRehearsal
{
    private readonly HashSet<Guid> _consumed = [];
    private readonly object _gate = new();

    public static EScopeProposal Inspect(IReadOnlyList<DiskInfo> disks)
    {
        var candidates = disks.SelectMany(d => d.Partitions.Where(p => p.DriveLetter == 'E').Select(p => (d, p))).ToArray();
        if (candidates.Length != 1) throw new InvalidOperationException("E: must resolve to exactly one partition.");
        var (disk, target) = candidates[0];
        if (disk.Style != PartitionStyle.Gpt || string.IsNullOrWhiteSpace(disk.UniqueId) || target.UniqueGuid is null || target.UniqueGuid == Guid.Empty)
            throw new InvalidOperationException("Stable GPT disk and partition identities are required.");
        if (disk.Partitions.Any(p => p.UniqueGuid is null || p.UniqueGuid == Guid.Empty) ||
            disk.Partitions.Select(p => p.UniqueGuid).Distinct().Count() != disk.Partitions.Count)
            throw new InvalidOperationException("Missing or ambiguous partition GUIDs.");
        if (!DiskIdentityMatcher.Match(DiskIdentity.From(disk), disks).IsMatch || !ValidGeometry(disk))
            throw new InvalidOperationException("Ambiguous disk identity or invalid partition boundaries.");
        if (target.IsProtectedRole || target.IsBoot || target.IsSystem || target.FileSystem != "NTFS")
            throw new InvalidOperationException("E: is not an eligible NTFS data partition.");
        return new(DiskIdentity.From(disk), PartitionTarget.From(target), Metadata(disk),
            disk.Partitions.Where(p => p.UniqueGuid != target.UniqueGuid).Select(PartitionTarget.From).ToArray());
    }

    // Captures flags and volume identities omitted from the ordinary structural fingerprint.
    public static string Metadata(DiskInfo disk) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        disk.Partitions.OrderBy(p => p.OffsetBytes).Select(p => new { p.PartitionNumber, p.UniqueGuid, p.GptType,
            p.OffsetBytes, p.SizeBytes, p.IsHidden, p.IsActive, p.IsBoot, p.IsSystem, p.Role, p.VolumePath, p.BitLocker }))));

    public static string ProposalHash(EScopeProposal policy, DiskInfo proposed) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { policy, Layout = proposed })));

    public ValidationResult EvaluateShrink(EScopeProposal policy, IReadOnlyList<DiskInfo> current,
        DiskInfo proposed, RehearsalConfirmation confirmation, DateTimeOffset now)
    {
        lock (_gate)
        {
            // Every attempt consumes its confirmation, including a rejected attempt.
            if (confirmation.Nonce == Guid.Empty || !_consumed.Add(confirmation.Nonce)) return Deny("REPLAY");
            if (confirmation.CreatedUtc > now || now - confirmation.CreatedUtc > TimeSpan.FromMinutes(2)) return Deny("EXPIRED_CONFIRMATION");
            if (confirmation.ProposalHash != ProposalHash(policy, proposed)) return Deny("WRONG_CONFIRMATION");
            var match = DiskIdentityMatcher.Match(policy.Disk, current);
            if (!match.IsMatch || Metadata(match.Disk!) != policy.MetadataFingerprint) return Deny("LAYOUT_CHANGED");
            var before = match.Disk!;
            if (proposed.Number != before.Number || proposed.UniqueId != before.UniqueId || proposed.SerialNumber != before.SerialNumber ||
                proposed.SizeBytes != before.SizeBytes || proposed.Style != before.Style || proposed.LogicalSectorSize != before.LogicalSectorSize ||
                !ValidGeometry(proposed) || proposed.Partitions.Count != before.Partitions.Count) return Deny("INVALID_LAYOUT");
            var target = proposed.Partitions.SingleOrDefault(p => p.UniqueGuid == policy.Target.UniqueGuid);
            var old = before.Partitions.Single(p => policy.Target.Matches(p));
            if (target is null || target.PartitionNumber != old.PartitionNumber || target.OffsetBytes != old.OffsetBytes ||
                target.SizeBytes <= 0 || target.SizeBytes >= old.SizeBytes || target.SizeBytes % (1 << 20) != 0 ||
                (target with { SizeBytes = old.SizeBytes }) != old) return Deny("OUTSIDE_E_SCOPE");
            foreach (var p in before.Partitions.Where(p => p.UniqueGuid != old.UniqueGuid))
                if (!proposed.Partitions.Contains(p)) return Deny("PROTECTED_PARTITION_CHANGED");
            return new(true, "REHEARSAL_ONLY", "Proposal passes the in-memory shrink rehearsal. Physical writes remain blocked; raw GPT and runtime checks are still required.");
        }
    }

    private static ValidationResult Deny(string code) => ValidationResult.Fail(code, "Rehearsal refused. No write authorization was issued.");

    private static bool ValidGeometry(DiskInfo disk)
    {
        long end = disk.LogicalSectorSize;
        if (disk.LogicalSectorSize <= 0 || disk.SizeBytes <= 0) return false;
        if (disk.Partitions.Select(p => p.PartitionNumber).Distinct().Count() != disk.Partitions.Count ||
            disk.Partitions.Select(p => p.UniqueGuid).Distinct().Count() != disk.Partitions.Count) return false;
        foreach (var p in disk.Partitions.OrderBy(p => p.OffsetBytes))
        {
            if (p.DiskNumber != disk.Number || p.OffsetBytes < end || p.SizeBytes <= 0 ||
                p.SizeBytes > disk.SizeBytes || p.OffsetBytes > disk.SizeBytes - p.SizeBytes ||
                p.OffsetBytes % disk.LogicalSectorSize != 0 || p.SizeBytes % disk.LogicalSectorSize != 0) return false;
            end = p.EndBytes;
        }
        return true;
    }
}
