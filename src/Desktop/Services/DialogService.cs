using System.Windows;
using PartitionManager.Desktop.ViewModels.Dialogs;
using PartitionManager.Desktop.Views;

namespace PartitionManager.Desktop.Services;

public interface IDialogService
{
    bool Show(DialogViewModel dialog);
    void Message(string title, string message, MessageKind kind = MessageKind.Info);
    bool Confirm(string title, string message, IReadOnlyList<string>? details = null, string confirmText = "Confirm", bool danger = false);
}

public sealed class DialogService : IDialogService
{
    public bool Show(DialogViewModel dialog)
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
        var window = new DialogWindow { DataContext = dialog, Owner = owner };
        return window.ShowDialog() == true;
    }

    public void Message(string title, string message, MessageKind kind = MessageKind.Info) =>
        Show(new MessageDialogViewModel(title, message, kind));

    public bool Confirm(string title, string message, IReadOnlyList<string>? details = null, string confirmText = "Confirm", bool danger = false) =>
        Show(new ConfirmDialogViewModel(title, message, details ?? [], confirmText, danger));
}
