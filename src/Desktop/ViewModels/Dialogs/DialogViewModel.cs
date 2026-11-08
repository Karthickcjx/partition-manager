using PartitionManager.Core.Operations;
using PartitionManager.Desktop.Mvvm;

namespace PartitionManager.Desktop.ViewModels.Dialogs;

/// <summary>Base for every modal dialog. The host window renders the body by DataTemplate and the footer from these properties.</summary>
public abstract class DialogViewModel : ObservableObject
{
    private string? _error;

    public abstract string Title { get; }
    public virtual string PrimaryText => "OK";
    public virtual string? SecondaryText => "Cancel";
    public virtual bool IsPrimaryDanger => false;
    public virtual bool ShowFooter => true;
    public virtual double Width => 520;

    public string? Error
    {
        get => _error;
        protected set => Set(ref _error, value);
    }

    public event EventHandler<bool>? CloseRequested;

    /// <summary>Called when the primary button is pressed. Return false to keep the dialog open (e.g. to show <see cref="Error"/>).</summary>
    public virtual bool TryAccept() => true;

    public void RequestClose(bool result) => CloseRequested?.Invoke(this, result);
}

/// <summary>A dialog that produces a <see cref="DiskOperation"/> and submits it to the pending-task queue.</summary>
public abstract class OperationDialogViewModel(Func<DiskOperation, QueueResult> submit) : DialogViewModel
{
    public override string PrimaryText => "Add to task list";

    protected abstract DiskOperation Build();

    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public override bool TryAccept()
    {
        try
        {
            var result = submit(Build());
            if (!result.Success)
            {
                Error = result.Error;
                return false;
            }
            Warnings = result.Warnings;
            return true;
        }
        catch (OperationValidationException ex)
        {
            Error = ex.Message;
            return false;
        }
        catch (FormatException ex)
        {
            Error = ex.Message;
            return false;
        }
    }

    protected void ClearError() => Error = null;
}
