using MeshVault.Core.Models;
using MeshVault.Core.Services;

namespace MeshVault.Tests;

public class VariantClassifierTests
{
    private readonly VariantClassifier _classifier = new();

    private (string Key, string? Label) Read(string path, string model = "Pack") =>
        _classifier.Classify(model, path) is var r ? (r.Key, r.Label) : default;

    [Theory]
    // The spellings creators actually ship, all meaning the same two things.
    [InlineData("Goblin_supported.stl", "Supported")]
    [InlineData("Goblin presupported.stl", "Supported")]
    [InlineData("Goblin-pre-supported.stl", "Supported")]
    [InlineData("Goblin_sup.stl", "Supported")]
    [InlineData("Goblin (with supports).stl", "Supported")]
    [InlineData("Goblin_unsupported.stl", "Unsupported")]
    [InlineData("Goblin_unsup.stl", "Unsupported")]
    [InlineData("Goblin no supports.stl", "Unsupported")]
    [InlineData("Goblin_hollowed.stl", "Hollowed")]
    [InlineData("Goblin_nologo.stl", "No logo")]
    [InlineData("Goblin_no-logo.stl", "No logo")]
    [InlineData("Goblin.stl", null)]
    public void Reads_the_variant_off_the_file_name(string file, string? expected)
    {
        var read = Read(file);

        Assert.Equal("goblin", read.Key);
        Assert.Equal(expected, read.Label);
    }

    [Fact]
    public void Unsupported_is_not_read_as_supported()
    {
        // "unsupported" contains "supported", and a substring match would call
        // the clean export a supported one — the exact mistake that would put
        // a hairball on every card.
        Assert.Equal("Unsupported", Read("Goblin_unsupported.stl").Label);
        Assert.Equal("Unsupported", Read("Goblin no support.stl").Label);
    }

    [Fact]
    public void Groups_a_sculpt_split_across_supported_and_unsupported_folders()
    {
        // The layout that currently produces two unrelated models, one of them
        // named "Supported".
        var supported = Read("Supported/Tavern.stl");
        var unsupported = Read("Unsupported/Tavern.stl");

        Assert.Equal(supported.Key, unsupported.Key);
        Assert.Equal("Supported", supported.Label);
        Assert.Equal("Unsupported", unsupported.Label);
    }

    [Fact]
    public void Falls_back_to_the_model_name_when_the_file_says_nothing_else()
    {
        // "Tavern/supported.stl" carries no sculpt name of its own. Keying it on
        // the word "supported" would leave it unable to find its own twin.
        var supported = Read("supported.stl", model: "Tavern");
        var unsupported = Read("unsupported.stl", model: "Tavern");

        Assert.Equal("tavern", supported.Key);
        Assert.Equal(supported.Key, unsupported.Key);
        Assert.NotEqual(supported.Label, unsupported.Label);
    }

    [Fact]
    public void Keeps_part_letters_apart()
    {
        // A and B are halves of one print, not two flavours of one sculpt.
        // Folding them together would hide half the model.
        Assert.NotEqual(Read("Dragon_A_supported.stl").Key, Read("Dragon_B_supported.stl").Key);
    }

    [Theory]
    // A real pack abbreviates in the middle of the name rather than at the end.
    [InlineData("UD-001-SUP-Wall.stl", "Supported")]
    [InlineData("UD-001-HOL-Wall.stl", "Hollowed")]
    [InlineData("UD-001-NL-Wall.stl", "No logo")]
    [InlineData("UD-001-Wall.stl", null)]
    public void Reads_the_abbreviations_packs_actually_use(string file, string? expected)
    {
        var read = Read(file);

        Assert.Equal("ud 001 wall", read.Key);
        Assert.Equal(expected, read.Label);
    }

    [Fact]
    public void Reads_a_scale_as_a_variant()
    {
        var small = Read("Goblin_32mm.stl");
        var large = Read("Goblin_75mm.stl");

        Assert.Equal(small.Key, large.Key);
        Assert.Equal("32mm", small.Label);
        Assert.Equal("75mm", large.Label);
    }

    [Fact]
    public void Collects_several_variant_words_at_once()
    {
        // Listed in the order the user ranked them, not the order they appear in
        // the filename, so the same pair always reads the same way.
        Assert.Equal("Hollowed, Supported", Read("Goblin_supported_hollowed.stl").Label);
        Assert.Equal("Hollowed, Supported", Read("Goblin_hollowed_supported.stl").Label);
    }

    [Fact]
    public void Splits_camel_case_so_spelling_does_not_separate_a_sculpt()
    {
        Assert.Equal(Read("GoblinKing.stl").Key, Read("goblin_king.stl").Key);
    }

    [Fact]
    public void Distinct_sculpts_keep_distinct_keys()
    {
        Assert.NotEqual(Read("Goblin.stl").Key, Read("Orc.stl").Key);
        Assert.NotEqual(Read("Tavern 01.stl").Key, Read("Tavern 02.stl").Key);
    }

    [Fact]
    public void A_curated_definition_changes_the_fingerprint()
    {
        // The fingerprint is what tells startup to re-read the library, so an
        // edit that left it alone would silently never take effect.
        var custom = new VariantClassifier(
        [
            .. VariantDefinition.Starter(),
            new VariantDefinition
            {
                Name = "House style", NormalizedName = "house style",
                MatchTerms = "mysupports", PreviewRank = 40,
            },
        ]);

        Assert.NotEqual(_classifier.Fingerprint(), custom.Fingerprint());
        Assert.Equal("House style", custom.Classify("Pack", "Goblin_mysupports.stl").Label);
    }

    [Fact]
    public void An_empty_vocabulary_leaves_every_file_standing_alone()
    {
        // Deleting every definition is allowed. Nothing should be labelled, and
        // the sculpt name should keep the words that would have been stripped.
        var bare = new VariantClassifier([]);
        var read = bare.Classify("Pack", "Goblin_supported.stl");

        Assert.Null(read.Label);
        Assert.Equal("goblin supported", read.Key);
        Assert.Equal(0, read.Rank);
    }

    [Fact]
    public void A_term_two_definitions_claim_goes_to_the_better_ranked_one()
    {
        var custom = new VariantClassifier(
        [
            new VariantDefinition { Name = "Rough", MatchTerms = "raw", PreviewRank = 20 },
            new VariantDefinition { Name = "Unsupported", MatchTerms = "raw", PreviewRank = 1 },
        ]);

        Assert.Equal("Unsupported", custom.Classify("Pack", "Goblin_raw.stl").Label);
    }

    [Fact]
    public void Only_meshes_and_cad_are_sculpts()
    {
        var entry = new ModelEntry { RelativePath = "Packs/Dungeon", Name = "Dungeon" };
        var readme = new ModelFile
        {
            RelativePath = "Packs/Dungeon/readme.txt",
            FileName = "readme.txt",
            Kind = FileKind.Document,
        };

        Assert.False(_classifier.Apply(entry, readme));
        Assert.Null(readme.SculptKey);
    }

    [Fact]
    public void Apply_records_the_sculpt_and_reports_whether_it_changed()
    {
        var entry = new ModelEntry { RelativePath = "Packs/Dungeon", Name = "Dungeon" };
        var file = new ModelFile
        {
            RelativePath = "Packs/Dungeon/Supported/Tavern.stl",
            FileName = "Tavern.stl",
            Kind = FileKind.Mesh,
        };

        Assert.True(_classifier.Apply(entry, file));
        Assert.Equal("tavern", file.SculptKey);
        Assert.Equal("Tavern", file.SculptName);
        Assert.Equal("Supported", file.VariantLabel);

        // Nothing to save on a rescan that found the same name.
        Assert.False(_classifier.Apply(entry, file));
    }

    [Fact]
    public void Supported_exports_rank_worst_for_preview()
    {
        var plain = Rank("Goblin.stl");
        var unsupported = Rank("Goblin_unsupported.stl");
        var hollowed = Rank("Goblin_hollowed.stl");
        var supported = Rank("Goblin_supported.stl");

        Assert.Equal(0, plain);
        Assert.True(unsupported < supported);
        Assert.True(hollowed < Rank("Goblin_supported_hollowed.stl"));

        int Rank(string file) => _classifier.Classify("Pack", file).Rank;
    }

    [Fact]
    public void The_user_decides_what_a_good_preview_is()
    {
        // Ranking is a preference on a row the user owns, not a fact in the
        // code: somebody who wants to see the supports says so here.
        var custom = new VariantClassifier(
        [
            new VariantDefinition { Name = "Supported", MatchTerms = "supported", PreviewRank = 0 },
            new VariantDefinition { Name = "Unsupported", MatchTerms = "unsupported", PreviewRank = 9 },
        ]);

        Assert.True(custom.Classify("Pack", "Goblin_supported.stl").Rank
                  < custom.Classify("Pack", "Goblin_unsupported.stl").Rank);
    }

    [Fact]
    public void A_file_set_by_hand_is_left_alone()
    {
        // The whole bargain of the override: detection proposes, the person
        // decides, and the next pass does not argue.
        var entry = new ModelEntry { RelativePath = "Dungeon", Name = "Dungeon" };
        var file = new ModelFile
        {
            RelativePath = "Dungeon/UD-003-SUP-Wall Skuls 2.stl",
            FileName = "UD-003-SUP-Wall Skuls 2.stl",
            Kind = FileKind.Mesh,
            SculptKey = "ud 003 wall skulls 2",
            SculptName = "UD 003 Wall Skulls 2",
            VariantLabel = "Supported",
            VariantRank = 30,
            VariantSetByUser = true,
        };

        Assert.False(_classifier.Apply(entry, file));
        Assert.Equal("ud 003 wall skulls 2", file.SculptKey);
    }

    [Fact]
    public void Renaming_a_file_to_another_case_does_not_restyle_its_sculpt()
    {
        // The sequence that quietly relabelled a whole library: organize under
        // a case convention, then rescan. The heading was read back off a file
        // name the app had itself rewritten, so the capitals the creator chose
        // survived exactly as long as the file that carried them.
        var entry = new ModelEntry { RelativePath = "Dungeon", Name = "Dungeon" };
        var file = new ModelFile
        {
            RelativePath = "Dungeon/UD-067-HOL-Hole Trap.stl",
            FileName = "UD-067-HOL-Hole Trap.stl",
            Kind = FileKind.Mesh,
        };

        Assert.True(_classifier.Apply(entry, file));
        Assert.Equal("UD 067 Hole Trap", file.SculptName);

        file.RelativePath = "Dungeon/ud-067-hol-hole-trap.stl";
        file.FileName = "ud-067-hol-hole-trap.stl";

        Assert.False(_classifier.Apply(entry, file));
        Assert.Equal("UD 067 Hole Trap", file.SculptName);
        Assert.Equal("ud 067 hole trap", file.SculptKey);
    }

    [Fact]
    public void A_rename_that_says_something_new_still_takes_effect()
    {
        // The other half of the bargain. Holding the spelling must not turn
        // into holding the name: a file actually renamed on the share is
        // reporting a different sculpt, and detection is right to follow it.
        var entry = new ModelEntry { RelativePath = "Dungeon", Name = "Dungeon" };
        var file = new ModelFile
        {
            RelativePath = "Dungeon/Goblin.stl",
            FileName = "Goblin.stl",
            Kind = FileKind.Mesh,
        };

        Assert.True(_classifier.Apply(entry, file));
        Assert.Equal("Goblin", file.SculptName);

        file.RelativePath = "Dungeon/Goblin King.stl";
        file.FileName = "Goblin King.stl";

        Assert.True(_classifier.Apply(entry, file));
        Assert.Equal("Goblin King", file.SculptName);
        Assert.Equal("goblin king", file.SculptKey);
    }
}
