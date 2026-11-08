using PartitionManager.Core.Models;

namespace PartitionManager.Core.Operations;

public sealed class OperationValidationException(string message) : Exception(message);

/// <summary>The layout of every disk as it would look after a sequence of pending operations.</summary>
public sealed record PreviewState(IReadOnlyList<DiskInfo> Disks)
{
    /// <summary>Drive letters used by things that are not partitions (optical drives, network shares, subst).</summary>
    public IReadOnlySet<char> ExternallyUsedLetters { get; init; } = new HashSet<char>();

    public DiskInfo Disk(int number) =>
        Disks.FirstOrDefault(d => d.Number == number)
        ?? throw new OperationValidationException($"Disk {number} is no longer present.");

    public PreviewState With(DiskInfo disk) =>
        this with { Disks = Disks.Select(d => d.Number == disk.Number ? disk : d).ToArray() };

    public IReadOnlySet<char> UsedLetters
    {
        get
        {
            var set = new HashSet<char>(ExternallyUsedLetters.Select(char.ToUpperInvariant));
            foreach (var p in Disks.SelectMany(d => d.Partitions))
                if (p.DriveLetter is { } c) set.Add(char.ToUpperInvariant(c));
            return set;
        }
    }

    /// <summary>A and B are traditionally reserved for floppy drives, so we offer C through Z.</summary>
    public IReadOnlyList<char> AvailableLetters
    {
        get
        {
            var used = UsedLetters;
            return Enumerable.Range('C', 'Z' - 'C' + 1).Select(i => (char)i).Where(c => !used.Contains(c)).ToArray();
        }
    }
}
