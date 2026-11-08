using System.Globalization;
using System.Text.RegularExpressions;

namespace PartitionManager.Core.Models;

/// <summary>Binary (1024-based) size formatting, matching how Windows Explorer labels sizes.</summary>
public static partial class ByteSize
{
    public const long KiB = 1024;
    public const long MiB = KiB * 1024;
    public const long GiB = MiB * 1024;
    public const long TiB = GiB * 1024;

    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes, int decimals = 2)
    {
        if (bytes < 0) return "-" + Format(-bytes, decimals);
        if (bytes < KiB) return $"{bytes} B";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString("0." + new string('#', Math.Max(decimals, 0)), CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string Format(long? bytes, int decimals = 2) => bytes is { } b ? Format(b, decimals) : "--";

    /// <summary>Parses "512", "512 B", "1.5 GB", "100MB", "2 TiB" (KB and KiB are both treated as 1024).</summary>
    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = SizePattern().Match(text.Trim());
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return false;
        var multiplier = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "" or "B" => 1L,
            "K" or "KB" or "KIB" => KiB,
            "M" or "MB" or "MIB" => MiB,
            "G" or "GB" or "GIB" => GiB,
            "T" or "TB" or "TIB" => TiB,
            _ => -1L,
        };
        if (multiplier < 0 || value < 0) return false;
        var result = value * multiplier;
        if (result > long.MaxValue) return false;
        bytes = (long)Math.Round(result);
        return true;
    }

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)\s*([A-Za-z]{0,3})$")]
    private static partial Regex SizePattern();
}
