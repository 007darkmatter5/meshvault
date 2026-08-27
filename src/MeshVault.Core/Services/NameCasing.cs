using System.Text;

namespace MeshVault.Core.Services;

/// <summary>A convention for joining the words of a name.</summary>
public enum NameCase
{
    /// <summary>
    /// No convention. The template renders exactly as it is written, spaces,
    /// punctuation and capitals included.
    /// </summary>
    /// <remarks>
    /// Zero on purpose, so it is what every existing library already has stored
    /// and what a default-constructed <c>OrganizeRules</c> means.
    /// </remarks>
    AsWritten = 0,

    /// <summary>spring-dragon-wall</summary>
    Kebab = 1,

    /// <summary>spring_dragon_wall</summary>
    Snake = 2,

    /// <summary>springDragonWall</summary>
    Camel = 3,

    /// <summary>SpringDragonWall</summary>
    Pascal = 4,
}

/// <summary>
/// Rewrites one name into a casing convention.
/// </summary>
/// <remarks>
/// Works on a single path segment, never a path: it treats every non-alphanumeric
/// character as a word break, so handing it a whole path would eat the slashes.
/// <see cref="PathTemplate"/> splits first and applies this to each segment.
///
/// Case is only ever *added* to a name, never taken away, in one respect that
/// matters: the tail of each word is left exactly as it was for the two
/// conventions that keep capitals. "UD-001-SUP-Wall" is a real filename from a
/// real pack, and lowercasing the rest of each word would turn SUP into Sup and
/// 3D into 3d — losing the very thing the acronym was carrying.
/// </remarks>
public static class NameCasing
{
    /// <summary>What to show in a picker, in the order they should be offered.</summary>
    public static IReadOnlyList<(NameCase Case, string Label, string Example)> Choices =>
    [
        (NameCase.AsWritten, "Leave as written", "Spring Dragon - Wall 01"),
        (NameCase.Kebab, "kebab-case", "spring-dragon-wall-01"),
        (NameCase.Snake, "snake_case", "spring_dragon_wall_01"),
        (NameCase.Camel, "camelCase", "springDragonWall01"),
        (NameCase.Pascal, "PascalCase", "SpringDragonWall01"),
    ];

    /// <summary>
    /// What to call a convention in the UI.
    /// </summary>
    /// <remarks>
    /// The enum's own name is no use here: a closed picker showing "Kebab"
    /// rather than "kebab-case" is naming the thing in the code instead of the
    /// thing the person chose.
    /// </remarks>
    public static string Label(NameCase casing) =>
        Choices.FirstOrDefault(c => c.Case == casing).Label ?? casing.ToString();

    /// <summary>
    /// Rewrites <paramref name="segment"/> into <paramref name="casing"/>.
    /// </summary>
    public static string Apply(string segment, NameCase casing)
    {
        // The default costs nothing and changes nothing, which is what keeps
        // every library that predates this feature rendering as it always did.
        if (casing == NameCase.AsWritten) return segment;

        var words = Words(segment);
        if (words.Count == 0) return "";

        return casing switch
        {
            NameCase.Kebab => string.Join('-', words.Select(w => w.ToLowerInvariant())),
            NameCase.Snake => string.Join('_', words.Select(w => w.ToLowerInvariant())),
            NameCase.Pascal => string.Concat(words.Select(Capitalise)),

            // A leading acronym goes down whole -- "supWall", not "sUPWall" --
            // which is the one place camelCase and PascalCase disagree about
            // more than the first letter.
            NameCase.Camel => words[0].ToLowerInvariant()
                + string.Concat(words.Skip(1).Select(Capitalise)),

            _ => segment,
        };
    }

    /// <summary>
    /// Splits a name into words.
    /// </summary>
    /// <remarks>
    /// Three things end a word: a character that is not a letter or a digit, a
    /// lowercase letter followed by an uppercase one, and a run of capitals
    /// followed by a lowercase one — the last of these is what keeps "XMLHttp"
    /// from becoming one word and "XMLHtt/p" from becoming a silly one.
    ///
    /// A digit next to a letter is deliberately *not* a break. "Cinderwing3D"
    /// is one word, because splitting it would render as "cinderwing-3-d" and
    /// nobody typing a designer's name meant that.
    /// </remarks>
    public static IReadOnlyList<string> Words(string segment)
    {
        var words = new List<string>();
        var word = new StringBuilder();

        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];

            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (word.Length > 0 && char.IsUpper(c))
            {
                var previous = segment[i - 1];
                var startsNewWord =
                    char.IsLower(previous)
                    || (char.IsUpper(previous)
                        && i + 1 < segment.Length && char.IsLower(segment[i + 1]));

                if (startsNewWord) Flush();
            }

            word.Append(c);
        }

        Flush();
        return words;

        void Flush()
        {
            if (word.Length == 0) return;
            words.Add(word.ToString());
            word.Clear();
        }
    }

    /// <summary>
    /// Uppercases the first character and leaves the rest of the word alone.
    /// </summary>
    /// <remarks>
    /// Not <c>ToTitleCase</c>, which would lowercase the tail and quietly
    /// destroy the acronyms these names are full of.
    /// </remarks>
    private static string Capitalise(string word) =>
        char.IsUpper(word[0]) ? word : char.ToUpperInvariant(word[0]) + word[1..];
}
