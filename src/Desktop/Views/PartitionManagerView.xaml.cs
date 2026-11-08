using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PartitionManager.Desktop.ViewModels;

namespace PartitionManager.Desktop.Views;

public partial class PartitionManagerView : UserControl
{
    public PartitionManagerView()
    {
        InitializeComponent();
        Loaded += (_, _) => Focus();
    }

    private PartitionManagerViewModel? Vm => DataContext as PartitionManagerViewModel;

    private void Segment_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm?.PropertiesCommand.CanExecute(null) == true) Vm.PropertiesCommand.Execute(null);
        e.Handled = true;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)FindResource("SegmentMenu");
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Bottom;
        menu.DataContext = DataContext;
        menu.IsOpen = true;
    }

    /// <summary>The disk map ListBox swallows wheel events; forward them to the outer ScrollViewer.</summary>
    private void DiskMap_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        var parent = VisualTreeHelper.GetParent((DependencyObject)sender) as UIElement;
        parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }
}
