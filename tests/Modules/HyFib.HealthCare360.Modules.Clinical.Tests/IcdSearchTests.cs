using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class IcdSearchTests
{
    private static readonly CodeCandidate E11 = new("E11", "Non-insulin-dependent diabetes mellitus", "type 2 diabetes T2DM NIDDM DM", false);
    private static readonly CodeCandidate E119 = new("E11.9", "Non-insulin-dependent diabetes mellitus without complications", "type 2 diabetes T2DM DM sugar", true);
    private static readonly CodeCandidate E149 = new("E14.9", "Unspecified diabetes mellitus without complications", "diabetes DM sugar", true);
    private static readonly CodeCandidate I10 = new("I10", "Essential (primary) hypertension", "HTN hypertension high BP", true);
    private static readonly CodeCandidate K358 = new("K35.8", "Acute appendicitis, other and unspecified", "appendicitis", true);
    private static readonly CodeCandidate K37 = new("K37", "Unspecified appendicitis", null, true);

    private static readonly CodeCandidate[] Catalogue = [E11, E119, E149, I10, K358, K37];

    private static IReadOnlyList<string> Search(string query, int limit = 10) =>
        IcdSearchRanker.Rank(Catalogue, c => c, query, limit).Select(c => c.Code).ToList();

    [Theory]
    [InlineData("e119", "E11.9")]
    [InlineData("E11.9", "E11.9")]
    [InlineData("E11", "E11")]
    [InlineData("i10", "I10")]
    [InlineData("j459", "J45.9")]
    [InlineData("0dtj4zz", "0DTJ4ZZ")]
    public void Codes_are_normalised(string input, string expected) => IcdSearchRanker.NormalizeCode(input).ShouldBe(expected);

    [Theory]
    [InlineData("E11", true)]
    [InlineData("e11.9", true)]
    [InlineData("T2DM", true)]
    [InlineData("diabetes", false)]
    [InlineData("type 2 diabetes", false)]
    public void Code_like_queries_are_detected(string query, bool expected) => IcdSearchRanker.LooksLikeCode(query).ShouldBe(expected);

    [Fact]
    public void Exact_code_ranks_above_its_subdivisions() => Search("E11").Take(2).ShouldBe(["E11", "E11.9"]);

    [Fact]
    public void Code_typed_without_the_dot_finds_the_code() => Search("e119").First().ShouldBe("E11.9");

    [Fact]
    public void Title_words_find_matches_billable_codes_first() =>
        Search("diabetes").ShouldBe(["E14.9", "E11.9", "E11"]);

    [Fact]
    public void Clinical_shorthand_matches_keywords() => Search("HTN").ShouldBe(["I10"]);

    [Fact]
    public void Title_prefix_outranks_word_matches() => Search("acute appendicitis").ShouldBe(["K35.8"]);

    [Fact]
    public void Words_can_be_typed_in_any_order_and_partially() => Search("appendic").ShouldBe(["K37", "K35.8"]);

    [Fact]
    public void Non_matching_entries_are_dropped_and_limit_applies()
    {
        Search("fracture").ShouldBeEmpty();
        Search("diabetes", limit: 1).ShouldHaveSingleItem().ShouldBe("E14.9");
    }

    [Theory]
    [InlineData("E11.9", "IV", "E10-E14")]
    [InlineData("I21.4", "IX", "I20-I25")]
    [InlineData("J45.9", "X", "J40-J47")]
    [InlineData("O80.0", "XV", "O80-O84")]
    [InlineData("S72.0", "XIX", "S70-S79")]
    [InlineData("T63.0", "XIX", "T51-T65")]
    [InlineData("W54", "XX", "W00-X59")]
    [InlineData("Z00.0", "XXI", "Z00-Z13")]
    [InlineData("U07.1", "XXII", "U00-U49")]
    [InlineData("C50.9", "II", "C50")]
    [InlineData("D25.9", "II", "D10-D36")]
    [InlineData("D50.9", "III", "D50-D53")]
    [InlineData("H66.9", "VIII", "H65-H75")]
    public void Codes_are_filed_under_chapter_and_block(string code, string chapter, string block)
    {
        IcdClassification.ChapterOf(code)!.Number.ShouldBe(chapter);
        IcdClassification.BlockOf(code)!.Range.ShouldBe(block);
    }

    [Theory]
    [InlineData("")]
    [InlineData("diabetes")]
    [InlineData("1A00")]
    public void Non_icd10_text_has_no_category(string text) => IcdClassification.CategoryOf(text).ShouldBeNull();

    [Fact]
    public void Blocks_do_not_overlap_and_sit_inside_their_chapter()
    {
        var blocks = IcdClassification.Blocks.OrderBy(b => b.Start, StringComparer.Ordinal).ToList();
        for (var i = 1; i < blocks.Count; i++)
        {
            string.CompareOrdinal(blocks[i].Start, blocks[i - 1].End).ShouldBeGreaterThan(0, $"{blocks[i - 1].Range} overlaps {blocks[i].Range}");
        }

        foreach (var block in IcdClassification.Blocks)
        {
            IcdClassification.ChapterOf(block.Start).ShouldNotBeNull().Number.ShouldBe(IcdClassification.ChapterOf(block.End)!.Number, block.Range);
        }
    }

    [Fact]
    public void Search_text_combines_code_title_and_keywords()
    {
        var code = new IcdCode { Code = "I10", Title = "Essential (primary) hypertension", Keywords = "HTN" };
        code.RefreshSearchText();

        code.SearchText.ShouldBe("i10 essential (primary) hypertension htn");
    }

    [Fact]
    public void Search_text_includes_the_who_inclusion_terms()
    {
        var code = new IcdCode { Code = "A00.0", Title = "Cholera due to Vibrio cholerae 01, biovar cholerae", IncludedTerms = "Classical cholera" };
        code.RefreshSearchText();

        code.SearchText.ShouldEndWith("classical cholera");
    }

    [Fact]
    public void Inclusion_terms_find_a_code_but_rank_below_title_and_shorthand_matches()
    {
        CodeCandidate[] catalogue =
        [
            new("A00.0", "Cholera due to Vibrio cholerae 01, biovar cholerae", null, true, "Classical cholera"),
            new("A00.9", "Cholera, unspecified", "classical", true),
            new("B99", "Other and unspecified infectious diseases", null, true),
        ];

        IcdSearchRanker.Rank(catalogue, c => c, "classical", 10).Select(c => c.Code).ShouldBe(["A00.9", "A00.0"]);
        IcdSearchRanker.Score(catalogue[0], "classical cholera").ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Words_late_in_a_long_title_still_count_as_title_words()
    {
        var candidate = new CodeCandidate("W00.09", "Fall on same level involving ice and snow, home, during unspecified activity", null, true);

        IcdSearchRanker.Score(candidate, "unspecified activity").ShouldBeGreaterThanOrEqualTo(500);
    }
}
