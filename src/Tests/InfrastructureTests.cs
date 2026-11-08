using PartitionManager.Core.Layout;
using PartitionManager.Core.Logging;
using PartitionManager.Core.Models;
using PartitionManager.Infrastructure.Logging;
using PartitionManager.Infrastructure.WindowsApi;
using Xunit;
using static PartitionManager.Tests.Fixtures;

namespace PartitionManager.Tests;

public class InfrastructureTests
{
    [Theory]
    [InlineData(17, "NVMe")]
    [InlineData(7, "USB")]
    [InlineData(11, "SATA")]
    [InlineData(15, "File Backed Virtual")]
    [InlineData(999, "Unknown")]
    public void Bus_type_codes_map_to_names(int code, string name) => Assert.Equal(name, WmiDiskDiscoveryService.BusTypeName((ushort)code));

    [Fact]
    public void Logicals_inside_extended_are_marked()
    {
        var d = new DiskInfo { Number = 1, Model = "m", SizeBytes = 16 * GiB, Style = PartitionStyle.Mbr };
        d = d.WithPartitions(
        [
            Part(1, 1, 1 * MiB, 1 * GiB),
            Part(1, 2, 2 * GiB, 8 * GiB, "", role: PartitionRole.Extended),
            Part(1, 5, 2 * GiB + 1 * MiB, 3 * GiB),
        ]);
        var marked = WmiDiskDiscoveryService.MarkLogicals(d);
        Assert.False(marked.FindPartition(1)!.IsLogical);
        Assert.False(marked.FindPartition(2)!.IsLogical);
        Assert.True(marked.FindPartition(5)!.IsLogical);
        Assert.True(LayoutValidator.IsValid(marked));
    }

    [Fact]
    public void File_log_appends_lines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pm-log-test-" + Guid.NewGuid());
        try
        {
            var log = new FileOperationLog(dir);
            log.Info("test", "hello");
            log.Error("test", "multi\nline");
            var lines = File.ReadAllLines(log.CurrentFile);
            Assert.Equal(2, lines.Length);
            Assert.Contains("[test] hello", lines[0]);
            Assert.Contains("multi line", lines[1]);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Read-only smoke test against the real machine: every Windows PC has at least one disk.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Wmi_discovery_finds_a_disk_with_consistent_layout()
    {
        var service = new WmiDiskDiscoveryService(NullOperationLog.Instance);
        var result = await service.DiscoverAsync();
        Assert.NotEmpty(result.State.Disks);
        foreach (var disk in result.State.Disks)
        {
            Assert.True(disk.SizeBytes > 0);
            Assert.DoesNotContain(LayoutValidator.Validate(disk), i => i.Code is "OVERLAP" or "OUT_OF_BOUNDS");
            foreach (var p in disk.Partitions)
                Assert.True(p.FreeBytes is null || p.FreeBytes <= (p.VolumeSizeBytes ?? p.SizeBytes));
        }
        Assert.Contains(result.State.Disks, d => d.HostsRunningSystem);
    }
}
