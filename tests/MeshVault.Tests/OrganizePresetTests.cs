using MeshVault.Core.Services;
using MeshVault.Data;

namespace MeshVault.Tests;

/// <summary>
/// The named layouts. The point of them is that a person picks one instead of
/// working out how four fields interact, so what is worth pinning is that each
/// one is internally coherent and that the page can tell which is selected.
/// </summary>
public class OrganizePresetTests
{
    [Fact]
    public void Every_preset_round_trips_to_itself()
    {
        // The page derives the selected radio from the fields rather than
        // remembering it. A preset that did not match its own rules would apply
        // cleanly and then show nothing selected.
        foreach (var preset in OrganizePresets.All)
            Assert.Equal(preset.Name, OrganizePresets.Matching(preset.Rules)?.Name);
    }

    [Fact]
    public void A_template_of_ones_own_matches_nothing()
    {
        var mine = new OrganizeRules { FolderTemplate = "{year}/{tag}/{model}" };

        Assert.Null(OrganizePresets.Matching(mine));
    }

    [Fact]
    public void A_file_template_left_over_from_before_does_not_decide_the_answer()
    {
        // Renaming is off, so the file half changes nothing on disk. Letting it
        // vote would leave two pages that behave identically disagreeing about
        // which preset they are on.
        var byDesigner = OrganizePresets.All.First(p => p.Name == "By designer");
        var stale = byDesigner.Rules with { FileTemplate = "{model}-{variant}", FileCase = NameCase.Kebab };

        Assert.Equal("By designer", OrganizePresets.Matching(stale)?.Name);
    }

    [Fact]
    public void Renaming_is_the_last_thing_the_ladder_offers()
    {
        // Renaming is the only choice here that destroys something, so it
        // belongs at the end, reached deliberately rather than met while
        // exploring. Two presets rename now -- one keeps every original name and
        // only fixes its case, the other rebuilds them -- and both have to sit
        // after everything that merely moves files.
        var renaming = OrganizePresets.All
            .Select((preset, index) => (preset, index))
            .Where(x => x.preset.Rules.RenameFiles)
            .Select(x => x.index)
            .ToList();

        Assert.NotEmpty(renaming);
        Assert.Equal(
            Enumerable.Range(OrganizePresets.All.Count - renaming.Count, renaming.Count),
            renaming);
    }

    [Fact]
    public void A_renaming_preset_never_throws_away_which_cut_of_a_mini_a_file_is()
    {
        // The original name is the only record that a mesh was hollowed. A
        // preset may keep it with {file}, or rebuild it from {sculpt} and
        // {variant} -- but one doing neither would quietly flatten every cut of
        // a mini onto a single name, and then number the collisions.
        foreach (var preset in OrganizePresets.All.Where(p => p.Rules.RenameFiles))
        {
            var template = preset.Rules.FileTemplate;

            Assert.True(
                template.Contains("{file}") || template.Contains("{variant}"),
                $"\"{preset.Name}\" renames without keeping {{file}} or rendering "
                + "{variant}, so which cut of a mini each file is would be lost.");
        }
    }

    [Fact]
    public void Every_preset_uses_tokens_that_exist()
    {
        // An unknown token renders as nothing, so a typo here would ship a
        // preset that quietly files everything one folder short.
        foreach (var preset in OrganizePresets.All)
        {
            Assert.Empty(PathTemplate.UnknownTokens(preset.Rules.FolderTemplate, forFile: false));

            if (preset.Rules.RenameFiles)
                Assert.Empty(PathTemplate.UnknownTokens(preset.Rules.FileTemplate, forFile: true));
        }
    }

    [Fact]
    public void Every_preset_renders_a_real_path()
    {
        var tokens = new Dictionary<string, string?>
        {
            ["designer"] = "Dungeon Blocks",
            ["model"] = "UD 001 Wall",
            ["sculpt"] = "UD 001 Wall",
            ["collection"] = "The Ultimate Dungeon",
        };

        foreach (var preset in OrganizePresets.All)
        {
            var rendered = PathTemplate.Render(
                preset.Rules.FolderTemplate, tokens, forFile: false, preset.Rules.FolderCase);

            Assert.NotEmpty(rendered);
            Assert.DoesNotContain('{', rendered);
        }
    }
}
