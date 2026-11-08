using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Logging;
using PartitionManager.Core.Models;
using PartitionManager.Infrastructure.WindowsApi;
using PartitionManager.PrivilegedHelper.Engine;
using Xunit;

namespace PartitionManager.Tests.Production;

/// <summary>
/// Disposable VHDX created by this test run through the documented Virtual Disk API (virtdisk.dll). Each image gets
/// a fresh VHDX identifier; cleanup re-reads that identifier from the file and deletes it only if it matches.
/// </summary>
internal sealed class TestVirtualDisk : IDisposable
{
    public string Path { get; }
    public Guid VhdxId { get; }
    public int? DiskNumber { get; private set; }
    private SafeFileHandle? _attached;

    private TestVirtualDisk(string path, Guid id) { Path = path; VhdxId = id; }

    public static TestVirtualDisk Create(string folder, string name, long sizeBytes)
    {
        folder = System.IO.Path.GetFullPath(folder);
        if (!string.Equals(folder.TrimEnd('\\'), @"D:\PartitionManager-TestDisks", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Disposable VHDX files are restricted to D:\\PartitionManager-TestDisks.");
        if (System.IO.Path.GetFileName(name) != name || !name.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Expected a plain VHDX filename.");
        if (sizeBytes < 64L << 20 || sizeBytes > 1L << 40 || sizeBytes % 512 != 0)
            throw new IOException("Unexpected test image size.");
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse test roots are forbidden.");
        if (new DriveInfo("D").AvailableFreeSpace < 8L << 30) throw new IOException("At least 8 GiB host free space is required.");
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, name);
        if (File.Exists(path)) throw new IOException($"Refusing to overwrite existing file {path}.");
        var id = Guid.NewGuid();
        var storage = new VirtualStorageType { DeviceId = 3 /* VHDX */, VendorId = MicrosoftVendor };
        var parameters = new CreateParametersV1 { Version = 1, UniqueId = id, MaximumSize = (ulong)sizeBytes, SectorSizeInBytes = 512 };
        var rc = CreateVirtualDisk(ref storage, path, 0x003f0000, IntPtr.Zero, 0, 0, ref parameters, IntPtr.Zero, out var handle);
        if (rc != 0) throw new IOException($"CreateVirtualDisk failed with {rc}");
        handle.Dispose();
        // Identity for cleanup is whatever the file itself reports, read back immediately after creation.
        return new TestVirtualDisk(path, ReadVhdxId(path));
    }

    /// <summary>Reads the VHDX virtual disk ID stored in the file metadata.</summary>
    public static Guid ReadVhdxId(string path)
    {
        var storage = new VirtualStorageType { DeviceId = 3, VendorId = MicrosoftVendor };
        var open = new OpenParametersV2 { Version = 2, GetInfoOnly = 1, ReadOnly = 1 };
        var rc = OpenVirtualDiskInfo(ref storage, path, 0, 0, ref open, out var handle);
        if (rc != 0) throw new IOException($"OpenVirtualDisk failed with {rc}");
        using (handle)
        {
            var buffer = new byte[64];
            // GET_VIRTUAL_DISK_INFO_VIRTUAL_DISK_ID (14): the VHDX metadata disk ID. Level 2 (IDENTIFIER) was observed
            // to change between creation and detach, so it cannot serve as a stable identity.
            BitConverter.GetBytes(14).CopyTo(buffer, 0);
            var size = (uint)buffer.Length;
            rc = GetVirtualDiskInformation(handle, ref size, buffer, out _);
            if (rc != 0) throw new IOException($"GetVirtualDiskInformation failed with {rc}");
            return new Guid(buffer.AsSpan(8, 16));
        }
    }

    /// <summary>Attaches without a drive letter and returns the disk number Windows assigned. Requires admin.</summary>
    public int Attach()
    {
        var storage = new VirtualStorageType { DeviceId = 3, VendorId = MicrosoftVendor };
        var open = new OpenParametersV1 { Version = 1 };
        var rc = OpenVirtualDisk(ref storage, Path, 0x003f0000, 0, ref open, out var handle);
        if (rc != 0) throw new IOException($"OpenVirtualDisk failed with {rc}");
        var attach = new AttachParameters { Version = 1 };
        rc = AttachVirtualDisk(handle, IntPtr.Zero, 0x00000002 /* NO_DRIVE_LETTER */, 0, ref attach, IntPtr.Zero);
        if (rc != 0) { handle.Dispose(); throw new IOException($"AttachVirtualDisk failed with {rc}"); }
        _attached = handle;
        var size = 1024;
        var buffer = new char[size / 2];
        rc = GetVirtualDiskPhysicalPath(handle, ref size, buffer);
        if (rc != 0) throw new IOException($"GetVirtualDiskPhysicalPath failed with {rc}");
        var physical = new string(buffer).TrimEnd('\0');
        DiskNumber = int.Parse(physical[(physical.LastIndexOf("PhysicalDrive", StringComparison.OrdinalIgnoreCase) + 13)..]);
        return DiskNumber.Value;
    }

    public void Detach()
    {
        if (_attached is null) return;
        var rc = DetachVirtualDisk(_attached, 0, 0);
        _attached.Dispose();
        _attached = null;
        if (rc != 0) throw new IOException($"DetachVirtualDisk failed with {rc}");
    }

    public void Dispose() { try { Detach(); } catch (IOException) { } }

    private static readonly Guid MicrosoftVendor = new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    [StructLayout(LayoutKind.Sequential)] private struct VirtualStorageType { public uint DeviceId; public Guid VendorId; }
    [StructLayout(LayoutKind.Sequential)] private struct CreateParametersV1 { public uint Version; public uint AlignmentPadding; public Guid UniqueId; public ulong MaximumSize; public uint BlockSizeInBytes; public uint SectorSizeInBytes; public IntPtr ParentPath; public IntPtr SourcePath; }
    [StructLayout(LayoutKind.Sequential)] private struct OpenParametersV1 { public uint Version; public uint RWDepth; }
    [StructLayout(LayoutKind.Sequential)] private struct OpenParametersV2 { public uint Version; public int GetInfoOnly; public int ReadOnly; public Guid ResiliencyGuid; }
    [StructLayout(LayoutKind.Sequential)] private struct AttachParameters { public uint Version; public uint Reserved; }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint CreateVirtualDisk(ref VirtualStorageType type, string path, uint mask, IntPtr sd, uint flags, uint providerFlags, ref CreateParametersV1 p, IntPtr overlapped, out SafeFileHandle handle);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDisk(ref VirtualStorageType type, string path, uint mask, uint flags, ref OpenParametersV1 p, out SafeFileHandle handle);
    [DllImport("virtdisk.dll", EntryPoint = "OpenVirtualDisk", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDiskInfo(ref VirtualStorageType type, string path, uint mask, uint flags, ref OpenParametersV2 p, out SafeFileHandle handle);
    [DllImport("virtdisk.dll")]
    private static extern uint AttachVirtualDisk(SafeFileHandle handle, IntPtr sd, uint flags, uint providerFlags, ref AttachParameters p, IntPtr overlapped);
    [DllImport("virtdisk.dll")]
    private static extern uint DetachVirtualDisk(SafeFileHandle handle, uint flags, uint providerFlags);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetVirtualDiskPhysicalPath(SafeFileHandle handle, ref int size, [Out] char[] path);
    [DllImport("virtdisk.dll")]
    private static extern uint GetVirtualDiskInformation(SafeFileHandle handle, ref uint size, [In, Out] byte[] info, out uint used);
}

/// <summary>Collects every step, setup action and verification into a markdown report and a JSON manifest.</summary>
internal sealed class RunReport
{
    public string RunId { get; } = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    public List<object> Images { get; } = [];
    public List<(string Kind, string Name, string Result, string Detail)> Rows { get; } = [];
    private readonly string _reportDir;

    public RunReport(string reportDir) { _reportDir = reportDir; Directory.CreateDirectory(reportDir); }

    public void Setup(string name, string detail) => Rows.Add(("Setup (Windows cmdlet, not a product test)", name, "done", detail));
    public void Guard(string name, bool ok, string detail) => Rows.Add(("Safety guard", name, ok ? "PASS" : "FAIL", detail));
    public void Op(string name, bool ok, string detail) => Rows.Add(("PartitionManager operation", name, ok ? "PASS" : "FAIL", detail));
    public void Cleanup(string name, bool ok, string detail) => Rows.Add(("Cleanup", name, ok ? "PASS" : "FAIL", detail));

    public string Write()
    {
        File.WriteAllText(System.IO.Path.Combine(_reportDir, $"VirtualDiskRun-{RunId}.json"),
            JsonSerializer.Serialize(new { runId = RunId, images = Images, rows = Rows.Select(r => new { r.Kind, r.Name, r.Result, r.Detail }) }, new JsonSerializerOptions { WriteIndented = true }));
        var sb = new StringBuilder();
        sb.AppendLine($"# Virtual-disk integration run {RunId}").AppendLine();
        sb.AppendLine($"Machine: {Environment.MachineName}, elevated: {Elevation.IsElevated}, OS: {Environment.OSVersion}").AppendLine();
        sb.AppendLine("A row passes only if the operation ran and an independent `Get-Partition`/`Get-Volume` query confirmed the result.").AppendLine();
        sb.AppendLine("| Kind | Step | Result | Detail |").AppendLine("|---|---|---|---|");
        foreach (var r in Rows) sb.AppendLine($"| {r.Kind} | {r.Name} | {r.Result} | {r.Detail.Replace("|", "/").ReplaceLineEndings(" ")} |");
        var path = System.IO.Path.Combine(_reportDir, $"VirtualDiskRun-{RunId}.md");
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}

/// <summary>A backend that must never be reached: used for the "Disk 0 is refused" check so a bug cannot write.</summary>
internal sealed class ForbiddenBackend : IStorageWriteBackend
{
    public int Calls { get; private set; }
    public void SetVolumeLabel(int d, int p, string label) { Calls++; throw new InvalidOperationException("write reached the backend"); }
    public void AddDriveLetter(int d, int p, char letter) { Calls++; throw new InvalidOperationException("write reached the backend"); }
    public void RemoveDriveLetter(int d, int p, char letter) { Calls++; throw new InvalidOperationException("write reached the backend"); }
    public (long Min, long Max) GetSupportedSize(int d, int p) { Calls++; throw new InvalidOperationException("backend reached"); }
    public void Resize(int d, int p, long size) { Calls++; throw new InvalidOperationException("write reached the backend"); }
    public int CreatePartition(int d, long offset, long size) { Calls++; throw new InvalidOperationException("write reached the backend"); }
    public void Format(int d, int p, string fs, string label, int cluster, bool full) { Calls++; throw new InvalidOperationException("write reached the backend"); }
    public void DeletePartition(int d, int p) { Calls++; throw new InvalidOperationException("write reached the backend"); }
    public void InitializeDisk(int d, PartitionManager.Core.Models.PartitionStyle s) { Calls++; throw new InvalidOperationException("write reached the backend"); }
    public void SetPartitionReadOnly(int d, int p, bool r) { Calls++; throw new InvalidOperationException("write reached the backend"); }
}

public class VirtualDiskTests
{
    private static string TestFolder => Environment.GetEnvironmentVariable("PM_VHD_TEST_DIR") ?? @"D:\PartitionManager-TestDisks";

    [SkippableFact]
    [Trait("Category", "VirtualDisk")]
    public void Vhdx_file_can_be_created_without_privileges()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("PM_VHD_TESTS") == "1", "VHDX creation requires explicit test mode.");
        var folder = TestFolder;
        var vhd = TestVirtualDisk.Create(folder, $"unit-{Guid.NewGuid():N}.vhdx", 64L * 1024 * 1024);
        try
        {
            Assert.True(File.Exists(vhd.Path));
            Assert.True(new HelperPolicy(WriteMode.VirtualDisksOnly, [folder]).AllowsImage(vhd.Path));
        }
        finally { File.Delete(vhd.Path); }
    }

    /// <summary>
    /// Real run: two throwaway VHDX images (GPT and MBR) in PM_VHD_TEST_DIR, attached, prepared with Windows cmdlets,
    /// then the implemented PartitionManager write operations through the real WriteEngine + WMI backend. Every step
    /// is checked by an independent PowerShell storage query. Needs admin and PM_VHD_TESTS=1.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "VirtualDisk")]
    public async Task Implemented_operations_on_disposable_virtual_disks()
    {
        Skip.IfNot(Elevation.IsElevated, "Requires administrator rights to attach a VHDX.");
        Skip.IfNot(Environment.GetEnvironmentVariable("PM_VHD_TESTS") == "1", "Set PM_VHD_TESTS=1 to run virtual-disk write tests.");

        var folder = System.IO.Path.GetFullPath(TestFolder);
        var report = new RunReport(Environment.GetEnvironmentVariable("PM_VHD_REPORT_DIR") ?? System.IO.Path.Combine(folder, "reports"));
        var discovery = new WmiDiskDiscoveryService(NullOperationLog.Instance);
        var before = (await discovery.DiscoverAsync()).State;

        // Never-write set: the disk holding the test folder (D:) and every disk present before this run.
        var hostLetter = char.ToUpperInvariant(folder[0]);
        var hostDisk = before.Disks.Single(d => d.Partitions.Any(p => p.DriveLetter == hostLetter));
        var preexisting = before.Disks.Select(d => d.Number).ToHashSet();
        report.Guard("Host disk identified", !hostDisk.IsVirtual,
            $"{hostLetter}: is on Disk {hostDisk.Number} ({hostDisk.Model}, S/N {hostDisk.SerialNumber}); pre-existing disks: {string.Join(",", preexisting)}");

        var policy = new FixedHelperPolicySource(new HelperPolicy(WriteMode.VirtualDisksOnly, [folder]));
        var engine = new WriteEngine(discovery, new WmiStorageWriteBackend(), policy, new MemoryAudit());
        var images = new List<TestVirtualDisk>();
        var failures = new List<string>();

        try
        {
            foreach (var style in new[] { "GPT", "MBR" })
            {
                var vhd = TestVirtualDisk.Create(folder, $"pm-{report.RunId}-{style.ToLowerInvariant()}.vhdx", 1L << 30);
                images.Add(vhd);
                report.Images.Add(new { path = vhd.Path, vhdxId = vhd.VhdxId, style, sizeBytes = 1L << 30 });
                var n = vhd.Attach();

                var attached = (await discovery.DiscoverAsync()).State.Disk(n);
                var guardOk = n != hostDisk.Number && n != 0 && !preexisting.Contains(n) && attached.IsVirtual
                              && string.Equals(System.IO.Path.GetFullPath(attached.VirtualImagePath), vhd.Path, StringComparison.OrdinalIgnoreCase);
                report.Guard($"{style} image attached as a new virtual disk", guardOk,
                    $"Disk {n}, bus {attached.BusType}, location '{attached.Location}', UniqueId '{attached.UniqueId}'");
                if (!guardOk) { failures.Add($"guard failed for {style}"); continue; } // never touch a disk we cannot prove is ours

                Ps($"Initialize-Disk -Number {n} -PartitionStyle {style}; " +
                   $"$p = New-Partition -DiskNumber {n} -Size 512MB; $p | Format-Volume -FileSystem NTFS -NewFileSystemLabel PMTEST -Confirm:$false | Out-Null; " +
                   $"Get-Partition -DiskNumber {n} | Where-Object DriveLetter | ForEach-Object {{ Remove-PartitionAccessPath -DiskNumber {n} -PartitionNumber $_.PartitionNumber -AccessPath ($_.DriveLetter + ':\\') }}");
                report.Setup($"{style}: initialize, create 512 MB partition, format NTFS 'PMTEST'", $"Disk {n}");

                await RunOperations(engine, discovery, n, style, report, failures);
            }

            // Disk 0 / host disk must be refused before any write. A backend that throws on use proves nothing was attempted.
            var host = (await discovery.DiscoverAsync()).State.Disk(hostDisk.Number);
            var hostPart = host.Partitions.Single(p => p.DriveLetter == hostLetter);
            var forbidden = new ForbiddenBackend();
            var guardEngine = new WriteEngine(discovery, forbidden, policy, new MemoryAudit());
            var labelBefore = Ps($"(Get-Volume -DriveLetter {hostLetter}).FileSystemLabel").Trim();
            var refusal = guardEngine.Execute(Guid.NewGuid(), new(WriteOperationKind.SetVolumeLabel, DiskIdentity.From(host), PartitionTarget.From(hostPart), "MUST-NOT-APPLY", null, null));
            var labelAfter = Ps($"(Get-Volume -DriveLetter {hostLetter}).FileSystemLabel").Trim();
            var refusedOk = refusal.Status == HelperResponseStatus.Rejected && refusal.Code == "PHYSICAL_DISK" && forbidden.Calls == 0 && labelBefore == labelAfter;
            report.Guard($"Write request for {hostLetter}: (physical Disk {host.Number}) refused", refusedOk,
                $"{refusal.Code}: {refusal.Message}; backend calls {forbidden.Calls}; label '{labelBefore}' → '{labelAfter}'");
            if (!refusedOk) failures.Add("host disk was not refused");
        }
        finally
        {
            foreach (var vhd in images)
            {
                try
                {
                    vhd.Detach();
                    var gone = (await discovery.DiscoverAsync()).State.Disks.All(d => !string.Equals(d.VirtualImagePath, vhd.Path, StringComparison.OrdinalIgnoreCase));
                    report.Cleanup($"Detach {System.IO.Path.GetFileName(vhd.Path)}", gone, gone ? "no longer attached" : "still attached");
                    var idInFile = TestVirtualDisk.ReadVhdxId(vhd.Path);
                    var inFolder = System.IO.Path.GetDirectoryName(vhd.Path)!.Equals(folder, StringComparison.OrdinalIgnoreCase);
                    if (gone && inFolder && idInFile == vhd.VhdxId && System.IO.Path.GetFileName(vhd.Path).StartsWith($"pm-{report.RunId}-", StringComparison.Ordinal))
                    {
                        File.Delete(vhd.Path);
                        report.Cleanup($"Delete {System.IO.Path.GetFileName(vhd.Path)}", !File.Exists(vhd.Path), $"VHDX id {idInFile} matched the manifest");
                    }
                    else
                    {
                        report.Cleanup($"Delete {System.IO.Path.GetFileName(vhd.Path)}", false, $"identity check failed (id {idInFile}, expected {vhd.VhdxId}); file kept");
                        failures.Add("cleanup identity check failed");
                    }
                }
                catch (Exception ex)
                {
                    report.Cleanup($"Cleanup {System.IO.Path.GetFileName(vhd.Path)}", false, ex.Message);
                    failures.Add("cleanup error: " + ex.Message);
                }
            }
            var path = report.Write();
            Console.WriteLine("Report: " + path);
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    private static async Task RunOperations(WriteEngine engine, WmiDiskDiscoveryService discovery, int n, string style, RunReport report, List<string> failures)
    {
        async Task<(DiskInfo Disk, PartitionInfo Part)> Current()
        {
            var d = (await discovery.DiscoverAsync()).State.Disk(n);
            return (d, d.Partitions.Single(p => p.HasFileSystem));
        }

        void Record(string name, HelperResponse resp, bool independentlyVerified, string observed)
        {
            var ok = resp.Status == HelperResponseStatus.Ok && independentlyVerified;
            report.Op($"{style}: {name}", ok, $"helper {resp.Status} {resp.Code}: {resp.Message} | independent query: {observed}");
            if (!ok) failures.Add($"{style} {name}");
        }

        var (d1, p1) = await Current();
        var r1 = engine.Execute(Guid.NewGuid(), new(WriteOperationKind.SetVolumeLabel, DiskIdentity.From(d1), PartitionTarget.From(p1), "PM-RENAMED", null, null));
        var label = Ps($"(Get-Partition -DiskNumber {n} -PartitionNumber {p1.PartitionNumber} | Get-Volume).FileSystemLabel").Trim();
        Record("set label", r1, label == "PM-RENAMED", $"label '{label}'");

        var free = (await discovery.DiscoverAsync()).State.AvailableLetters;
        var a = free[^1];
        var b = free[^2];

        var (d2, p2) = await Current();
        var r2 = engine.Execute(Guid.NewGuid(), new(WriteOperationKind.AssignDriveLetter, DiskIdentity.From(d2), PartitionTarget.From(p2), null, a, null));
        var l2 = Letter(n, p2.PartitionNumber);
        Record($"assign {a}:", r2, l2 == a.ToString(), $"letter '{l2}'");

        var (d3, p3) = await Current();
        var r3 = engine.Execute(Guid.NewGuid(), new(WriteOperationKind.ChangeDriveLetter, DiskIdentity.From(d3), PartitionTarget.From(p3), null, b, a));
        var l3 = Letter(n, p3.PartitionNumber);
        Record($"change {a}: → {b}:", r3, l3 == b.ToString(), $"letter '{l3}'");

        var (d4, p4) = await Current();
        var r4 = engine.Execute(Guid.NewGuid(), new(WriteOperationKind.RemoveDriveLetter, DiskIdentity.From(d4), PartitionTarget.From(p4), null, null, b));
        var l4 = Letter(n, p4.PartitionNumber);
        Record($"remove {b}:", r4, l4 == "", $"letter '{l4}'");

        // Stale identity: a request built before a structural change must be refused (layout fingerprint).
        var (d5, p5) = await Current();
        var stale = new WriteOperationRequest(WriteOperationKind.SetVolumeLabel, DiskIdentity.From(d5), PartitionTarget.From(p5), "STALE", null, null);
        Ps($"New-Partition -DiskNumber {n} -Size 64MB | Out-Null");
        report.Setup($"{style}: add a 64 MB partition behind PartitionManager's back", $"Disk {n}");
        var r5 = engine.Execute(Guid.NewGuid(), stale);
        var labelAfterStale = Ps($"(Get-Partition -DiskNumber {n} -PartitionNumber {p5.PartitionNumber} | Get-Volume).FileSystemLabel").Trim();
        var staleOk = r5.Status == HelperResponseStatus.Rejected && r5.Code == "IDENTITY_LAYOUTCHANGED" && labelAfterStale == "PM-RENAMED";
        report.Guard($"{style}: request with stale layout refused", staleOk, $"{r5.Code}: {r5.Message} | label still '{labelAfterStale}'");
        if (!staleOk) failures.Add($"{style} stale request");
    }

    private static string Letter(int disk, int partition) =>
        Ps($"$l = (Get-Partition -DiskNumber {disk} -PartitionNumber {partition}).DriveLetter; if ($l -and [int]$l -ne 0) {{ [string]$l }} else {{ '' }}").Trim();

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
        if (p.ExitCode != 0 || err.Length > 0) throw new InvalidOperationException($"PowerShell failed: {err}");
        return output;
    }
}
