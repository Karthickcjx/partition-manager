using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PartitionManager.Desktop.ViewModels.Dialogs;

namespace PartitionManager.Desktop.Views;

public partial class DialogWindow : Window
{
    private bool _running;

    public DialogWindow()
    {
        InitializeComponent();
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 40);
        MaxWidth = Math.Max(360, SystemParameters.WorkArea.Width - 40);
        DataContextChanged += OnDataContextChanged;
        ContentRendered += OnContentRendered;
        Closing += OnClosing;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Vm is { SecondaryText: null, ShowFooter: true }) { DialogResult = true; e.Handled = true; }
        };
    }

    private DialogViewModel? Vm => DataContext as DialogViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DialogViewModel old) old.CloseRequested -= OnCloseRequested;
        if (e.NewValue is DialogViewModel vm)
        {
            vm.CloseRequested += OnCloseRequested;
            Width = vm.Width;
            PrimaryButton.IsDefault = !vm.IsPrimaryDanger;
            if (vm.IsPrimaryDanger) PrimaryButton.Style = (Style)FindResource("DangerButton");
        }
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        // Forms start on their first field; message-style dialogs start on the primary button.
        if (Vm?.IsPrimaryDanger == true && SecondaryButton.IsVisible)
            SecondaryButton.Focus();
        else if (!DialogBody.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)) && Vm?.ShowFooter == true)
            PrimaryButton.Focus();
        if (DataContext is ProgressDialogViewModel progress)
        {
            _running = true;
            await progress.RunAsync();
        }
    }

    private void OnCloseRequested(object? sender, bool result)
    {
        _running = false;
        if (IsLoaded) DialogResult = result;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Alt+F4 during execution requests cancellation instead of abandoning the work.
        if (_running && DataContext is ProgressDialogViewModel p)
        {
            e.Cancel = true;
            if (p.CancelCommand.CanExecute(null)) p.CancelCommand.Execute(null);
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        // Enter (IsDefault) and UI Automation invoke don't move focus, so LostFocus bindings would never commit.
        CommitTextBoxes(this);
        if (Vm?.TryAccept() != false) DialogResult = true;
    }

    private static void CommitTextBoxes(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBox tb && !tb.IsReadOnly) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            CommitTextBoxes(child);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
