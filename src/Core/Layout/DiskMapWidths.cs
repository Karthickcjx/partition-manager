namespace PartitionManager.Core.Layout;

/// <summary>Width allocation for the disk map: proportional to size, but never narrower than a minimum.</summary>
public static class DiskMapWidths
{
    public static double[] Allocate(IReadOnlyList<double> weights, double available, double minWidth)
    {
        var n = weights.Count;
        var widths = new double[n];
        if (n == 0 || available <= 0) return widths;
        if (n * minWidth >= available)
        {
            Array.Fill(widths, available / n);
            return widths;
        }

        var pinned = new bool[n];
        while (true)
        {
            var free = available - pinned.Count(p => p) * minWidth;
            var weightSum = weights.Where((_, i) => !pinned[i]).Sum(w => Math.Max(w, 0));
            var unpinned = pinned.Count(p => !p);
            var changed = false;
            for (var i = 0; i < n; i++)
            {
                if (pinned[i]) { widths[i] = minWidth; continue; }
                widths[i] = weightSum > 0 ? free * Math.Max(weights[i], 0) / weightSum : free / unpinned;
                if (widths[i] < minWidth) { pinned[i] = true; changed = true; }
            }
            if (!changed) return widths;
        }
    }
}
