using MeshVault.Core.Services;

namespace MeshVault.Tests;

/// <summary>
/// Every rendered template becomes a real path in someone's model library, so
/// the interesting cases are all the ones where a name is hostile rather than
/// the ones where it is ordinary.
/// </summary>
public class PathTemplateTests
{
    private static string Render(string template, params (string Key, string? Value)[] values) =>
        PathTemplate.Render(template, values.ToDictionary(v => v.Key, v => v.Value), forFile: false);

    [Fact]
    public void Tokens_are_replaced()
    {
        Assert.Equal("Prusa Research/3DBenchy",
            Render("{designer}/{model}", ("designer", "Prusa Research"), ("model", "3DBenchy")));
    }

    [Fact]
    public void Literal_text_around_tokens_is_kept()
    {
        Assert.Equal("by Prusa/3DBenchy",
            Render("by {designer}/{model}", ("designer", "Prusa"), ("model", "3DBenchy")));
    }

    [Fact]
    public void An_empty_value_falls_back_rather_than_vanishing()
    {
        // Otherwise a model with no designer lands at the library root, mixed in
        // with the folders that were organised properly.
        Assert.Equal("Unsorted/3DBenchy",
            Render("{designer}/{model}", ("designer", null), ("model", "3DBenchy")));
    }

    [Fact]
    public void Whitespace_counts_as_empty()
    {
        Assert.Equal("Unsorted/Benchy",
            Render("{designer}/{model}", ("designer", "   "), ("model", "Benchy")));
    }

    [Fact]
    public void A_backslash_template_means_the_same_as_a_slash_one()
    {
        Assert.Equal("Prusa/Benchy",
            Render(@"{designer}\{model}", ("designer", "Prusa"), ("model", "Benchy")));
    }

    [Theory]
    [InlineData("Cinderwing3D/Dragons", "Cinderwing3D-Dragons")]
    [InlineData(@"back\slash", "back-slash")]
    public void A_value_can_never_introduce_a_folder_level(string designer, string expected)
    {
        // A designer called "A/B" must not silently become two folders.
        Assert.Equal($"{expected}/Benchy", Render("{designer}/{model}", ("designer", designer), ("model", "Benchy")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("...")]
    public void A_value_cannot_climb_out_of_the_library(string designer)
    {
        Assert.Equal("Benchy", Render("{designer}/{model}", ("designer", designer), ("model", "Benchy")));
    }

    [Theory]
    [InlineData("What? Is: This*")]
    [InlineData("Quote\"Pipe|")]
    public void Characters_no_filesystem_accepts_are_replaced(string name)
    {
        var rendered = Render("{model}", ("model", name));

        Assert.DoesNotContain('?', rendered);
        Assert.DoesNotContain(':', rendered);
        Assert.DoesNotContain('*', rendered);
        Assert.DoesNotContain('"', rendered);
        Assert.DoesNotContain('|', rendered);
        Assert.NotEmpty(rendered);
    }

    [Fact]
    public void Control_characters_are_dropped()
    {
        Assert.Equal("Benchy", Render("{model}", ("model", "Benchy")));
    }

    [Fact]
    public void A_trailing_dot_is_removed()
    {
        // Windows strips these when creating the folder, so a folder made as
        // "Model V2." can never be opened by that name again.
        Assert.Equal("Model V2", Render("{model}", ("model", "Model V2.")));
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        Assert.Equal("Benchy", Render("{model}", ("model", "  Benchy  ")));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("LPT9.stl")]
    public void Names_Windows_reserves_for_devices_are_escaped(string name)
    {
        // A library on Unraid is usually reached from Windows over SMB, where a
        // folder called CON cannot be created at all.
        var rendered = Render("{model}", ("model", name));

        Assert.StartsWith("_", rendered);
    }

    [Fact]
    public void A_name_that_is_only_punctuation_does_not_produce_an_empty_segment()
    {
        Assert.Equal("Benchy", Render("{designer}/{model}", ("designer", "..."), ("model", "Benchy")));
    }

    [Fact]
    public void Absurdly_long_names_are_capped()
    {
        var rendered = Render("{model}", ("model", new string('x', 400)));

        Assert.Equal(PathTemplate.MaxSegmentLength, rendered.Length);
    }

    [Fact]
    public void Empty_segments_do_not_leave_double_slashes()
    {
        Assert.Equal("Prusa/Benchy",
            Render("{designer}//{model}", ("designer", "Prusa"), ("model", "Benchy")));
    }

    [Fact]
    public void An_unclosed_brace_is_left_as_typed_rather_than_swallowing_the_rest()
    {
        Assert.Equal("Prusa/{model", Render("{designer}/{model", ("designer", "Prusa")));
    }

    [Fact]
    public void Unknown_tokens_are_reported()
    {
        var unknown = PathTemplate.UnknownTokens("{designer}/{nonsense}/{model}", forFile: false);

        Assert.Equal(["nonsense"], unknown);
    }

    [Fact]
    public void File_only_tokens_are_unknown_in_a_folder_template()
    {
        Assert.Equal(["file"], PathTemplate.UnknownTokens("{designer}/{file}", forFile: false));
        Assert.Empty(PathTemplate.UnknownTokens("{designer}/{file}", forFile: true));
    }

    [Fact]
    public void The_same_unknown_token_is_only_reported_once()
    {
        Assert.Equal(["oops"], PathTemplate.UnknownTokens("{oops}/{oops}", forFile: false));
    }

    [Fact]
    public void A_file_template_can_keep_the_original_name()
    {
        // The recommended shape: the catalog name plus whatever the original
        // filename encoded, so "presupported" or "body_v2" is not thrown away.
        var rendered = PathTemplate.Render(
            "{model} - {file}",
            new Dictionary<string, string?> { ["model"] = "Dragon", ["file"] = "presupported" },
            forFile: true);

        Assert.Equal("Dragon - presupported", rendered);
    }

    private static string Render(string template, NameCase casing,
        params (string Key, string? Value)[] values) =>
        PathTemplate.Render(
            template, values.ToDictionary(v => v.Key, v => v.Value), forFile: false, casing);

    [Fact]
    public void A_casing_convention_applies_to_each_segment_separately()
    {
        // The separators have to survive. NameCasing treats anything that is
        // not a letter or digit as a word break, so a whole path handed to it
        // would come back as one long folder name.
        Assert.Equal("prusa-research/3d-benchy-v2",
            Render("{designer}/{model}", NameCase.Kebab,
                ("designer", "Prusa Research"), ("model", "3DBenchy V2")));
    }

    [Fact]
    public void Literal_text_in_the_template_is_cased_along_with_the_tokens()
    {
        // The convention governs the whole name, not just the parts that came
        // out of a token. Leaving " by " uncased would produce "prusa-By-..."
        // and read as a bug.
        Assert.Equal("by-prusa/3d-benchy",
            Render("by {designer}/{model}", NameCase.Kebab,
                ("designer", "Prusa"), ("model", "3DBenchy")));
    }

    [Fact]
    public void A_fallback_placeholder_is_cased_like_anything_else()
    {
        Assert.Equal("unsorted/3d-benchy",
            Render("{designer}/{model}", NameCase.Kebab, ("model", "3DBenchy")));
    }

    [Fact]
    public void A_value_still_cannot_climb_out_of_the_library()
    {
        // Sanitising runs after casing, so it keeps the last word on the things
        // that matter: a token that tried to introduce a separator is already
        // neutralised before casing sees it.
        Assert.Equal("cinderwing3d-dragons/wall",
            Render("{designer}/{model}", NameCase.Kebab,
                ("designer", "Cinderwing3D/Dragons"), ("model", "Wall")));
    }

    [Fact]
    public void A_reserved_device_name_is_still_escaped_after_casing()
    {
        // Casing runs first for exactly this reason. Run it afterwards and the
        // underscore Sanitize added would be eaten as a word break, handing
        // "con" straight back to Windows.
        Assert.Equal("_con", Render("{model}", NameCase.Kebab, ("model", "CON")));
    }

    [Fact]
    public void A_separator_with_nothing_beyond_it_is_trimmed()
    {
        // "{sculpt}-{variant}" on an unmarked file. The dash is the template
        // showing through, not part of anyone's name.
        Assert.Equal("Otto Bismark", PathTemplate.Render(
            "{sculpt}-{variant}",
            new Dictionary<string, string?> { ["sculpt"] = "Otto Bismark", ["variant"] = "" },
            forFile: true));

        Assert.Equal("Otto Bismark", PathTemplate.Render(
            "{variant} {sculpt}",
            new Dictionary<string, string?> { ["sculpt"] = "Otto Bismark", ["variant"] = "" },
            forFile: true));
    }

    [Fact]
    public void An_unmarked_variant_renders_as_nothing_rather_than_a_placeholder()
    {
        // Every other token names something a model has, so a gap is worth
        // marking. A variant is a thing a file either is or is not.
        Assert.Equal("Wall", PathTemplate.Render(
            "{sculpt}-{variant}",
            new Dictionary<string, string?> { ["sculpt"] = "Wall" },
            forFile: true));
    }

    [Fact]
    public void Leave_as_written_renders_the_way_it_always_did()
    {
        Assert.Equal("Prusa Research/3DBenchy V2",
            Render("{designer}/{model}", NameCase.AsWritten,
                ("designer", "Prusa Research"), ("model", "3DBenchy V2")));
    }
}
