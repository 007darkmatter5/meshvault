using MeshVault.Core.Services;

namespace MeshVault.Tests;

/// <summary>
/// Turning a name into a convention. The cases worth pinning are the ones real
/// pack filenames are full of: acronyms, version numbers and a designer name
/// with a digit in the middle of it.
/// </summary>
public class NameCasingTests
{
    [Theory]
    [InlineData(NameCase.Kebab, "spring-dragon-wall-01")]
    [InlineData(NameCase.Snake, "spring_dragon_wall_01")]
    [InlineData(NameCase.Camel, "springDragonWall01")]
    [InlineData(NameCase.Pascal, "SpringDragonWall01")]
    public void Each_convention_renders_a_plain_name(NameCase casing, string expected) =>
        Assert.Equal(expected, NameCasing.Apply("Spring Dragon - Wall 01", casing));

    [Fact]
    public void Leave_as_written_changes_nothing_at_all()
    {
        // The whole compatibility story rests on this: every library that
        // predates the feature holds AsWritten, and must render exactly as it
        // did before there was a choice.
        const string name = "Spring Dragon - Wall 01 (v2).";
        Assert.Equal(name, NameCasing.Apply(name, NameCase.AsWritten));
    }

    [Fact]
    public void Capitals_inside_a_word_start_a_new_one()
    {
        Assert.Equal("spring-dragon", NameCasing.Apply("springDragon", NameCase.Kebab));
        Assert.Equal("SpringDragon", NameCasing.Apply("spring dragon", NameCase.Pascal));
    }

    [Fact]
    public void An_acronym_keeps_its_capitals_where_the_convention_keeps_any()
    {
        // "UD-001-SUP-Wall" is a real filename shape. ToTitleCase would render
        // SUP as "Sup" and lose what it was saying.
        Assert.Equal("UD001SUPWall", NameCasing.Apply("UD-001-SUP-Wall", NameCase.Pascal));
        Assert.Equal("ud-001-sup-wall", NameCasing.Apply("UD-001-SUP-Wall", NameCase.Kebab));
    }

    [Fact]
    public void A_leading_acronym_goes_down_whole_in_camel_case()
    {
        // "sUPWall" is what lowercasing only the first letter would give, and
        // it reads as a typo.
        Assert.Equal("supWall", NameCasing.Apply("SUP Wall", NameCase.Camel));
    }

    [Fact]
    public void A_run_of_capitals_before_a_lowercase_letter_splits_correctly()
    {
        Assert.Equal("xml-http-request", NameCasing.Apply("XMLHttpRequest", NameCase.Kebab));
    }

    [Fact]
    public void A_digit_beside_a_letter_is_not_a_word_break()
    {
        // Splitting here would render Cinderwing3D as "cinderwing-3-d", which
        // is not what anybody typing a designer's name meant.
        Assert.Equal("cinderwing3d", NameCasing.Apply("Cinderwing3D", NameCase.Kebab));
        Assert.Equal("Cinderwing3D", NameCasing.Apply("Cinderwing3D", NameCase.Pascal));
    }

    [Fact]
    public void Runs_of_punctuation_collapse_rather_than_doubling_the_separator()
    {
        Assert.Equal("wall-door", NameCasing.Apply("Wall  --  Door", NameCase.Kebab));
        Assert.Equal("wall_door", NameCasing.Apply("Wall  --  Door", NameCase.Snake));
    }

    [Fact]
    public void A_name_with_no_letters_or_digits_renders_as_nothing()
    {
        // PathTemplate drops an empty segment, which is the right answer: there
        // was no name there to convert.
        Assert.Equal("", NameCasing.Apply("---", NameCase.Kebab));
    }
}
