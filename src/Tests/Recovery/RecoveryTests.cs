using System.Security.Cryptography;
using System.Text;
using PartitionManager.Core.Models;
using PartitionManager.Core.PartitionTables;
using PartitionManager.Core.Recovery;
using Xunit;

namespace PartitionManager.Tests.Recovery;

public class NtfsStructureTests
{
    [Fact]
    public void Boot_sector_parses()
    {
        var img = new NtfsTestImage();
        var boot = NtfsBootSector.TryParse(img.Image.AsSpan(0, 512))!;
        Assert.Equal(512, boot.BytesPerSector);
        Assert.Equal(4096, boot.ClusterSize);
        Assert.Equal(1024, boot.MftRecordSize);
        Assert.Equal(16, boot.MftLcn);
    }

    [Fact]
    public void Non_ntfs_sector_is_rejected()
    {
        Assert.Null(NtfsBootSector.TryParse(new byte[512]));
        var img = new NtfsTestImage();
        img.Image[13] = 3; // sectors per cluster not a power of two
        Assert.Null(NtfsBootSector.TryParse(img.Image.AsSpan(0, 512)));
    }

    [Fact]
    public void Data_runs_decode_relative_negative_and_sparse()
    {
        // 0x18 clusters at LCN 0x5634, then 8 clusters at -16 relative, then 5 sparse clusters.
        var runs = DataRuns.Decode([0x21, 0x18, 0x34, 0x56, 0x11, 0x08, 0xF0, 0x01, 0x05, 0x00]);
        Assert.Equal(3, runs.Count);
        Assert.Equal(new DataRun(0x5634, 0x18), runs[0]);
        Assert.Equal(new DataRun(0x5634 - 16, 8), runs[1]);
        Assert.True(runs[2].IsSparse);
        Assert.Equal(5, runs[2].ClusterCount);
    }

    [Theory]
    [InlineData(new byte[] { 0x09, 0x01 })]                   // length field claims 9 bytes
    [InlineData(new byte[] { 0x21, 0x10 })]                   // truncated
    [InlineData(new byte[] { 0x11, 0x00, 0x05, 0x00 })]       // zero length
    [InlineData(new byte[] { 0x11, 0x04, 0x80, 0x00 })]       // negative LCN
    public void Malformed_runs_are_rejected(byte[] runs) =>
        Assert.Throws<NtfsFormatException>(() => DataRuns.Decode(runs, maxClusters: 1_000_000));

    [Fact]
    public void Run_outside_volume_is_rejected() =>
        Assert.Throws<NtfsFormatException>(() => DataRuns.Decode(NtfsTestImage.EncodeRuns([(5000, 10)]), maxClusters: 4096));

    [Fact]
    public void Torn_record_is_rejected()
    {
        var img = new NtfsTestImage();
        img.AddRecord(40, inUse: false, dir: false, seq: 2, attrs: [NtfsTestImage.FileName(5, 5, "x.txt")], tear: true);
        var raw = img.Image.AsSpan((int)(NtfsTestImage.MftLcn * NtfsTestImage.Cluster + 40 * NtfsTestImage.RecordSize), NtfsTestImage.RecordSize);
        Assert.Null(MftRecordParser.Parse(raw, 512, 40, 4096));
    }

    [Fact]
    public void Truncated_or_random_records_never_throw()
    {
        var rng = new Random(1234);
        for (var i = 0; i < 2000; i++)
        {
            var junk = new byte[1024];
            rng.NextBytes(junk);
            "FILE"u8.CopyTo(junk);
            junk[4] = 48; junk[5] = 0; junk[6] = 3; junk[7] = 0;
            _ = MftRecordParser.Parse(junk, 512, i, 4096); // must return a record or null, not throw
        }
    }

    [Theory]
    [InlineData("report.docx", "report.docx")]
    [InlineData("..", "_")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("a/b\\c", "a_b_c")]
    [InlineData("x:y?", "x_y_")]
    [InlineData("trailing.", "trailing")]
    public void Names_from_disk_are_sanitised(string name, string expected) => Assert.Equal(expected, FileRecoverer.SafeName(name));
}

public class NtfsRecoveryScannerTests
{
    private static readonly byte[] SmallText = Encoding.UTF8.GetBytes("Quarterly notes - deleted by mistake.");

    private static (NtfsTestImage Image, byte[] Big) BuildVolume()
    {
        var img = new NtfsTestImage();
        // Live folder \Docs (record 30), and a deleted folder \Docs\Old (record 31, sequence bumped on delete).
        img.AddRecord(30, inUse: true, dir: true, seq: 3, attrs: [NtfsTestImage.FileName(5, 5, "Docs")]);
        img.AddRecord(31, inUse: false, dir: true, seq: 4, attrs: [NtfsTestImage.FileName(30, 3, "Old")]);

        // Deleted resident file.
        img.AddRecord(32, inUse: false, dir: false, seq: 2, attrs:
        [
            NtfsTestImage.StandardInformation(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc)),
            NtfsTestImage.FileName(30, 3, "NOTES~1.TXT", ns: 2),
            NtfsTestImage.FileName(30, 3, "notes.txt", SmallText.Length),
            NtfsTestImage.Resident(0x80, SmallText),
        ]);

        // Deleted non-resident file in two fragments, clusters 100-101 and 300-301, real size 15000.
        var big = new byte[15000];
        new Random(7).NextBytes(big);
        var padded = new byte[4 * NtfsTestImage.Cluster];
        big.CopyTo(padded, 0);
        img.WriteClusters(100, padded[..(2 * NtfsTestImage.Cluster)]);
        img.WriteClusters(300, padded[(2 * NtfsTestImage.Cluster)..]);
        img.AddRecord(33, inUse: false, dir: false, seq: 2, attrs:
        [
            NtfsTestImage.FileName(31, 4, "photo.raw", big.Length),
            NtfsTestImage.NonResidentData([(100, 2), (300, 2)], big.Length, big.Length),
        ]);

        // Deleted file whose clusters were reused by something else.
        img.MarkUsed(500); img.MarkUsed(501);
        img.AddRecord(34, inUse: false, dir: false, seq: 2, attrs:
            [NtfsTestImage.FileName(5, 5, "gone.bin", 8192), NtfsTestImage.NonResidentData([(500, 2)], 8192, 8192)]);

        // Half reused.
        img.MarkUsed(600);
        img.AddRecord(35, inUse: false, dir: false, seq: 2, attrs:
            [NtfsTestImage.FileName(5, 5, "half.bin", 8192), NtfsTestImage.NonResidentData([(600, 2)], 8192, 8192)]);

        // Live file: must not be listed.
        img.MarkUsed(700);
        img.AddRecord(36, inUse: true, dir: false, seq: 1, attrs:
            [NtfsTestImage.FileName(5, 5, "alive.txt", 10), NtfsTestImage.Resident(0x80, new byte[10])]);

        // Fragmented file described through an attribute list: reported, but unsupported.
        img.AddRecord(37, inUse: false, dir: false, seq: 2, attrs:
            [NtfsTestImage.FileName(5, 5, "huge.iso", 1 << 30), NtfsTestImage.Resident(0x20, new byte[32])]);

        // Torn record.
        img.AddRecord(38, inUse: false, dir: false, seq: 2, attrs: [NtfsTestImage.FileName(5, 5, "torn.txt")], tear: true);

        // Sparse + valid-data-length: 3 clusters, middle sparse, only first 5000 bytes initialised.
        var head = new byte[NtfsTestImage.Cluster];
        new Random(9).NextBytes(head);
        img.WriteClusters(800, head);
        img.WriteClusters(802, Enumerable.Repeat((byte)0xAB, NtfsTestImage.Cluster).ToArray());
        img.AddRecord(39, inUse: false, dir: false, seq: 2, attrs:
            [NtfsTestImage.FileName(5, 5, "sparse.dat", 3 * 4096), NtfsTestImage.NonResidentData([(800, 1), (-1, 1), (802, 1)], 3 * 4096, 5000)]);

        img.Finish();
        return (img, big);
    }

    private static VolumeReader Reader(NtfsTestImage img) => new StreamVolumeReader(new MemoryStream(img.Image));

    [Fact]
    public void Scan_finds_deleted_files_with_paths_and_chances()
    {
        var (img, _) = BuildVolume();
        var result = NtfsRecoveryScanner.Scan(Reader(img));
        var byName = result.Files.ToDictionary(f => f.Name);

        Assert.Equal(RecoveryChance.Excellent, byName["notes.txt"].Chance);
        Assert.Equal(@"\Docs", byName["notes.txt"].Folder);
        Assert.Equal(new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc), byName["notes.txt"].Modified);
        Assert.Equal(RecoveryChance.Good, byName["photo.raw"].Chance);
        Assert.Equal(@"\Docs\Old", byName["photo.raw"].Folder);
        Assert.Equal(15000, byName["photo.raw"].SizeBytes);
        Assert.Equal(RecoveryChance.Overwritten, byName["gone.bin"].Chance);
        Assert.Equal(RecoveryChance.Partial, byName["half.bin"].Chance);
        Assert.Equal(RecoveryChance.Unsupported, byName["huge.iso"].Chance);
        Assert.DoesNotContain("alive.txt", byName.Keys);
        Assert.DoesNotContain("torn.txt", byName.Keys);
        Assert.Equal(1, result.CorruptRecords);
        Assert.Equal(64, result.RecordsScanned);
    }

    /// <summary>Helper-hosted recovery streams files in chunks; every chunking must reproduce Recover() exactly.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(511)]
    [InlineData(4096)]
    [InlineData(32 * 1024)]
    public void Chunked_reads_match_direct_recovery_byte_for_byte(int chunk)
    {
        var (img, _) = BuildVolume();
        var v = Reader(img);
        var result = NtfsRecoveryScanner.Scan(v);
        var dest = Path.Combine(Path.GetTempPath(), "pm-recover-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var f in result.Files.Where(f => f.Chance is RecoveryChance.Excellent or RecoveryChance.Good or RecoveryChance.Partial))
            {
                var direct = File.ReadAllBytes(FileRecoverer.Recover(v, result.Boot, f, dest).OutputPath);
                var chunked = new MemoryStream();
                var buffer = new byte[chunk];
                for (long pos = 0; ; )
                {
                    var n = FileRecoverer.ReadAt(v, result.Boot, f, pos, buffer, chunk);
                    chunked.Write(buffer, 0, n);
                    pos += n;
                    if (n < chunk) break;
                }
                Assert.True(direct.AsSpan().SequenceEqual(chunked.ToArray()), $"{f.Name} differs at chunk size {chunk}");
            }
        }
        finally { if (Directory.Exists(dest)) Directory.Delete(dest, true); }
    }

    [Fact]
    public void Helper_recovery_service_scans_lists_and_reads_read_only()
    {
        var (img, big) = BuildVolume();
        var disk = new DiskInfo { Number = 4, Model = "Test disk", SizeBytes = 1L << 30, Style = PartitionStyle.Gpt, UniqueId = "D4" }
            .WithPartitions([new PartitionInfo { DiskNumber = 4, PartitionNumber = 2, OffsetBytes = 1 << 20, SizeBytes = img.Image.Length,
                FileSystem = "NTFS", VolumePath = @"\\?\Volume{00000000-0000-0000-0000-000000000004}\" }]);
        var audit = new PartitionManager.Tests.Production.MemoryAudit();
        var before = (byte[])img.Image.Clone();
        var service = new PartitionManager.PrivilegedHelper.Engine.RecoveryService(new PartitionManager.Tests.Production.FakeDiscovery(disk), audit, _ => Reader(img));
        var p = disk.FindPartition(2)!;
        var json = PartitionManager.Core.Execution.HelperProtocol.Json;

        var stale = service.Handle(Guid.NewGuid(), new RecoveryRequest(RecoveryAction.ScanVolume, 4, 2, p.OffsetBytes + 1, p.SizeBytes));
        Assert.Equal("TARGET_CHANGED", stale.Code);
        Assert.Equal("NO_SCAN", service.Handle(Guid.NewGuid(), new RecoveryRequest(RecoveryAction.ReadFile, 0, RecordNumber: 1, Length: 10)).Code);

        var scan = service.Handle(Guid.NewGuid(), new RecoveryRequest(RecoveryAction.ScanVolume, 4, 2, p.OffsetBytes, p.SizeBytes));
        Assert.Equal("OK", scan.Code);
        var summary = System.Text.Json.JsonSerializer.Deserialize<RecoveryScanSummary>(scan.Payload!, json)!;
        var page = System.Text.Json.JsonSerializer.Deserialize<RecoveryFilePage>(service.Handle(Guid.NewGuid(), new RecoveryRequest(RecoveryAction.ListFiles, 0, Start: 0, Count: 1000)).Payload!, json)!;
        Assert.Equal(summary.FileCount, page.Total);
        Assert.True(page.Files.Count <= RecoveryRequest.MaxListPage);
        var photo = page.Files.Single(f => f.Name == "photo.raw");

        var output = new MemoryStream();
        for (long pos = 0; ; )
        {
            var r = service.Handle(Guid.NewGuid(), new RecoveryRequest(RecoveryAction.ReadFile, 0, RecordNumber: photo.RecordNumber, Position: pos, Length: 1_000_000));
            var chunk = System.Text.Json.JsonSerializer.Deserialize<RecoveryChunk>(r.Payload!, json)!;
            Assert.True(chunk.Length <= RecoveryRequest.MaxReadBytes);
            output.Write(Convert.FromBase64String(chunk.Base64));
            pos += chunk.Length;
            if (chunk.EndOfData) break;
        }
        Assert.Equal(SHA256.HashData(big), SHA256.HashData(output.ToArray()));
        Assert.Equal("UNKNOWN_FILE", service.Handle(Guid.NewGuid(), new RecoveryRequest(RecoveryAction.ReadFile, 0, RecordNumber: 999999, Length: 10)).Code);
        Assert.Equal(before, img.Image);                                    // the source was only read
        Assert.Contains(audit.Entries, a => a.Kind == "RecoveryScan" && a.Outcome == "READ_ONLY");
    }

    [Fact]
    public void Recovered_files_match_byte_for_byte()
    {
        var (img, big) = BuildVolume();
        var v = Reader(img);
        var result = NtfsRecoveryScanner.Scan(v);
        var dest = Path.Combine(Path.GetTempPath(), "pm-recover-" + Guid.NewGuid().ToString("N"));
        try
        {
            var notes = FileRecoverer.Recover(v, result.Boot, result.Files.Single(f => f.Name == "notes.txt"), dest);
            Assert.Equal(SmallText, File.ReadAllBytes(notes.OutputPath));
            Assert.EndsWith(Path.Combine("Docs", "notes.txt"), notes.OutputPath);
            Assert.Equal(new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc), File.GetLastWriteTimeUtc(notes.OutputPath));

            var photo = FileRecoverer.Recover(v, result.Boot, result.Files.Single(f => f.Name == "photo.raw"), dest);
            Assert.Equal(SHA256.HashData(big), SHA256.HashData(File.ReadAllBytes(photo.OutputPath)));

            var again = FileRecoverer.Recover(v, result.Boot, result.Files.Single(f => f.Name == "notes.txt"), dest);
            Assert.NotEqual(notes.OutputPath, again.OutputPath); // never overwrites
        }
        finally { if (Directory.Exists(dest)) Directory.Delete(dest, true); }
    }

    [Fact]
    public void Sparse_runs_and_uninitialised_tail_read_as_zeros()
    {
        var (img, _) = BuildVolume();
        var v = Reader(img);
        var result = NtfsRecoveryScanner.Scan(v);
        var dest = Path.Combine(Path.GetTempPath(), "pm-recover-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = FileRecoverer.Recover(v, result.Boot, result.Files.Single(f => f.Name == "sparse.dat"), dest);
            var bytes = File.ReadAllBytes(r.OutputPath);
            Assert.Equal(3 * 4096, bytes.Length);
            Assert.Equal(img.Image.AsSpan(800 * 4096, 5000).ToArray(), bytes[..5000]);
            Assert.All(bytes[5000..], b => Assert.Equal(0, b));
        }
        finally { if (Directory.Exists(dest)) Directory.Delete(dest, true); }
    }

    [Fact]
    public void Unsupported_file_is_not_written()
    {
        var (img, _) = BuildVolume();
        var v = Reader(img);
        var result = NtfsRecoveryScanner.Scan(v);
        Assert.Throws<NtfsFormatException>(() => FileRecoverer.Recover(v, result.Boot, result.Files.Single(f => f.Name == "huge.iso"), Path.GetTempPath()));
    }

    [Fact]
    public void Scan_is_cancellable()
    {
        var (img, _) = BuildVolume();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => NtfsRecoveryScanner.Scan(Reader(img), null, cts.Token));
    }

    [Fact]
    public void Non_ntfs_volume_is_reported_clearly() =>
        Assert.Throws<NtfsFormatException>(() => NtfsRecoveryScanner.Scan(new StreamVolumeReader(new MemoryStream(new byte[1 << 20]))));
}

public class LostPartitionScannerTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void Finds_ntfs_fat32_and_exfat_boot_sectors_in_free_space()
    {
        var disk = new byte[64 * MiB];
        // NTFS volume (the synthetic 16 MiB image, MFT included) at 4 MiB.
        var ntfs = new NtfsTestImage();
        ntfs.Finish();
        ntfs.Image.CopyTo(disk, 4 * MiB);
        // FAT32 at 32 MiB: 8 MiB.
        var fat = disk.AsSpan((int)(32 * MiB), 512);
        "FAT32   "u8.CopyTo(fat[82..]);
        BitConverter.GetBytes((ushort)512).CopyTo(fat[11..]);
        BitConverter.GetBytes((uint)(8 * MiB / 512)).CopyTo(fat[32..]);
        fat[510] = 0x55; fat[511] = 0xAA;
        // exFAT at 48 MiB: 4 MiB.
        var ex = disk.AsSpan((int)(48 * MiB), 512);
        "EXFAT   "u8.CopyTo(ex[3..]);
        BitConverter.GetBytes((ulong)(4 * MiB / 512)).CopyTo(ex[72..]);
        ex[108] = 9;
        ex[510] = 0x55; ex[511] = 0xAA;

        var info = new DiskInfo { Number = 9, Model = "img", SizeBytes = disk.Length, Style = PartitionStyle.Gpt };
        var found = LostPartitionScanner.ScanUnallocated(new StreamSectorSource(new MemoryStream(disk)), info);

        Assert.Equal(3, found.Count);
        Assert.Equal(("NTFS", 4 * MiB, FoundConfidence.High), (found[0].FileSystem, found[0].OffsetBytes, found[0].Confidence));
        Assert.Equal(16 * MiB, found[0].SizeBytes);
        Assert.Equal(("FAT32", 32 * MiB, 8 * MiB), (found[1].FileSystem, found[1].OffsetBytes, found[1].SizeBytes));
        Assert.Equal(("exFAT", 48 * MiB, 4 * MiB), (found[2].FileSystem, found[2].OffsetBytes, found[2].SizeBytes));
    }

    [Fact]
    public void Existing_partitions_are_not_reported()
    {
        var disk = new byte[32 * MiB];
        var ntfs = new NtfsTestImage();
        ntfs.Image.CopyTo(disk, 4 * MiB);
        var info = new DiskInfo { Number = 9, Model = "img", SizeBytes = disk.Length, Style = PartitionStyle.Gpt }
            .WithPartitions([new PartitionInfo { DiskNumber = 9, PartitionNumber = 1, OffsetBytes = 4 * MiB, SizeBytes = 16 * MiB, FileSystem = "NTFS" }]);
        Assert.Empty(LostPartitionScanner.ScanUnallocated(new StreamSectorSource(new MemoryStream(disk)), info));
    }
}
