using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace MeshVault.Tests;

/// <summary>
/// Razor does not warn when a PascalCase tag fails to resolve to a component —
/// it silently emits it as literal markup, which renders as a dead element with
/// no error anywhere. MudCardActionArea was removed in MudBlazor 9 and slipped
/// through exactly this way, leaving every model card unclickable.
/// </summary>
public class RazorComponentTests
{
    private static readonly Regex ComponentTag = new(@"<(Mud[A-Za-z0-9]*)", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshVault.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> RazorFiles() =>
        Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "src", "MeshVault.Web"), "*.razor", SearchOption.AllDirectories);

    [Fact]
    public void Every_MudBlazor_tag_used_resolves_to_a_real_component()
    {
        // Generic components reflect as "MudSelect`1"; the tag is written without
        // the arity, so trim it before comparing.
        var known = typeof(MudBlazor.MudCard).Assembly
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ComponentBase)))
            .Select(t => t.Name.Split('`')[0])
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(known);

        var unresolved = new List<string>();
        foreach (var file in RazorFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in ComponentTag.Matches(text))
            {
                var tag = match.Groups[1].Value;
                if (!known.Contains(tag))
                    unresolved.Add($"{Path.GetFileName(file)}: <{tag}>");
            }
        }

        Assert.True(unresolved.Count == 0,
            "These tags do not resolve to MudBlazor components and will render as dead markup:\n  "
            + string.Join("\n  ", unresolved.Distinct()));
    }

    [Fact]
    public void Razor_files_do_not_reference_components_removed_in_MudBlazor_9()
    {
        // Named explicitly so the failure message says what to use instead.
        var replacements = new Dictionary<string, string>
        {
            ["MudCardActionArea"] = "a plain <a> element wrapping the card content",
            ["MudIconButton Link"] = "Href",
        };

        var problems = new List<string>();
        foreach (var file in RazorFiles())
        {
            var text = File.ReadAllText(file);
            foreach (var (removed, replacement) in replacements)
            {
                if (text.Contains("<" + removed, StringComparison.Ordinal))
                    problems.Add($"{Path.GetFileName(file)}: <{removed}> — use {replacement}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }
}
