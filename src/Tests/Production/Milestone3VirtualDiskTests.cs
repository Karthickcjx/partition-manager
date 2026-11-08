using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Layout;
using PartitionManager.Core.Logging;
using PartitionManager.Core.Models;
using PartitionManager.Core.Safety;
using PartitionManager.Infrastructure.WindowsApi;
using PartitionManager.PrivilegedHelper.Engine;
using Xunit;

namespace PartitionManager.Tests.Production;

public class Milestone3VirtualDiskTests
{
    [SkippableFact]
    [Trait("Category", "VirtualDisk")]
    public async Task Durable_journal_records_real_GPT_and_MBR_initialization_and_metadata()
    {
        RequireOptIn();
        var discovery=new WmiDiskDiscoveryService(NullOperationLog.Instance);
        var rows=new StringBuilder("# Milestone 4 durable VHDX journal\n\n");
        foreach(var style in new[] { PartitionStyle.Gpt,PartitionStyle.Mbr })
        {
            using var image=TestVirtualDisk.Create(Root,$"m4-journal-{Guid.NewGuid():N}.vhdx",512L<<20);
            var folder=Path.Combine(ReportDir,$"m4-journal-{style}-{Guid.NewGuid():N}"); Directory.CreateDirectory(folder);
            using var journal=new FileOperationJournal(folder);
            try
            {
                image.Attach(); var blank=await Ours(discovery,image);
                var engine=new WriteEngine(discovery,new WmiStorageWriteBackend(),new FixedHelperPolicySource(new HelperPolicy(WriteMode.VirtualDisksOnly,[Root])
                    { InitializationApprovals=[new(DiskIdentity.From(blank),DateTimeOffset.UtcNow)] }),new MemoryAudit()) { Journal=journal };
                var initialized=engine.Execute(Guid.NewGuid(),new(WriteOperationKind.InitializeDisk,DiskIdentity.From(blank),null,null,null,null,NewStyle:style));
                Assert.True(initialized.Status==HelperResponseStatus.Ok,initialized.Message);
                var disk=await Ours(discovery,image);
                var create=engine.Execute(Guid.NewGuid(),new(WriteOperationKind.CreatePartition,DiskIdentity.From(disk),null,"JournalTest",null,null,OffsetBytes:32L<<20,SizeBytes:256L<<20,FileSystem:"NTFS"));
                Assert.True(create.Status==HelperResponseStatus.Ok,create.Message);
                disk=await Ours(discovery,image); var p=disk.Partitions.Single(x=>x.FileSystem=="NTFS");
                var label=engine.Execute(Guid.NewGuid(),new(WriteOperationKind.SetVolumeLabel,DiskIdentity.From(disk),PartitionTarget.From(p),"VerifiedJournal",null,null));
                Assert.True(label.Status==HelperResponseStatus.Ok,label.Message);
                Assert.Equal("VerifiedJournal",Ps($"Get-Partition -DiskNumber {disk.Number} -PartitionNumber {p.PartitionNumber} | Get-Volume | Select-Object -ExpandProperty FileSystemLabel").Trim());
                var records=FileOperationJournal.Inspect(folder);
                Assert.Equal(3,records.Count); Assert.All(records,r=>{Assert.True(r.Valid);Assert.False(r.RequiresReview);});
                rows.AppendLine($"- {style}: PASS; initialize/create-format/label, independent volume label query, 3 valid terminal Verified journals in {Path.GetFileName(folder)}.");
            }
            finally { image.Detach(); if(TestVirtualDisk.ReadVhdxId(image.Path)==image.VhdxId) File.Delete(image.Path); }
        }
        File.WriteAllText(Path.Combine(ReportDir,"m4-journal-vhdx.md"),rows.ToString());
    }
    private const string Root = @"D:\PartitionManager-TestDisks";
    private static string ReportDir => Environment.GetEnvironmentVariable("PM_VHD_REPORT_DIR") ?? Root;

    private static void RequireOptIn() => Skip.IfNot(Elevation.IsElevated && Environment.GetEnvironmentVariable("PM_VHD_TESTS") == "1", "Requires elevated, explicitly enabled disposable VHDX test run.");

    private static async Task<DiskInfo> Ours(WmiDiskDiscoveryService discovery, TestVirtualDisk image)
    {
        var disk = (await discovery.DiscoverAsync()).State.Disk(image.DiskNumber!.Value);
        Assert.True(disk.IsVirtual && disk.Number != 0 && !disk.HostsRunningSystem);
        Assert.Equal(image.Path, disk.VirtualImagePath, ignoreCase: true);
        Assert.Equal(image.VhdxId, TestVirtualDisk.ReadVhdxId(image.Path));
        return disk;
    }

    private static WriteEngine ApprovedEngine(WmiDiskDiscoveryService discovery, DiskInfo blank, MemoryAudit audit) =>
        new(discovery, new WmiStorageWriteBackend(), new FixedHelperPolicySource(new HelperPolicy(WriteMode.VirtualDisksOnly, [Root])
        { InitializationApprovals = [new(DiskIdentity.From(blank), DateTimeOffset.UtcNow)] }), audit);

    [SkippableFact]
    [Trait("Category", "VirtualDisk")]
    public async Task Initialize_new_approved_images_as_GPT_and_MBR()
    {
        RequireOptIn();
        var discovery = new WmiDiskDiscoveryService(NullOperationLog.Instance);
        var rows = new StringBuilder("# Milestone 3 initialization (real VHDX)\n\n");
        foreach (var style in new[] { PartitionStyle.Gpt, PartitionStyle.Mbr })
        {
            using var image = TestVirtualDisk.Create(Root, $"m3-init-{style}-{Guid.NewGuid():N}.vhdx", 256L << 20);
            try
            {
                image.Attach();
                var blank = await Ours(discovery, image);
                Assert.Equal(PartitionStyle.Raw, blank.Style);
                Assert.Empty(blank.Partitions);
                var audit = new MemoryAudit(); var engine = ApprovedEngine(discovery, blank, audit);
                var request = new WriteOperationRequest(WriteOperationKind.InitializeDisk, DiskIdentity.From(blank), null, null, null, null, NewStyle: style);
                var result = engine.Execute(Guid.NewGuid(), request);
                Assert.True(result.Status == HelperResponseStatus.Ok, result.Code + ": " + result.Message);
                var independent = Ps($"(Get-Disk -Number {blank.Number}).PartitionStyle").Trim();
                Assert.Equal(style.ToString(), independent, ignoreCase: true);
                Assert.Contains(audit.Entries, e => e.Outcome == "SUCCEEDED");
                Assert.NotEqual(HelperResponseStatus.Ok, engine.Execute(Guid.NewGuid(), request).Status);
                rows.AppendLine($"- {style}: PASS, helper engine initialization; independent Get-Disk={independent}; repeated request refused; image ID {image.VhdxId}.");
            }
            finally { image.Detach(); if (TestVirtualDisk.ReadVhdxId(image.Path) == image.VhdxId) File.Delete(image.Path); File.WriteAllText(Path.Combine(ReportDir,"m3-initialization.md"),rows.ToString()); }
        }
    }

    [SkippableFact]
    [Trait("Category", "VirtualDisk")]
    public async Task Large_sparse_volume_many_files_and_bounded_hash_verification()
    {
        RequireOptIn();
        var discovery = new WmiDiskDiscoveryService(NullOperationLog.Instance);
        using var image = TestVirtualDisk.Create(Root, $"m3-large-{Guid.NewGuid():N}.vhdx", 16L << 30);
        try
        {
            image.Attach(); var blank = await Ours(discovery,image); var engine = ApprovedEngine(discovery,blank,new());
            var init = engine.Execute(Guid.NewGuid(),new(WriteOperationKind.InitializeDisk,DiskIdentity.From(blank),null,null,null,null,NewStyle:PartitionStyle.Gpt));
            Assert.True(init.Status == HelperResponseStatus.Ok,init.Message);
            var d = await Ours(discovery,image);
            var used = (await discovery.DiscoverAsync()).State.UsedLetters;
            var letter = Enumerable.Range('G',20).Select(c => (char)c).Reverse().First(c => !used.Contains(c) && !Directory.Exists($"{c}:\\"));
            var create = engine.Execute(Guid.NewGuid(),new(WriteOperationKind.CreatePartition,DiskIdentity.From(d),null,"M3Synthetic",letter,null,
                OffsetBytes:16L<<20,SizeBytes:8L<<30,FileSystem:"NTFS"));
            Assert.True(create.Status == HelperResponseStatus.Ok,create.Message);
            d = await Ours(discovery,image); var partition = d.Partitions.Single(p=>p.DriveLetter==letter);
            var folder = $"{letter}:\\synthetic"; Directory.CreateDirectory(folder);
            var expected = new Dictionary<string,string>();
            var buffer = new byte[1<<20]; new Random(730).NextBytes(buffer);
            var names = Enumerable.Range(0,1000).Select(i=>$"many\\file-{i:D4}.bin").Concat(new[] {
                "Unicode-文件-☃\\readonly.bin", string.Join('\\',Enumerable.Range(0,35).Select(i=>$"level{i:D2}"))+"\\deep.bin", "large.bin" });
            foreach(var relative in names)
            {
                var path = Path.Combine(folder,relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using(var file = new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,buffer.Length))
                {
                    if(relative=="large.bin") for(var i=0;i<128;i++) file.Write(buffer);
                    else file.Write(buffer,0,1024);
                    file.Flush(true);
                }
                if(relative.EndsWith("readonly.bin")) File.SetAttributes(path,FileAttributes.ReadOnly);
                using var input = File.OpenRead(path); expected[relative]=Convert.ToHexString(SHA256.HashData(input));
            }
            var min = new WmiStorageWriteBackend().GetSupportedSize(d.Number,partition.PartitionNumber).Min;
            var size = DiskLayout.AlignUp(Math.Max(4L<<30,min)); Assert.True(size<partition.SizeBytes);
            var shrink = engine.Execute(Guid.NewGuid(),new(WriteOperationKind.ShrinkPartition,DiskIdentity.From(d),PartitionTarget.From(partition),null,null,null,SizeBytes:size));
            Assert.True(shrink.Status==HelperResponseStatus.Ok,shrink.Message);
            Assert.Equal(size,long.Parse(Ps($"(Get-Partition -DiskNumber {d.Number} -PartitionNumber {partition.PartitionNumber}).Size").Trim()));
            foreach(var (relative,hash) in expected) { using var file=File.OpenRead(Path.Combine(folder,relative)); Assert.Equal(hash,Convert.ToHexString(SHA256.HashData(file))); }
            File.WriteAllText(Path.Combine(ReportDir,"m3-large-integrity.md"),$"# Large sparse VHDX\n\nPASS: 16 GiB dynamic VHDX, 8 GiB NTFS shrunk to {size} bytes through helper; independent Get-Partition check.\n1003 files re-read and SHA-256 matched, including a 128 MiB file, Unicode, read-only and >260-character deep path.\n1 MiB write buffer; streaming hash reads. Image file length before cleanup: {new FileInfo(image.Path).Length}.\nNot tested: near-full/fragmented media, device I/O fault, power loss.\n");
        }
        finally { image.Detach(); if(TestVirtualDisk.ReadVhdxId(image.Path)==image.VhdxId) File.Delete(image.Path); }
    }

    [SkippableFact]
    [Trait("Category", "VirtualDisk")]
    public async Task Layout_matched_virtual_rehearsal_refuses_every_protected_target()
    {
        RequireOptIn();
        var discovery = new WmiDiskDiscoveryService(NullOperationLog.Instance);
        var host = (await discovery.DiscoverAsync()).State.Disks.Single(d=>d.Partitions.Any(p=>p.DriveLetter=='E'));
        const long geometryShift = 32L << 20; // Windows reserves a prefix when creating GPT data partitions.
        using var image = TestVirtualDisk.Create(Root,$"m3-layout-{Guid.NewGuid():N}.vhdx",host.SizeBytes + 2 * geometryShift);
        try
        {
            image.Attach(); var blank=await Ours(discovery,image); var engine=ApprovedEngine(discovery,blank,new());
            var init=engine.Execute(Guid.NewGuid(),new(WriteOperationKind.InitializeDisk,DiskIdentity.From(blank),null,null,null,null,NewStyle:PartitionStyle.Gpt));
            Assert.True(init.Status==HelperResponseStatus.Ok,init.Message);
            // Geometry only: all on-disk entries are synthetic BasicData, unformatted and unmounted.
            // Protected roles and C/D/E labels exist only in the rehearsal model.
            foreach(var p in host.Partitions.OrderBy(p=>p.OffsetBytes))
            {
                await Ours(discovery,image);
                Ps($"New-Partition -DiskNumber {blank.Number} -Offset {p.OffsetBytes + geometryShift} -Size {p.SizeBytes} -GptType '{{{PartitionTypes.BasicData}}}' | Out-Null");
            }
            var replica=await Ours(discovery,image);
            Assert.Equal(host.Partitions.Count,replica.Partitions.Count(p => p.OffsetBytes >= geometryShift));
            var geometryEvidence = Ps($"Get-Partition -DiskNumber {blank.Number} | Select-Object PartitionNumber,Offset,Size,GptType | ConvertTo-Json");
            File.WriteAllText(Path.Combine(ReportDir,"m3-replica-geometry.json"),geometryEvidence);
            var sizeAdjustments = new List<string>();
            foreach(var original in host.Partitions.Where(p=>p.DriveLetter is 'C' or 'D' or 'E'))
            {
                var fresh=await Ours(discovery,image);
                var part=fresh.Partitions.Single(p=>p.OffsetBytes==original.OffsetBytes+geometryShift);
                Assert.InRange(Math.Abs(part.SizeBytes-original.SizeBytes),0,(1<<20)-1);
                sizeAdjustments.Add($"{original.DriveLetter}: size delta {part.SizeBytes-original.SizeBytes} bytes");
                var formatted=engine.Execute(Guid.NewGuid(),new(WriteOperationKind.FormatVolume,DiskIdentity.From(fresh),PartitionTarget.From(part),"Synthetic",null,null,FileSystem:"NTFS"));
                Assert.True(formatted.Status==HelperResponseStatus.Ok,formatted.Message);
                fresh=await Ours(discovery,image); part=fresh.Partitions.Single(p=>p.OffsetBytes==part.OffsetBytes);
                var letters=(await discovery.DiscoverAsync()).State.UsedLetters;
                var letter=Enumerable.Range('G',20).Select(c=>(char)c).Reverse().First(c=>!letters.Contains(c));
                var assign=engine.Execute(Guid.NewGuid(),new(WriteOperationKind.AssignDriveLetter,DiskIdentity.From(fresh),PartitionTarget.From(part),null,letter,null));
                Assert.True(assign.Status==HelperResponseStatus.Ok,assign.Message);
                var bytes=Encoding.UTF8.GetBytes("Synthetic layout rehearsal: " + original.DriveLetter);
                var path=$"{letter}:\\synthetic-only.txt";
                File.WriteAllBytes(path,bytes);
                using(var file=File.OpenRead(path)) Assert.Equal(SHA256.HashData(bytes),SHA256.HashData(file));
                fresh=await Ours(discovery,image); part=fresh.Partitions.Single(p=>p.OffsetBytes==part.OffsetBytes);
                var remove=engine.Execute(Guid.NewGuid(),new(WriteOperationKind.RemoveDriveLetter,DiskIdentity.From(fresh),PartitionTarget.From(part),null,null,letter));
                Assert.True(remove.Status==HelperResponseStatus.Ok,remove.Message);
            }
            replica=await Ours(discovery,image);
            replica=replica.WithPartitions(replica.Partitions.Select(p=> {
                if (p.OffsetBytes < geometryShift) return p;
                var original=host.Partitions.Single(x=>x.OffsetBytes + geometryShift==p.OffsetBytes);
                Assert.InRange(Math.Abs(p.SizeBytes-original.SizeBytes),0,(1<<20)-1);
                return p with { Role=original.Role,GptType=original.GptType,DriveLetter=original.DriveLetter,FileSystem=original.DriveLetter is null ? "" : "NTFS",IsBoot=original.IsBoot,IsSystem=original.IsSystem };
            }));
            var policy=EScopeRehearsal.Inspect([replica]); var count=0;
            var target=replica.Partitions.Single(p=>p.DriveLetter=='E');
            var region=new RegionRehearsal(replica,target.UniqueGuid!.Value);
            var proposedShrink=replica.WithPartitions(replica.Partitions.Select(p=>p==target?p with {SizeBytes=p.SizeBytes-(1L<<30)}:p));
            var accepted=region.ValidateAndAdvance([replica],proposedShrink,new(Guid.NewGuid(),region.ConfirmationHash(proposedShrink),DateTimeOffset.UtcNow),DateTimeOffset.UtcNow);
            Assert.True(accepted.IsValid,accepted.Message);
            Assert.Equal(target.OffsetBytes,region.OriginalStart); Assert.Equal(target.EndBytes,region.OriginalEndExclusive);
            foreach(var protectedPart in replica.Partitions.Where(p=>p.DriveLetter!='E'))
            {
                var proposal=replica.WithPartitions(replica.Partitions.Where(p=>p.UniqueGuid!=protectedPart.UniqueGuid));
                var result=new EScopeRehearsal().EvaluateShrink(policy,[replica],proposal,new(Guid.NewGuid(),EScopeRehearsal.ProposalHash(policy,proposal),DateTimeOffset.UtcNow),DateTimeOffset.UtcNow);
                Assert.False(result.IsValid); count++;
            }
            File.WriteAllText(Path.Combine(ReportDir,"m3-layout-rehearsal.md"),$"# Layout-matched virtual rehearsal\n\nPASS: dynamic VHDX size {host.SizeBytes + 2 * geometryShift}; {host.Partitions.Count} source extents recreated with a uniform {geometryShift}-byte offset shift for Windows' reserved GPT prefix and provider size rounding below 1 MiB. {string.Join("; ",sizeAdjustments)}. Synthetic entries use BasicData. Windows' own MSR is preserved. No host data copied, no real C/D/E letters assigned.\nThree corresponding data partitions formatted NTFS, each seeded with one synthetic file and SHA-256 checked, using temporary G-Z letters only.\n{count}/{count} proposed protected-partition deletions rejected by design-only evaluator. Host roles are model annotations, not on-disk types.\nNo physical-write authorization exists. No real E-scope operation or power-loss rehearsal is claimed.\n");
        }
        finally { image.Detach(); if(TestVirtualDisk.ReadVhdxId(image.Path)==image.VhdxId) File.Delete(image.Path); }
    }

    private static string Ps(string command)
    {
        using var p=Process.Start(new ProcessStartInfo("powershell.exe") { ArgumentList={"-NoProfile","-NonInteractive","-Command","$ErrorActionPreference='Stop'; "+command},UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true })!;
        var output=p.StandardOutput.ReadToEnd(); var error=p.StandardError.ReadToEnd(); p.WaitForExit();
        if(p.ExitCode!=0 || !string.IsNullOrWhiteSpace(error)) throw new IOException(error);
        return output;
    }
}
