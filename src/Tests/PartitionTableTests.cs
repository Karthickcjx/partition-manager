using PartitionManager.Core.Models;
using PartitionManager.Core.PartitionTables;
using Xunit;

namespace PartitionManager.Tests;

public class PartitionTableTests
{
    [Fact]
    public void Crc32_matches_known_check_value()
    {
        // Standard CRC-32 check value for the ASCII string "123456789".
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    [Fact]
    public void Mbr_parses_primary_entries()
    {
        var mbr = MbrParser.Parse(TestImages.Mbr((0x07, 2048, 204800, true), (0x0C, 206848, 409600, false)));
        Assert.Equal(0xA1B2C3D4u, mbr.DiskSignature);
        var used = mbr.UsedEntries.ToList();
        Assert.Equal(2, used.Count);
        Assert.True(used[0].IsActive);
        Assert.Equal(2048u, used[0].StartLba);
        Assert.Equal((byte)0x0C, used[1].Type);
        Assert.False(mbr.IsProtective);
    }

    [Fact]
    public void Mbr_without_signature_is_rejected()
    {
        var sector = TestImages.Mbr((0x07, 2048, 100, false));
        sector[511] = 0;
        Assert.Throws<PartitionTableException>(() => MbrParser.Parse(sector));
    }

    [Fact]
    public void Mbr_with_two_active_entries_is_rejected()
    {
        Assert.Throws<PartitionTableException>(() => MbrParser.Parse(TestImages.Mbr((0x07, 2048, 100, true), (0x07, 4096, 100, true))));
    }

    [Fact]
    public void Mbr_short_buffer_is_rejected()
    {
        Assert.Throws<PartitionTableException>(() => MbrParser.Parse(new byte[100]));
    }

    [Fact]
    public void Ebr_chain_yields_logical_partitions_with_absolute_lbas()
    {
        const uint extStart = 10000, extCount = 20000;
        var image = new byte[(extStart + extCount) * 512L];
        TestImages.Mbr((0x07, 2048, 4096, false), (0x0F, extStart, extCount, false)).CopyTo(image, 0);
        // EBR 1 at extStart: logical at +2048, link to next EBR at relative 8000.
        TestImages.Mbr((0x07, 2048, 4000, false), (0x05, 8000, 6000, false)).CopyTo(image, extStart * 512L);
        // EBR 2 at extStart+8000: logical at +2048, no further link.
        TestImages.Mbr((0x07, 2048, 3000, false)).CopyTo(image, (extStart + 8000) * 512L);

        var snap = PartitionTableReader.Read(new StreamSectorSource(new MemoryStream(image)));
        Assert.Equal(PartitionStyle.Mbr, snap.Style);
        var logicals = snap.Partitions.Where(p => p.IsLogical).ToList();
        Assert.Equal(2, logicals.Count);
        Assert.Equal(extStart + 2048, logicals[0].StartLba);
        Assert.Equal(extStart + 8000 + 2048, logicals[1].StartLba);
        Assert.Contains(snap.Partitions, p => p.Role == PartitionRole.Extended);
    }

    [Fact]
    public void Ebr_chain_loop_is_detected()
    {
        const uint extStart = 100;
        var image = new byte[(extStart + 1000) * 512L];
        TestImages.Mbr((0x0F, extStart, 1000, false)).CopyTo(image, 0);
        TestImages.Mbr((0x07, 10, 10, false), (0x05, 0, 100, false)).CopyTo(image, extStart * 512L);
        Assert.Throws<PartitionTableException>(() => PartitionTableReader.Read(new StreamSectorSource(new MemoryStream(image))));
    }

    [Fact]
    public void Gpt_image_round_trips()
    {
        var image = TestImages.GptImage(20480,
            new TestImages.GptPart(PartitionTypes.EfiSystem, 2048, 4095, "EFI system partition"),
            new TestImages.GptPart(PartitionTypes.BasicData, 4096, 16383, "Basic data partition", GptEntry.AttrHidden));

        var snap = PartitionTableReader.Read(new StreamSectorSource(new MemoryStream(image)));
        Assert.Equal(PartitionStyle.Gpt, snap.Style);
        Assert.True(snap.Gpt!.HeaderCrcValid);
        Assert.True(snap.GptEntriesCrcValid);
        Assert.True(snap.BackupGptValid);
        Assert.Empty(snap.Warnings);
        Assert.Equal(2, snap.Partitions.Count);
        Assert.Equal(PartitionRole.EfiSystem, snap.Partitions[0].Role);
        Assert.Equal(2048UL, snap.Partitions[0].SectorCount);
        Assert.Equal("Basic data partition", snap.Partitions[1].Name);
        Assert.True(snap.Partitions[1].IsHidden);
    }

    [Fact]
    public void Gpt_header_crc_corruption_is_reported_not_hidden()
    {
        var image = TestImages.GptImage(8192, corruptPrimaryCrc: true, new TestImages.GptPart(PartitionTypes.BasicData, 2048, 4095));
        var snap = PartitionTableReader.Read(new StreamSectorSource(new MemoryStream(image)));
        Assert.False(snap.Gpt!.HeaderCrcValid);
        Assert.Contains(snap.Warnings, w => w.Contains("CRC32"));
    }

    [Fact]
    public void Gpt_entry_corruption_is_reported()
    {
        var image = TestImages.GptImage(8192, new TestImages.GptPart(PartitionTypes.BasicData, 2048, 4095));
        image[2 * 512 + 40] ^= 0x01; // flip a bit in entry 0's last LBA
        var snap = PartitionTableReader.Read(new StreamSectorSource(new MemoryStream(image)));
        Assert.False(snap.GptEntriesCrcValid);
    }

    [Fact]
    public void Gpt_missing_signature_is_rejected()
    {
        var bytes = new byte[512];
        Assert.Throws<PartitionTableException>(() => GptParser.ParseHeader(bytes));
    }

    [Fact]
    public void Blank_disk_reads_as_raw()
    {
        var snap = PartitionTableReader.Read(new StreamSectorSource(new MemoryStream(new byte[512 * 64])));
        Assert.Equal(PartitionStyle.Raw, snap.Style);
        Assert.Empty(snap.Partitions);
    }

    [Theory]
    [InlineData("c12a7328-f81f-11d2-ba4b-00a0c93ec93b", PartitionRole.EfiSystem)]
    [InlineData("e3c9e316-0b5c-4db8-817d-f92df00215ae", PartitionRole.MicrosoftReserved)]
    [InlineData("de94bba4-06d1-4d40-a16a-bfd50179d6ac", PartitionRole.Recovery)]
    [InlineData("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", PartitionRole.BasicData)]
    [InlineData("00000000-0000-0000-0000-000000000001", PartitionRole.Other)]
    public void Gpt_type_guid_maps_to_role(string guid, PartitionRole role) =>
        Assert.Equal(role, PartitionTypes.RoleFromGpt(Guid.Parse(guid)));
}
