using System.Windows;
using Microsoft.Win32;

namespace PartitionManager.Desktop.Services;

public enum AppTheme { Light, Dark }

public static class ThemeManager
{
    static ThemeManager()
    {
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
                Application.Current?.Dispatcher.InvokeAsync(() => Apply(Current));
        };
    }
    private static readonly Uri LightUri = new("Themes/Light.xaml", UriKind.Relative);
    private static readonly Uri DarkUri = new("Themes/Dark.xaml", UriKind.Relative);

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static event EventHandler? ThemeChanged;

    /// <summary>Follows the Windows "app mode" setting (HKCU ...\Themes\Personalize\AppsUseLightTheme).</summary>
    public static AppTheme SystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch
        {
            return AppTheme.Light;
        }
    }

    public static void Apply(AppTheme theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = theme == AppTheme.Dark ? DarkUri : LightUri };
        if (SystemParameters.HighContrast)
        {
            foreach (var key in new[] { "WindowBackgroundBrush", "SurfaceBrush", "SurfaceAltBrush", "SurfaceHoverBrush", "NavHoverBrush", "NavSelectedBrush", "AccentSoftBrush", "DangerSoftBrush", "WarningSoftBrush", "SuccessSoftBrush" }) palette[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "BorderBrush", "DividerBrush", "DangerBrush", "WarningBrush", "SuccessBrush" }) palette[key] = SystemColors.WindowTextBrush;
            foreach (var key in new[] { "AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "SegmentDataBrush" }) palette[key] = SystemColors.HighlightBrush;
            palette["TextOnAccentBrush"] = SystemColors.HighlightTextBrush;
            palette["TextDisabledBrush"] = SystemColors.GrayTextBrush;
        }
        var existing = dictionaries.FirstOrDefault(d => d.Source is { } s &&
            (s.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) || s.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)));
        if (existing is null) dictionaries.Insert(0, palette);
        else dictionaries[dictionaries.IndexOf(existing)] = palette;
        Current = theme;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void Toggle() => Apply(Current == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);
}
