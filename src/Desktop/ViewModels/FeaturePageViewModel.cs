namespace PartitionManager.Desktop.ViewModels;

public sealed record FeatureCard(string Glyph, string Title, string Description, string Status = "Planned");

/// <summary>
/// A section whose features were identified in the reference application but are not implemented yet. The page says so
/// plainly instead of presenting non-working buttons.
/// </summary>
public sealed class FeaturePageViewModel(string heading, string intro, IReadOnlyList<FeatureCard> cards)
{
    public string Heading { get; } = heading;
    public string Intro { get; } = intro;
    public IReadOnlyList<FeatureCard> Cards { get; } = cards;
    public FeatureCard? Hero => Cards.Count > 0 ? Cards[0] : null;
    public IReadOnlyList<FeatureCard> Rest => Cards.Skip(1).ToList();
}
