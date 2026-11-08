using System.Text.RegularExpressions;
using Xunit;

namespace PartitionManager.Tests.Production;

/// <summary>
/// ProgressBar.Value binds two-way by default; binding it to a read-only view-model property throws at runtime
/// ("A TwoWay or OneWayToSource binding cannot work on the read-only property 'Progress'"), found in RDP testing of the
/// Data Recovery page. Every ProgressBar Value binding must be explicitly OneWay.
/// </summary>
public class XamlBindingTests
{
    [Fact]
    public void ProgressBar_value_bindings_are_one_way()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PartitionManager.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var offenders = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src", "Desktop"), "*.xaml", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"<ProgressBar\b[^>]*\bValue=""\{Binding[^}]*\}""").Select(m => (f, m.Value)))
            .Where(x => !x.Value.Contains("Mode=OneWay"))
            .Select(x => $"{Path.GetFileName(x.f)}: {x.Value}")
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }
}
