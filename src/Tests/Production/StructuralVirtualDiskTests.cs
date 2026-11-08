using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Layout;
using PartitionManager.Core.Logging;
using PartitionManager.Core.Models;
using PartitionManager.Infrastructure.WindowsApi;
using PartitionManager.PrivilegedHelper.Engine;
using Xunit;

namespace PartitionManager.Tests.Production;

/// <summary>Real backend with one call forced to fail, for partial-failure tests on a real virtual disk.</summary>
internal sealed class FailingBackend(IStorageWriteBackend inner, string failOn) : IStorageWriteBackend
{
    public IDisposable? PinBackingImage(DiskInfo disk) => inner.PinBackingImage(disk);
    private void Maybe(string name) { if (name == failOn) throw new StorageWriteException($"injected failure in {name}"); }
    public void SetVolumeLabel(int d, int p, string l) { Maybe("label"); inner.SetVolumeLabel(d, p, l); }
    public void AddDriveLetter(int d, int p, char c) { Maybe("add"); inner.AddDriveLetter(d, p, c); }
    public void RemoveDriveLetter(int d, int p, char c) { Maybe("remove"); inner.RemoveDriveLetter(d, p, c); }
    public (long Min, long Max) GetSupportedSize(int d, int p) => inner.GetSupportedSize(d, p);
    public void Resize(int d, int p, long s) { Maybe("resize"); inner.Resize(d, p, s); }
    public int CreatePartition(int d, long o, long s) { Maybe("create"); return inner.CreatePartition(d, o, s); }
    public void Format(int d, int p, string fs, string l, int c, bool f) { Maybe("format"); inner.Format(d, p, fs, l, c, f); }
    public void DeletePartition(int d, int p) { Maybe("delete"); inner.DeletePartition(d, p); }
    public void InitializeDisk(int d, PartitionStyle s) { Maybe("initialize"); inner.InitializeDisk(d, s); }
    public void SetPartitionReadOnly(int d, int p, bool r) { Maybe(r ? "readonly" : "writable"); inner.SetPartitionReadOnly(d, p, r); }
}

/// <summary>
/// Create, format, delete, shrink, extend and split executed for real on disposable VHDX images (GPT and MBR), each
/// verified with an independent PowerShell storage query; file contents on the resized partition are hashed before
/// and after every structural change. Needs admin and PM_VHD_TESTS=1. Images live in PM_VHD_TEST_DIR only.
/// </summary>
public class StructuralVirtualDiskTests
{
    private const long MiB = ByteSize.MiB;
    private sealed record PsPart(int PartitionNumber, long Offset, long Size, string DriveLetter, string FileSystem, string Label);

    private readonly StringBuilder _rows = new();
    private readonly StringBuilder _integrity = new();
    private readonly List<string> _failures = [];

    private void Row(string kind, string step, bool ok, string detail)
    {
        _rows.AppendLine($"| {kind} | {step} | {(ok ? "PASS" : "FAIL")} | {detail.Replace("|", "/").ReplaceLineEndings(" ")} |");
        if (!ok) _failures.Add(step);
    }

    [SkippableFact]
    [Trait("Category", "VirtualDisk")]
    public async Task Six_structural_operations_on_disposable_virtual_disks()
    {
        Skip.IfNot(Elevation.IsElevated, "Requires administrator rights to attach a VHDX.");
        Skip.IfNot(Environment.GetEnvironmentVariable("PM_VHD_TESTS") == "1", "Set PM_VHD_TESTS=1 to run virtual-disk write tests.");

        var folder = Path.GetFullPath(Environment.GetEnvironmentVariable("PM_VHD_TEST_DIR") ?? @"D:\PartitionManager-TestDisks");
        var reportDir = Environment.GetEnvironmentVariable("PM_VHD_REPORT_DIR") ?? Path.Combine(folder, "reports");
        Directory.CreateDirectory(reportDir);
        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var discovery = new WmiDiskDiscoveryService(NullOperationLog.Instance);
        var initial = (await discovery.DiscoverAsync()).State;
        var hostLetter = char.ToUpperInvariant(folder[0]);
        var hostDisk = initial.Disks.Single(d => d.Partitions.Any(p => p.DriveLetter == hostLetter));
        var preexisting = initial.Disks.Select(d => d.Number).ToHashSet();
        var protectedBefore = PsLayout(hostDisk.Number);
        Row("Guard", "Host disk recorded", !hostDisk.IsVirtual, $"{hostLetter}: on Disk {hostDisk.Number} ({hostDisk.Model}); {protectedBefore.Count} partitions recorded for the before/after comparison");

        var policy = new FixedHelperPolicySource(new HelperPolicy(WriteMode.VirtualDisksOnly, [folder]));
        var real = new WmiStorageWriteBackend();
        var engine = new WriteEngine(discovery, real, policy, new MemoryAudit());
        var images = new List<TestVirtualDisk>();
        try
        {
            foreach (var style in new[] { "GPT", "MBR" })
            {
                var vhd = TestVirtualDisk.Create(folder, $"pm-{runId}-struct-{style.ToLowerInvariant()}.vhdx", 1L << 30);
                images.Add(vhd);
                var n = vhd.Attach();
                var attached = (await discovery.DiscoverAsync()).State.Disk(n);
                var ours = n != hostDisk.Number && !preexisting.Contains(n) && attached.IsVirtual
                           && string.Equals(Path.GetFullPath(attached.VirtualImagePath), vhd.Path, StringComparison.OrdinalIgnoreCase);
                Row("Guard", $"{style}: attached image is a new virtual disk", ours, $"Disk {n}, '{attached.Location}'");
                if (!ours) continue;
                Ps($"Initialize-Disk -Number {n} -PartitionStyle {style}");
                Row("Setup (cmdlet)", $"{style}: initialize disk", true, "Initialize is not one of the six operations under test");

                await RunSequence(engine, discovery, n, style, style == "GPT");
            }

            // Partial failure on real hardware: shrink succeeds, create is forced to fail.
            var gpt = images[0];
            if (gpt.DiskNumber is { } g)
            {
                var data = await Single(discovery, g);
                var hashes = HashFiles(data.DriveLetter!.Value);
                var failing = new WriteEngine(discovery, new FailingBackend(real, "create"), policy, new MemoryAudit());
                var originalSize = data.SizeBytes;
                // Keep the first part above Windows' own shrink minimum so the shrink really happens before the injected failure.
                var (minSize, _) = real.GetSupportedSize(g, data.PartitionNumber);
                var firstSize = DiskLayout.AlignUp(minSize + 8 * MiB);
                Assert.True(firstSize < originalSize - 8 * MiB, $"test partition too small to split ({minSize:N0} min of {originalSize:N0})");
                var resp = failing.Execute(Guid.NewGuid(), Req(await Disk(discovery, g), data, WriteOperationKind.SplitPartition, size: firstSize, fs: "NTFS"));
                var after = PsLayout(g);
                var shrunk = after.SingleOrDefault(p => p.Offset == data.OffsetBytes);
                Row("Partial failure", "Split with create forced to fail", resp.Code == "SPLIT_CREATE_FAILED" && shrunk?.Size == firstSize && after.Count(p => p.Size > 32 * MiB) == 1,
                    $"{resp.Code}: {resp.Message} | independent: original partition {shrunk?.Size:N0} B, {after.Count} partitions");
                Integrity("after partial Split", hashes, data.DriveLetter.Value);

                var back = engine.Execute(Guid.NewGuid(), Req(await Disk(discovery, g), await Single(discovery, g), WriteOperationKind.ExtendPartition, size: originalSize));
                var restored = PsLayout(g).SingleOrDefault(p => p.Offset == data.OffsetBytes);
                Row("Partial failure", "Documented recovery: extend back", back.Status == HelperResponseStatus.Ok && restored?.Size == originalSize, $"{back.Code} | independent size {restored?.Size:N0}");
                Integrity("after recovery Extend", hashes, data.DriveLetter.Value);

                // Disconnection: request built while attached, executed after detach.
                var pending = Req(await Disk(discovery, g), await Single(discovery, g), WriteOperationKind.ShrinkPartition, size: DiskLayout.AlignDown(originalSize / 2));
                gpt.Detach();
                var gone = engine.Execute(Guid.NewGuid(), pending);
                Row("Disconnection", "Request for a detached disk", gone.Code == "IDENTITY_NOTFOUND", $"{gone.Code}: {gone.Message}");
            }

            var protectedAfter = PsLayout(hostDisk.Number);
            Row("Guard", $"Host Disk {hostDisk.Number} unchanged", protectedBefore.SequenceEqual(protectedAfter),
                $"{protectedAfter.Count} partitions; offsets, sizes and letters identical before and after");
        }
        finally
        {
            foreach (var vhd in images)
            {
                try
                {
                    vhd.Detach();
                    var idOk = TestVirtualDisk.ReadVhdxId(vhd.Path) == vhd.VhdxId;
                    if (idOk && Path.GetFileName(vhd.Path).StartsWith($"pm-{runId}-struct-", StringComparison.Ordinal)) File.Delete(vhd.Path);
                    Row("Cleanup", $"Detach and delete {Path.GetFileName(vhd.Path)}", idOk && !File.Exists(vhd.Path), idOk ? "VHDX id matched" : "identity mismatch, kept");
                }
                catch (Exception ex) { Row("Cleanup", Path.GetFileName(vhd.Path), false, ex.Message); }
            }
            var report = $"# Structural operations on virtual disks, run {runId}\n\nEvery PASS means the helper reported success (or the expected refusal) **and** an independent `Get-Partition`/`Get-Volume` query agreed.\n\n" +
                         "| Kind | Step | Result | Detail |\n|---|---|---|---|\n" + _rows +
                         "\n## Data integrity (SHA-256 of files on the partition being resized)\n\n| Checkpoint | Files | Result |\n|---|---|---|\n" + _integrity;
            File.WriteAllText(Path.Combine(reportDir, $"StructuralRun-{runId}.md"), report);
        }
        Assert.True(_failures.Count == 0, string.Join("; ", _failures));
    }

    private async Task RunSequence(WriteEngine engine, WmiDiskDiscoveryService discovery, int n, string style, bool negatives)
    {
        var disk = await Disk(discovery, n);
        var free = DiskLayout.FreeSegments(disk).OrderByDescending(s => s.SizeBytes).First();
        var offset = DiskLayout.AlignUp(free.OffsetBytes);
        var letters = (await discovery.DiscoverAsync()).State.AvailableLetters;
        char dataLetter = letters[^1], splitLetter = letters[^2];

        // CREATE
        var create = engine.Execute(Guid.NewGuid(), new(WriteOperationKind.CreatePartition, DiskIdentity.From(disk), null, "PMDATA", dataLetter, null,
            OffsetBytes: offset, SizeBytes: 600 * MiB, FileSystem: "NTFS"));
        var created = PsLayout(n).SingleOrDefault(p => p.Offset == offset);
        Row("Operation", $"{style}: Create 600 MB NTFS 'PMDATA' {dataLetter}:", create.Status == HelperResponseStatus.Ok && created is { FileSystem: "NTFS", Label: "PMDATA" } && created.DriveLetter == dataLetter.ToString() && created.Size == 600 * MiB,
            $"{create.Code}: {create.Message} | independent: {Describe(created)}");
        if (created is null) return;

        var hashes = WriteFiles(dataLetter);
        Integrity($"{style}: after create (baseline)", hashes, dataLetter);

        if (negatives)
        {
            var overlap = engine.Execute(Guid.NewGuid(), new(WriteOperationKind.CreatePartition, DiskIdentity.From(await Disk(discovery, n)), null, null, null, null,
                OffsetBytes: offset, SizeBytes: 100 * MiB, FileSystem: "NTFS"));
            Row("Negative", $"{style}: Create over existing partition refused", overlap.Code == "NOT_ENOUGH_SPACE" && PsLayout(n).Count(p => p.Size > 32 * MiB) == 1, $"{overlap.Code}");

            var tooSmall = engine.Execute(Guid.NewGuid(), Req(await Disk(discovery, n), await Single(discovery, n), WriteOperationKind.ShrinkPartition, size: MiB));
            Row("Negative", $"{style}: Shrink below Windows minimum refused", tooSmall.Code == "BELOW_MINIMUM" && PsLayout(n).Single(p => p.Offset == offset).Size == 600 * MiB, $"{tooSmall.Code}: {tooSmall.Message}");

            var tooBig = engine.Execute(Guid.NewGuid(), Req(await Disk(discovery, n), await Single(discovery, n), WriteOperationKind.ExtendPartition, size: 2048 * MiB));
            Row("Negative", $"{style}: Extend past the disk refused", tooBig.Code is "NOT_ENOUGH_SPACE" or "BAD_SIZE", $"{tooBig.Code}");
        }

        // SHRINK
        var p0 = await Single(discovery, n);
        var shrink = engine.Execute(Guid.NewGuid(), Req(await Disk(discovery, n), p0, WriteOperationKind.ShrinkPartition, size: 400 * MiB));
        var afterShrink = PsLayout(n).Single(p => p.Offset == offset);
        Row("Operation", $"{style}: Shrink 600 → 400 MB", shrink.Status == HelperResponseStatus.Ok && afterShrink.Size == 400 * MiB, $"{shrink.Code} | independent size {afterShrink.Size:N0}");
        Integrity($"{style}: after shrink", hashes, dataLetter);

        // EXTEND
        var extend = engine.Execute(Guid.NewGuid(), Req(await Disk(discovery, n), await Single(discovery, n), WriteOperationKind.ExtendPartition, size: 500 * MiB));
        var afterExtend = PsLayout(n).Single(p => p.Offset == offset);
        Row("Operation", $"{style}: Extend 400 → 500 MB", extend.Status == HelperResponseStatus.Ok && afterExtend.Size == 500 * MiB, $"{extend.Code} | independent size {afterExtend.Size:N0}");
        Integrity($"{style}: after extend", hashes, dataLetter);

        if (negatives)
        {
            // Concurrent layout change: request planned, then another partition appears before execution.
            var stale = Req(await Disk(discovery, n), await Single(discovery, n), WriteOperationKind.ShrinkPartition, size: 450 * MiB);
            var tail = DiskLayout.FreeSegments(await Disk(discovery, n)).OrderByDescending(s => s.SizeBytes).First();
            Ps($"New-Partition -DiskNumber {n} -Offset {DiskLayout.AlignUp(tail.OffsetBytes)} -Size 32MB | Out-Null");
            var r = engine.Execute(Guid.NewGuid(), stale);
            Row("Negative", $"{style}: Request with a stale layout refused", r.Code == "IDENTITY_LAYOUTCHANGED" && PsLayout(n).Single(p => p.Offset == offset).Size == 500 * MiB, $"{r.Code}");
            var extra = PsLayout(n).Single(p => p.Size == 32 * MiB);
            Ps($"Remove-Partition -DiskNumber {n} -PartitionNumber {extra.PartitionNumber} -Confirm:$false");
        }

        // SPLIT
        var beforeSplit = await Single(discovery, n);
        var split = engine.Execute(Guid.NewGuid(), Req(await Disk(discovery, n), beforeSplit, WriteOperationKind.SplitPartition, size: 300 * MiB, fs: "NTFS", label: "PMSPLIT", letter: splitLetter));
        var layout = PsLayout(n);
        var firstPart = layout.SingleOrDefault(p => p.Offset == offset);
        var second = layout.SingleOrDefault(p => p.Offset == offset + 300 * MiB);
        Row("Operation", $"{style}: Split 500 MB → 300 MB + new NTFS 'PMSPLIT' {splitLetter}:",
            split.Status == HelperResponseStatus.Ok && firstPart?.Size == 300 * MiB && second is { FileSystem: "NTFS", Label: "PMSPLIT" } && second.DriveLetter == splitLetter.ToString(),
            $"{split.Code}: {split.Message} | steps: {string.Join(" → ", split.Steps)} | independent: first {Describe(firstPart)}; new {Describe(second)}");
        Integrity($"{style}: after split", hashes, dataLetter);
        if (second is null) return;

        // FORMAT the new partition
        var newPart = (await Disk(discovery, n)).Partitions.Single(p => p.OffsetBytes == second.Offset);
        var format = engine.Execute(Guid.NewGuid(), Req(await Disk(discovery, n), newPart, WriteOperationKind.FormatVolume, fs: "exFAT", label: "PMFMT"));
        var formatted = PsLayout(n).SingleOrDefault(p => p.Offset == second.Offset);
        Row("Operation", $"{style}: Format new partition as exFAT 'PMFMT'", format.Status == HelperResponseStatus.Ok && formatted is { FileSystem: "exFAT", Label: "PMFMT" }, $"{format.Code} | independent: {Describe(formatted)}");
        Integrity($"{style}: after formatting the other partition", hashes, dataLetter);

        // DELETE the new partition
        var toDelete = (await Disk(discovery, n)).Partitions.Single(p => p.OffsetBytes == second.Offset);
        var delete = engine.Execute(Guid.NewGuid(), Req(await Disk(discovery, n), toDelete, WriteOperationKind.DeletePartition));
        var afterDelete = PsLayout(n);
        Row("Operation", $"{style}: Delete new partition", delete.Status == HelperResponseStatus.Ok && afterDelete.All(p => p.Offset != second.Offset) && afterDelete.Any(p => p.Offset == offset),
            $"{delete.Code} | independent: {afterDelete.Count} partition(s) left");
        Integrity($"{style}: after deleting the other partition", hashes, dataLetter);
    }

    private static WriteOperationRequest Req(DiskInfo d, PartitionInfo p, WriteOperationKind kind, long? size = null, string? fs = null, string? label = null, char? letter = null) =>
        new(kind, DiskIdentity.From(d), PartitionTarget.From(p), label, letter, null, SizeBytes: size, FileSystem: fs);

    private static async Task<DiskInfo> Disk(WmiDiskDiscoveryService discovery, int n) => (await discovery.DiscoverAsync()).State.Disk(n);

    /// <summary>The data partition (the one with a drive letter and NTFS) on the test disk.</summary>
    private static async Task<PartitionInfo> Single(WmiDiskDiscoveryService discovery, int n) =>
        (await Disk(discovery, n)).Partitions.Where(p => p.FileSystem == "NTFS" && p.DriveLetter is not null).OrderBy(p => p.OffsetBytes).First();

    private static Dictionary<string, string> WriteFiles(char letter)
    {
        var rng = new Random(42);
        var result = new Dictionary<string, string>();
        foreach (var (name, size) in new[] { ("a.bin", 1 << 20), ("b.bin", 5 << 20), (@"dir\c.bin", 20 << 20) })
        {
            var data = new byte[size];
            rng.NextBytes(data);
            var path = $@"{letter}:\{name}";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.WriteThrough)) { fs.Write(data); fs.Flush(true); }
            result[name] = Convert.ToHexString(SHA256.HashData(data));
        }
        return result;
    }

    private static Dictionary<string, string> HashFiles(char letter) =>
        new[] { "a.bin", "b.bin", @"dir\c.bin" }.ToDictionary(n => n, n => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes($@"{letter}:\{n}"))));

    private void Integrity(string checkpoint, Dictionary<string, string> expected, char letter)
    {
        Dictionary<string, string> now;
        try { now = HashFiles(letter); }
        catch (Exception ex) { _integrity.AppendLine($"| {checkpoint} | – | FAIL ({ex.Message}) |"); _failures.Add("integrity " + checkpoint); return; }
        var ok = expected.All(kv => now.TryGetValue(kv.Key, out var h) && h == kv.Value);
        _integrity.AppendLine($"| {checkpoint} | {expected.Count} files, {string.Join(", ", expected.Keys)} | {(ok ? "PASS, all SHA-256 match" : "FAIL, hash mismatch")} |");
        if (!ok) _failures.Add("integrity " + checkpoint);
    }

    private static string Describe(PsPart? p) => p is null ? "not found" : $"#{p.PartitionNumber} {p.Size:N0} B at {p.Offset:N0}, {(p.FileSystem == "" ? "RAW" : p.FileSystem)} '{p.Label}' {(p.DriveLetter == "" ? "no letter" : p.DriveLetter + ":")}";

    /// <summary>Independent view of a disk's layout through the Storage PowerShell module (not the app's own code).</summary>
    private static List<PsPart> PsLayout(int disk)
    {
        var json = Ps($"@(Get-Partition -DiskNumber {disk} | ForEach-Object {{ $v = $_ | Get-Volume -ErrorAction SilentlyContinue; " +
                      "[pscustomobject]@{ PartitionNumber = $_.PartitionNumber; Offset = [int64]$_.Offset; Size = [int64]$_.Size; " +
                      "DriveLetter = $(if ($_.DriveLetter -and [int]$_.DriveLetter -ne 0) { [string]$_.DriveLetter } else { '' }); " +
                      "FileSystem = $(if ($v) { [string]$v.FileSystem } else { '' }); Label = $(if ($v) { [string]$v.FileSystemLabel } else { '' }) } }) | ConvertTo-Json -Compress");
        if (string.IsNullOrWhiteSpace(json)) return [];
        json = json.Trim();
        if (!json.StartsWith('[')) json = "[" + json + "]";
        return JsonSerializer.Deserialize<List<PsPart>>(json)!;
    }

    private static string Ps(string script)
    {
        using var p = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        })!;
        var output = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 || err.Trim().Length > 0) throw new InvalidOperationException("PowerShell failed: " + err);
        return output;
    }
}
