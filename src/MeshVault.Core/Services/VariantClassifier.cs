using System.Text;
using System.Text.RegularExpressions;
using MeshVault.Core.Models;

namespace MeshVault.Core.Services;

/// <summary>
/// What a file's name says about it: which sculpt it is, and which flavour of
/// that sculpt.
/// </summary>
/// <param name="Key">
/// Normalised identity of the sculpt. Files sharing a key are the same thing
/// exported differently, so they belong together rather than side by side.
/// </param>
/// <param name="DisplayName">The same name with its original casing, for headings.</param>
/// <param name="Labels">Variant names found, best-ranked first.</param>
/// <param name="Rank">
/// Combined <see cref="VariantDefinition.PreviewRank"/> of those labels. Zero
/// for a plain export, which is what makes "show the cleanest copy" a sort.
/// </param>
public record VariantClassification(
    string Key, string DisplayName, IReadOnlyList<string> Labels, int Rank)
{
    /// <summary>The labels as one display string, or null when this is the plain export.</summary>
    public string? Label => Labels.Count == 0 ? null : string.Join(", ", Labels);
}

/// <summary>
/// Splits a mesh file's name into "which sculpt" and "which variant", using the
/// vocabulary the user curates.
///
/// Creators ship the same sculpt several times over — supported and unsupported,
/// hollowed and solid, with and without their logo — and encode which is which
/// in the filename or a containing folder. Nothing else in the app can tell
/// those apart from genuinely different models, so a pack of forty minis reads
/// as two hundred unrelated files.
///
/// This proposes; it never decides. A file the user has corrected carries
/// <see cref="ModelFile.VariantSetByUser"/> and is left alone, the same way a
/// hand-written model name survives a rescan.
///
/// Deliberately does <b>not</b> treat part suffixes (A/B, part1, piece 2) as
/// variants. Those are halves of one print, and folding them together would
/// hide the other half rather than tidy it away.
/// </summary>
public class VariantClassifier
{
    /// <summary>
    /// Identifies what a variant pass produces in this build. Bump it whenever
    /// the same definitions would now give a different result — a change to the
    /// parsing, or to anything else the pass derives, such as which export a
    /// model's card image points at. Existing rows are recomputed at startup on
    /// a mismatch. The definitions are fingerprinted alongside it, so editing
    /// them has the same effect.
    /// </summary>
    /// <remarks>
    /// 2: card images are re-picked to avoid supported exports.
    /// 3: the vocabulary moved from a rules blob to curated definitions.
    /// </remarks>
    public const int Version = 3;

    /// <summary>
    /// Rank given to a label with no definition behind it — today only a scale.
    /// Enough to lose to a plain export, not enough to lose to a supported one.
    /// </summary>
    private const int UndefinedRank = 1;

    /// <summary>Reads a scale as a variant: "Goblin_32mm" and "Goblin_75mm" are one sculpt.</summary>
    /// <remarks>
    /// Built in rather than curated because it is a shape, not a vocabulary.
    /// Nobody wants to add a row for every millimetre they might one day own.
    /// </remarks>
    private static readonly Regex ScaleToken = new(@"^\d{1,3}(\.\d+)?mm$", RegexOptions.Compiled);

    private readonly record struct Match(string Label, int Rank, bool IsFiller);

    private readonly Dictionary<string, Match> _byPhrase;
    private readonly int _longestPhrase;

    public VariantClassifier(IEnumerable<VariantDefinition>? definitions = null)
    {
        Definitions = (definitions ?? VariantDefinition.Starter()).ToList();
        (_byPhrase, _longestPhrase) = Index(Definitions);
    }

    public IReadOnlyList<VariantDefinition> Definitions { get; }

    /// <summary>
    /// Identifies <paramref name="pathWithinModel"/> — a file path relative to
    /// its model's own folder, such as "Supported/Goblin_A.stl".
    /// </summary>
    /// <param name="modelName">
    /// Falls back to this when the name carries nothing but variant words, as
    /// "Goblin/supported.stl" does. Without it such a file would key on the
    /// word "supported" and never meet its unsupported twin.
    /// </param>
    public VariantClassification Classify(string modelName, string pathWithinModel)
    {
        var words = new List<Word>();
        foreach (var segment in SplitPath(pathWithinModel))
            words.AddRange(Tokenize(segment));

        var kept = new List<string>();
        var found = new List<Match>();

        for (var i = 0; i < words.Count;)
        {
            var (match, length) = MatchAt(words, i);
            if (length == 0)
            {
                // The original spelling, so the heading reads "Dwarf MK2" rather
                // than the lowercased form used for matching.
                kept.Add(words[i].Original);
                i++;
                continue;
            }

            if (!match.IsFiller && !found.Any(f => f.Label == match.Label)) found.Add(match);
            i += length;
        }

        found.Sort((a, b) => a.Rank.CompareTo(b.Rank));

        var display = kept.Count > 0 ? string.Join(' ', kept) : modelName;
        return new VariantClassification(
            Normalize(display),
            display.Trim(),
            found.Select(f => f.Label).ToList(),
            found.Sum(f => f.Rank));
    }

    /// <summary>
    /// Records on <paramref name="file"/> which sculpt it is an export of, and
    /// returns true when that changed what was already there.
    /// </summary>
    /// <remarks>
    /// Only meshes and CAD files are sculpts. An image or a readme is not a
    /// variant of anything, and keying them would scatter them through the list.
    ///
    /// A file the user has set by hand is never touched. That is the whole
    /// point of the flag: detection guesses well but not always, and a
    /// correction that a rescan could undo is not a correction.
    /// </remarks>
    public bool Apply(ModelEntry entry, ModelFile file)
    {
        if (file.VariantSetByUser) return false;

        string? key = null, name = null, label = null;
        var rank = 0;

        if (file.Kind is FileKind.Mesh or FileKind.Cad)
        {
            var fallback = string.IsNullOrWhiteSpace(entry.Name)
                ? Path.GetFileNameWithoutExtension(file.FileName)
                : entry.Name;

            var read = Classify(fallback, WithinModel(entry.RelativePath, file.RelativePath));
            (key, name, label, rank) = (read.Key, read.DisplayName, read.Label, read.Rank);
        }

        // A spelling that differs only in case is not new information, and the
        // app rewrites filenames itself: organizing under a case convention
        // turns "UD-067-Hole-Trap.stl" into "ud-067-hole-trap.stl", and the
        // next scan would read the heading back off its own handiwork. The key
        // is lowercased either way, so nothing groups differently -- the only
        // thing at stake is the spelling shown, which came from whoever named
        // the file and is not ours to quietly restyle.
        if (name is not null && file.SculptKey == key
            && string.Equals(file.SculptName, name, StringComparison.OrdinalIgnoreCase))
        {
            name = file.SculptName;
        }

        if (file.SculptKey == key && file.SculptName == name
            && file.VariantLabel == label && file.VariantRank == rank)
            return false;

        file.SculptKey = key;
        file.SculptName = name;
        file.VariantLabel = label;
        file.VariantRank = rank;
        return true;
    }

    /// <summary>
    /// Strips a model's own folder off one of its file paths, leaving what
    /// <see cref="Classify"/> reads. Both paths are relative to the library root.
    /// </summary>
    public static string WithinModel(string modelRelativePath, string fileRelativePath)
    {
        if (string.IsNullOrEmpty(modelRelativePath)) return fileRelativePath;

        return fileRelativePath.Length > modelRelativePath.Length
            && fileRelativePath[modelRelativePath.Length] == '/'
            && fileRelativePath.StartsWith(modelRelativePath, StringComparison.OrdinalIgnoreCase)
                ? fileRelativePath[(modelRelativePath.Length + 1)..]
                : fileRelativePath;
    }

    /// <summary>
    /// Turns a sculpt name into the key files are grouped by. Public so a name
    /// typed by hand lands in the same group as a detected one.
    /// </summary>
    public static string NormalizeKey(string name) => Normalize(name);

    /// <summary>
    /// Identity of the vocabulary in force, stored so a change to it is noticed
    /// at startup and the affected rows recomputed without rescanning the share.
    /// </summary>
    public string Fingerprint()
    {
        var text = string.Join('\n', Definitions
            .OrderBy(d => d.NormalizedName, StringComparer.Ordinal)
            .Select(d => $"{d.NormalizedName}|{d.MatchTerms}|{d.PreviewRank}|{d.IsFiller}"));

        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return $"{Version}:{Convert.ToHexString(bytes)[..16]}";
    }

    /// <summary>Longest run of words at <paramref name="start"/> that matches a definition.</summary>
    private (Match Match, int Length) MatchAt(List<Word> words, int start)
    {
        var longest = Math.Min(_longestPhrase, words.Count - start);
        for (var length = longest; length >= 1; length--)
        {
            var phrase = string.Join(' ', words.GetRange(start, length).Select(w => w.Lower));
            if (_byPhrase.TryGetValue(phrase, out var match)) return (match, length);
        }

        return ScaleToken.IsMatch(words[start].Lower)
            ? (new Match(words[start].Lower, UndefinedRank, false), 1)
            : (default, 0);
    }

    private static (Dictionary<string, Match>, int) Index(IEnumerable<VariantDefinition> definitions)
    {
        var byPhrase = new Dictionary<string, Match>(StringComparer.Ordinal);
        var longest = 1;

        // Best-ranked first, so a term two definitions claim resolves to the one
        // the user put ahead of the other rather than to whichever loaded first.
        foreach (var definition in definitions.OrderBy(d => d.PreviewRank))
        {
            if (string.IsNullOrWhiteSpace(definition.Name)) continue;

            foreach (var term in definition.Terms())
            {
                var words = Tokenize(term);
                if (words.Count == 0) continue;

                byPhrase.TryAdd(
                    string.Join(' ', words.Select(w => w.Lower)),
                    new Match(definition.Name, definition.PreviewRank, definition.IsFiller));

                longest = Math.Max(longest, words.Count);
            }
        }

        return (byPhrase, longest);
    }

    private static IEnumerable<string> SplitPath(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            yield return i == segments.Length - 1
                ? Path.GetFileNameWithoutExtension(segments[i])
                : segments[i];
        }
    }

    /// <summary>
    /// One word of a name: lowercased for matching, and as written for display.
    /// </summary>
    private readonly record struct Word(string Lower, string Original);

    /// <summary>
    /// The words in a name. Separators, punctuation and camelCase boundaries all
    /// count, so "GoblinKing_v2 (pre-supported)" reads as
    /// goblin / king / v2 / pre / supported.
    /// </summary>
    private static List<Word> Tokenize(string text)
    {
        var words = new List<Word>();
        var word = new StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            // A capital after a lowercase starts a new word, but a digit after a
            // letter does not: "32mm" and "v2" are each one word.
            if (word.Length > 0 && char.IsUpper(c) && char.IsLower(text[i - 1])) Flush();
            word.Append(c);
        }

        Flush();
        return words;

        void Flush()
        {
            if (word.Length == 0) return;
            var written = word.ToString();
            words.Add(new Word(written.ToLowerInvariant(), written));
            word.Clear();
        }
    }

    private static string Normalize(string text) =>
        string.Join(' ', Tokenize(text).Select(w => w.Lower));
}
