namespace PartitionManager.Desktop.ViewModels.Dialogs;

public enum MessageKind { Info, Success, Warning, Error }

public sealed class MessageDialogViewModel(string title, string message, MessageKind kind) : DialogViewModel
{
    public override string Title => title;
    public override string? SecondaryText => null;
    public string Message => message;
    public MessageKind Kind => kind;
    public string Glyph => kind switch
    {
        MessageKind.Success => "",
        MessageKind.Warning => "",
        MessageKind.Error => "",
        _ => "",
    };
}

public sealed class ConfirmDialogViewModel(string title, string message, IReadOnlyList<string> details, string confirmText, bool danger) : DialogViewModel
{
    public override string Title => title;
    public override string PrimaryText => confirmText;
    public override bool IsPrimaryDanger => danger;
    public override double Width => 600;
    public string Message => message;
    public IReadOnlyList<string> Details => details;
    public bool IsDanger => danger;
}

public sealed record PropertyRow(string Name, string Value);

public sealed class PropertiesDialogViewModel(string title, IReadOnlyList<PropertyRow> rows, IReadOnlyList<string> issues) : DialogViewModel
{
    public override string Title => title;
    public override string PrimaryText => "Close";
    public override string? SecondaryText => null;
    public override double Width => 600;
    public IReadOnlyList<PropertyRow> Rows => rows;
    public IReadOnlyList<string> Issues => issues;
}

public sealed record LetterOption(char? Letter)
{
    public string Text => Letter is { } c ? $"{c}:" : "None";
    public override string ToString() => Text;
}

public sealed record ChoiceOption<T>(T Value, string Text)
{
    public override string ToString() => Text;
}
