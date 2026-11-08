using PartitionManager.Core.Layout;
using Xunit;

namespace PartitionManager.Tests;

public class DiskMapWidthTests
{
    [Fact]
    public void Widths_fill_available_space_exactly()
    {
        var w = DiskMapWidths.Allocate([260, 16, 150_000, 1000, 320_000, 260], 700, 58);
        Assert.Equal(700, w.Sum(), 6);
    }

    [Fact]
    public void Tiny_partitions_get_minimum_width()
    {
        var w = DiskMapWidths.Allocate([16, 100_000, 100_000], 600, 58);
        Assert.Equal(58, w[0], 6);
        Assert.Equal(w[1], w[2], 6);
    }

    [Fact]
    public void Larger_partitions_are_wider()
    {
        var w = DiskMapWidths.Allocate([150, 325], 800, 58);
        Assert.True(w[1] > w[0]);
        Assert.Equal(325.0 / 150, w[1] / w[0], 3);
    }

    [Fact]
    public void Too_little_space_splits_evenly()
    {
        var w = DiskMapWidths.Allocate([1, 1000, 1_000_000], 100, 58);
        Assert.All(w, x => Assert.Equal(100.0 / 3, x, 6));
    }

    [Fact]
    public void Empty_and_zero_inputs_are_safe()
    {
        Assert.Empty(DiskMapWidths.Allocate([], 500, 58));
        Assert.All(DiskMapWidths.Allocate([1, 2], 0, 58), x => Assert.Equal(0, x));
        Assert.Equal(500, DiskMapWidths.Allocate([0, 0], 500, 58).Sum(), 6);
    }
}
