using System.Security.Cryptography;
using System.Text.Json;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Models;

namespace PartitionManager.Core.Safety;

/// <summary>
/// Stateful, virtual-only E-region policy rehearsal. No execution API, physical
/// grant, persistence or administrator consent is implied by an accepted proposal.
/// </summary>
public sealed class RegionRehearsal
{
    private DiskInfo _expected;
    private readonly Guid _originalGuid;
    private readonly PartitionInfo[] _protected;
    private readonly HashSet<Guid> _consumed = [];
    private readonly HashSet<Guid> _owned = [];
    private readonly object _gate = new();
    public long OriginalStart { get; }
    public long OriginalEndExclusive { get; }
    public bool EnablesPhysicalWrites => false;
    public IReadOnlyList<Guid> OwnedPartitions { get { lock (_gate) return _owned.ToArray(); } }

    public RegionRehearsal(DiskInfo virtualDisk, Guid targetGuid)
    {
        if (!virtualDisk.IsVirtual || virtualDisk.Style != PartitionStyle.Gpt || !DiskIdentity.From(virtualDisk).HasStableId)
            throw new ArgumentException("Region rehearsal requires an identified disposable GPT virtual disk.");
        var target=virtualDisk.Partitions.Single(p=>p.UniqueGuid==targetGuid);
        if(target.IsProtectedRole || target.IsBoot || target.IsSystem || target.Role!=PartitionRole.BasicData || !GeometryValid(virtualDisk))
            throw new ArgumentException("Invalid or protected rehearsal target.");
        _expected=Snapshot(virtualDisk); _originalGuid=targetGuid;
        OriginalStart=target.OffsetBytes; OriginalEndExclusive=checked(target.OffsetBytes+target.SizeBytes);
        _protected=virtualDisk.Partitions.Where(p=>p.UniqueGuid!=targetGuid).ToArray();
        _owned.Add(targetGuid);
    }

    public string ConfirmationHash(DiskInfo proposed)
    {
        lock(_gate) return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { OriginalStart,OriginalEndExclusive,_originalGuid,Before=Fingerprint(_expected),After=Fingerprint(proposed) })));
    }

    public ValidationResult ValidateAndAdvance(IReadOnlyList<DiskInfo> freshlyDiscovered,DiskInfo proposed,RehearsalConfirmation confirmation,DateTimeOffset now)
    {
        lock(_gate)
        {
            if(confirmation.Nonce==Guid.Empty || !_consumed.Add(confirmation.Nonce)) return Deny("REPLAY");
            if(confirmation.CreatedUtc>now || now-confirmation.CreatedUtc>TimeSpan.FromMinutes(2)) return Deny("EXPIRED_CONFIRMATION");
            if(confirmation.ProposalHash!=ConfirmationHash(proposed)) return Deny("WRONG_CONFIRMATION");
            var match=DiskIdentityMatcher.Match(DiskIdentity.From(_expected),freshlyDiscovered);
            if(!match.IsMatch || Fingerprint(match.Disk!)!=Fingerprint(_expected)) return Deny("LAYOUT_CHANGED");
            if(!proposed.IsVirtual || !GeometryValid(proposed) ||
                Fingerprint(proposed with { Partitions=_expected.Partitions })!=Fingerprint(_expected)) return Deny("DISK_CHANGED");
            foreach(var p in _protected)
                if(!proposed.Partitions.Any(q=>PartitionFingerprint(q)==PartitionFingerprint(p))) return Deny("PROTECTED_PARTITION_CHANGED");
            var original=proposed.Partitions.SingleOrDefault(p=>p.UniqueGuid==_originalGuid);
            if(original is null || original.OffsetBytes!=OriginalStart) return Deny("ORIGINAL_TARGET_REMOVED_OR_MOVED");
            var protectedIds=_protected.Select(p=>p.UniqueGuid).ToHashSet();
            foreach(var p in proposed.Partitions.Where(p=>!protectedIds.Contains(p.UniqueGuid)))
            {
                if(p.OffsetBytes<OriginalStart || p.SizeBytes>OriginalEndExclusive-OriginalStart || p.OffsetBytes>OriginalEndExclusive-p.SizeBytes ||
                    p.Role!=PartitionRole.BasicData || p.GptType!=PartitionTypes.BasicData || p.IsBoot || p.IsSystem || p.IsProtectedRole ||
                    p.OffsetBytes%(1<<20)!=0 || p.SizeBytes%512!=0) return Deny("OUTSIDE_REGION");
                if(!_owned.Contains(p.UniqueGuid!.Value) && _expected.Partitions.Any(q=>q.UniqueGuid==p.UniqueGuid)) return Deny("FOREIGN_PARTITION");
            }
            // This advances only the model after a validated proposed transition.
            // A future executor must independently verify real results before enrollment.
            _expected=Snapshot(proposed);
            _owned.Clear();
            foreach(var p in proposed.Partitions.Where(p=>!protectedIds.Contains(p.UniqueGuid))) _owned.Add(p.UniqueGuid!.Value);
            return new(true,"REHEARSAL_ONLY","Virtual model accepted; immutable original region preserved. No physical write permission.");
        }
    }

    private static DiskInfo Snapshot(DiskInfo d)=>d with { Partitions=d.Partitions.ToArray() };
    private static string Fingerprint(DiskInfo d)=>JsonSerializer.Serialize(d with { Partitions=d.Partitions.OrderBy(p=>p.OffsetBytes).ToArray() });
    private static string PartitionFingerprint(PartitionInfo p)=>JsonSerializer.Serialize(p);
    private static ValidationResult Deny(string code)=>ValidationResult.Fail(code,"Region rehearsal refused; no writes authorized.");
    private static bool GeometryValid(DiskInfo d)
    {
        if(d.LogicalSectorSize<=0 || d.SizeBytes<=0 || d.Partitions.Any(p=>p.UniqueGuid is null || p.UniqueGuid==Guid.Empty) ||
            d.Partitions.Select(p=>p.UniqueGuid).Distinct().Count()!=d.Partitions.Count || d.Partitions.Select(p=>p.PartitionNumber).Distinct().Count()!=d.Partitions.Count) return false;
        long end=d.LogicalSectorSize;
        foreach(var p in d.Partitions.OrderBy(p=>p.OffsetBytes))
        {
            if(p.DiskNumber!=d.Number || p.PartitionNumber<=0 || p.OffsetBytes<end || p.SizeBytes<=0 || p.SizeBytes>d.SizeBytes ||
                p.OffsetBytes>d.SizeBytes-p.SizeBytes || p.OffsetBytes%d.LogicalSectorSize!=0 || p.SizeBytes%d.LogicalSectorSize!=0) return false;
            end=p.OffsetBytes+p.SizeBytes;
        }
        return true;
    }
}
