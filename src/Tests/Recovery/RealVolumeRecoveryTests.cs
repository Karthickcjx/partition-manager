using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PartitionManager.Core.Logging;
using PartitionManager.Core.Recovery;
using PartitionManager.Infrastructure.WindowsApi;
using PartitionManager.Tests.Production;
using Xunit;

namespace PartitionManager.Tests.Recovery;

/// <summary>
/// Real recovery on a real NTFS volume (default E:). The test only creates and deletes its own files through normal
/// file APIs; the volume itself is read raw and never written. Recovered files go to a folder on another volume.
/// Needs admin and PM_RECOVERY_TESTS=1.
/// </summary>
public class RealVolumeRecoveryTests
{
    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

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
        if (p.ExitCode != 0 || err.Length > 0) throw new InvalidOperationException("PowerShell failed: " + err);
        return output;
    }

    [SkippableFact]
    [Trait("Category", "RealVolumeRecovery")]
    public async Task Deleted_files_on_a_real_volume_are_recovered_bit_exact()
    {
        Skip.IfNot(Elevation.IsElevated, "Requires administrator rights for raw volume reads.");
        Skip.IfNot(Environment.GetEnvironmentVariable("PM_RECOVERY_TESTS") == "1", "Set PM_RECOVERY_TESTS=1 to run real recovery tests.");

        var source = Environment.GetEnvironmentVariable("PM_RECOVERY_SOURCE");
        Skip.If(string.IsNullOrWhiteSpace(source), "An explicit disposable virtual source is required; there is no default.");
        var letter = char.ToUpperInvariant(source![0]);
        Assert.DoesNotContain(letter, new[] { 'C', 'D', 'E' });
        var outputRoot = Env("PM_RECOVERY_OUTPUT", @"D:\PartitionManager-Recovered");
        var reportDir = Env("PM_VHD_REPORT_DIR", Path.Combine(outputRoot, "reports"));
        Directory.CreateDirectory(reportDir);
        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var report = new StringBuilder($"# Recovery run {runId}\n\nSource volume {letter}: (read raw, never written). Output: {outputRoot}\n\n| Step | Result | Detail |\n|---|---|---|\n");
        void Row(string step, bool ok, string detail) => report.AppendLine($"| {step} | {(ok ? "PASS" : "FAIL")} | {detail.Replace("|", "/")} |");
        var failures = new List<string>();

        var discovery = new WmiDiskDiscoveryService(NullOperationLog.Instance);
        var state = (await discovery.DiscoverAsync()).State;
        var sourceDisk = state.Disks.Single(d => d.Partitions.Any(p => p.DriveLetter == letter));
        Assert.True(sourceDisk.IsVirtual && new Core.Execution.HelperPolicy(Core.Execution.WriteMode.VirtualDisksOnly,
            [@"D:\PartitionManager-TestDisks"]).AllowsImage(sourceDisk.VirtualImagePath), "Recovery test refuses physical or unapproved source volumes.");
        var partition = state.Disks.SelectMany(d => d.Partitions).Single(p => p.DriveLetter == letter);
        Assert.Equal("NTFS", partition.FileSystem);
        Directory.CreateDirectory(outputRoot);
        Assert.False(VolumeIdentity.IsSameVolume(partition.VolumePath, outputRoot), "output folder must be on a different volume");

        // 1. Create files with known content.
        var folder = $@"{letter}:\PM-RecoveryTest-{runId}";
        var rng = new Random(int.Parse(runId[^6..]));
        var specs = new (string Rel, int Size)[] { ("tiny.txt", 300), ("doc.bin", 1 << 20), (@"sub\photo.jpg", 3 << 20), ("video.bin", 24 << 20) };
        var expected = new Dictionary<string, string>();
        foreach (var (rel, size) in specs)
        {
            var data = new byte[size];
            rng.NextBytes(data);
            var path = Path.Combine(folder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.WriteThrough))
            {
                fs.Write(data);
                fs.Flush(true);
            }
            expected[Path.GetFileName(rel)] = Convert.ToHexString(SHA256.HashData(data));
        }
        Row("Create test files", true, string.Join(", ", specs.Select(s => $"{s.Rel} ({s.Size:N0} B)")));

        // 2. Delete them (and their folders) the normal way, then flush the volume cache.
        Directory.Delete(folder, recursive: true);
        Ps($"Write-VolumeCache -DriveLetter {letter}");
        Row("Delete files and folders, flush volume cache", !Directory.Exists(folder), folder);

        // 3. Scan raw (read-only) and recover to another volume.
        NtfsScanResult scan;
        using (var reader = RawVolumeReader.Open(letter.ToString(), partition.SizeBytes))
        {
            scan = NtfsRecoveryScanner.Scan(reader);
            Row($"Scan {letter}: MFT", true, $"{scan.RecordsScanned:N0} records, {scan.Files.Count:N0} deleted files, {scan.CorruptRecords} corrupt, {scan.Elapsed.TotalMilliseconds:N0} ms");

            var dest = Path.Combine(outputRoot, runId);
            foreach (var (rel, size) in specs)
            {
                var name = Path.GetFileName(rel);
                var hit = scan.Files.Where(f => f.Name == name && f.FullPath.Contains($"PM-RecoveryTest-{runId}", StringComparison.Ordinal)).ToList();
                if (hit.Count != 1)
                {
                    Row($"Find {rel}", false, $"{hit.Count} matches");
                    failures.Add($"find {rel}");
                    continue;
                }
                var f = hit[0];
                var sw = Stopwatch.StartNew();
                var r = FileRecoverer.Recover(reader, scan.Boot, f, dest);
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(r.OutputPath)));
                var ok = hash == expected[name] && r.BytesWritten == size;
                Row($"Recover {rel}", ok, $"path {f.FullPath}, chance {f.Chance}, {(f.Data?.IsResident == true ? "resident" : $"{f.Data?.Runs.Count} run(s)")}, " +
                                          $"{r.BytesWritten:N0} B in {sw.ElapsedMilliseconds} ms, SHA-256 {(ok ? "matches" : "MISMATCH")}");
                if (!ok) failures.Add($"hash {rel}");
            }
        }

        // 4. Read-only benchmark of a busy volume (counts and time only, no file names recorded).
        var bench = Env("PM_RECOVERY_BENCH", "D");
        var benchPart = state.Disks.SelectMany(d => d.Partitions).FirstOrDefault(p => p.DriveLetter == char.ToUpperInvariant(bench[0]) && p.FileSystem == "NTFS");
        if (benchPart is not null)
        {
            using var reader = RawVolumeReader.Open(bench[..1], benchPart.SizeBytes);
            var b = NtfsRecoveryScanner.Scan(reader);
            Row($"Benchmark scan {bench[0]}: ({ByteSize(benchPart.SizeBytes)})", true,
                $"{b.RecordsScanned:N0} MFT records, {b.Files.Count:N0} deleted files found, {b.Elapsed.TotalSeconds:0.00} s ({b.RecordsScanned / Math.Max(0.001, b.Elapsed.TotalSeconds):N0} records/s)");
        }

        File.WriteAllText(Path.Combine(reportDir, $"RecoveryRun-{runId}.md"), report.ToString());
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [SkippableFact]
    [Trait("Category", "RealVolumeRecovery")]
    public async Task Deleted_partition_on_a_virtual_disk_is_found_at_its_original_place()
    {
        Skip.IfNot(Elevation.IsElevated, "Requires administrator rights to attach a VHDX.");
        Skip.IfNot(Environment.GetEnvironmentVariable("PM_RECOVERY_TESTS") == "1", "Set PM_RECOVERY_TESTS=1 to run real recovery tests.");

        var folder = Path.GetFullPath(Env("PM_VHD_TEST_DIR", @"D:\PartitionManager-TestDisks"));
        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var discovery = new WmiDiskDiscoveryService(NullOperationLog.Instance);
        var before = (await discovery.DiscoverAsync()).State.Disks.Select(d => d.Number).ToHashSet();
        var vhd = TestVirtualDisk.Create(folder, $"pm-{runId}-lost.vhdx", 512L << 20);
        try
        {
            var n = vhd.Attach();
            var disk = (await discovery.DiscoverAsync()).State.Disk(n);
            Assert.True(!before.Contains(n) && n != 0 && disk.IsVirtual
                        && string.Equals(Path.GetFullPath(disk.VirtualImagePath), vhd.Path, StringComparison.OrdinalIgnoreCase), "attached disk is not ours");

            Ps($"Initialize-Disk -Number {n} -PartitionStyle GPT; $p = New-Partition -DiskNumber {n} -Size 256MB; $p | Format-Volume -FileSystem NTFS -NewFileSystemLabel LOSTTEST -Confirm:$false | Out-Null");
            var created = (await discovery.DiscoverAsync()).State.Disk(n).Partitions.Single(p => p.FileSystem == "NTFS");
            Ps($"Remove-Partition -DiskNumber {n} -PartitionNumber {created.PartitionNumber} -Confirm:$false");
            var afterDelete = (await discovery.DiscoverAsync()).State.Disk(n);
            Assert.DoesNotContain(afterDelete.Partitions, p => p.OffsetBytes == created.OffsetBytes);

            using var reader = RawDiskReader.Open(n, afterDelete.LogicalSectorSize, afterDelete.SizeBytes);
            var sw = Stopwatch.StartNew();
            var found = LostPartitionScanner.ScanUnallocated(reader, afterDelete);
            var hit = Assert.Single(found);
            Assert.Equal(created.OffsetBytes, hit.OffsetBytes);
            Assert.Equal("NTFS", hit.FileSystem);
            Assert.Equal(FoundConfidence.High, hit.Confidence);
            Assert.Equal(created.SizeBytes, hit.SizeBytes);
            File.AppendAllText(Path.Combine(Env("PM_VHD_REPORT_DIR", folder), $"RecoveryRun-{runId}-lostpartition.md"),
                $"# Lost partition run {runId}\n\n| Step | Result | Detail |\n|---|---|---|\n" +
                $"| Create 256 MB NTFS partition on VHDX Disk {n}, then delete it (Windows cmdlets, setup) | done | offset {created.OffsetBytes:N0}, size {created.SizeBytes:N0} |\n" +
                $"| Scan unallocated space | PASS | found {hit.FileSystem} at {hit.OffsetBytes:N0}, size {hit.SizeBytes:N0}, confidence {hit.Confidence}, {sw.ElapsedMilliseconds} ms |\n");
        }
        finally
        {
            vhd.Detach();
            if (TestVirtualDisk.ReadVhdxId(vhd.Path) == vhd.VhdxId) File.Delete(vhd.Path);
        }
    }

    private static string ByteSize(long b) => Core.Models.ByteSize.Format(b);
}
