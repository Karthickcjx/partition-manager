using System.Windows;
using System.Windows.Controls;
using PartitionManager.Core.Layout;

namespace PartitionManager.Desktop.Controls;

/// <summary>
/// Lays children out left-to-right with widths proportional to their <see cref="WeightProperty"/>, while guaranteeing
/// every child at least <see cref="MinItemWidth"/> so tiny partitions (MSR, ESP) stay clickable and legible.
/// </summary>
public sealed class ProportionalPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(ProportionalPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetWeight(DependencyObject d) => (double)d.GetValue(WeightProperty);
    public static void SetWeight(DependencyObject d, double value) => d.SetValue(WeightProperty, value);

    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(ProportionalPanel), new FrameworkPropertyMetadata(64.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ProportionalPanel), new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren.Cast<UIElement>().ToList();
        var width = double.IsInfinity(availableSize.Width) ? children.Count * MinItemWidth * 2 : availableSize.Width;
        var widths = DiskMapWidths.Allocate(children.Select(GetWeight).ToList(), Math.Max(0, width - Spacing * Math.Max(0, children.Count - 1)), MinItemWidth);
        double height = 0;
        for (var i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(widths[i], availableSize.Height));
            height = Math.Max(height, children[i].DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren.Cast<UIElement>().ToList();
        var widths = DiskMapWidths.Allocate(children.Select(GetWeight).ToList(), Math.Max(0, finalSize.Width - Spacing * Math.Max(0, children.Count - 1)), MinItemWidth);
        double x = 0;
        for (var i = 0; i < children.Count; i++)
        {
            children[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            x += widths[i] + Spacing;
        }
        return finalSize;
    }
}
