using HyFib.HealthCare360.Integrations.Icd;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Icd;

public sealed class IcdResponseParserTests
{
    /// <summary>ICD-API v2 flat search response (trimmed) with highlighting and a code-less block entity.</summary>
    internal const string SearchJson = """
        {
          "error": false,
          "errorMessage": null,
          "resultChopped": false,
          "wordSuggestionsChopped": false,
          "guessType": 0,
          "uniqueSearchId": "c7f0e9b3",
          "destinationEntities": [
            { "id": "http://id.who.int/icd/entity/257068234", "title": "<em class='found'>Cholera</em>", "theCode": "1A00", "chapter": "01", "isLeaf": false, "score": 1.0 },
            { "id": "http://id.who.int/icd/entity/1000136433", "title": "<em class='found'>Cholera</em> due to Vibrio cholerae O1, biovar cholerae", "theCode": "1A00.0", "chapter": "01", "isLeaf": true },
            { "id": "http://id.who.int/icd/entity/135352227", "title": "Intestinal infectious diseases", "chapter": "01", "isLeaf": false },
            { "id": "http://id.who.int/icd/entity/1", "title": "Duplicate &amp; repeated", "theCode": "1A00", "chapter": "01" }
          ]
        }
        """;

    [Fact]
    public void Reads_codes_and_clean_titles_from_an_icd11_search()
    {
        var results = IcdResponseParser.ParseIcd11Search(SearchJson, "2025-01", 10);

        results.ShouldBe(
        [
            new IcdSearchResult("1A00", "Cholera", "ICD-11 MMS 2025-01", "01", IsLeaf: false),
            new IcdSearchResult("1A00.0", "Cholera due to Vibrio cholerae O1, biovar cholerae", "ICD-11 MMS 2025-01", "01", IsLeaf: true),
        ]);
    }

    [Fact]
    public void Honours_the_limit() => IcdResponseParser.ParseIcd11Search(SearchJson, "2025-01", 1).ShouldHaveSingleItem().Code.ShouldBe("1A00");

    [Theory]
    [InlineData("")]
    [InlineData("<html>Service unavailable</html>")]
    [InlineData("""{"error":true,"errorMessage":"Bad request"}""")]
    public void Unexpected_answers_yield_no_results(string json) => IcdResponseParser.ParseIcd11Search(json, "2025-01", 10).ShouldBeEmpty();

    [Fact]
    public void Reads_an_icd10_category_and_keeps_only_safe_child_paths()
    {
        const string json = """
            {
              "@context": "http://id.who.int/icd/contexts/contextForTopLevel.json",
              "@id": "http://id.who.int/icd/release/10/2019/E11",
              "parent": ["http://id.who.int/icd/release/10/2019/E10-E14"],
              "child": [
                "http://id.who.int/icd/release/10/2019/E11.0",
                "http://id.who.int/icd/release/10/2019/E11.9",
                "https://evil.example/icd/release/10/2019/../../admin",
                "not a uri"
              ],
              "code": "E11",
              "title": { "@language": "en", "@value": "Type 2 diabetes mellitus" },
              "classKind": "category"
            }
            """;

        var entity = IcdResponseParser.ParseIcd10Entity(json).ShouldNotBeNull();

        entity.Code.ShouldBe("E11");
        entity.Title.ShouldBe("Type 2 diabetes mellitus");
        entity.ClassKind.ShouldBe("category");
        entity.ChildPaths.ShouldBe(["icd/release/10/2019/E11.0", "icd/release/10/2019/E11.9"]);
    }

    [Theory]
    [InlineData("""{"title":{"@value":"No code"}}""")]
    [InlineData("""{"code":"E11"}""")]
    [InlineData("nonsense")]
    public void Incomplete_icd10_entities_are_ignored(string json) => IcdResponseParser.ParseIcd10Entity(json).ShouldBeNull();

    [Theory]
    [InlineData("E11", true)]
    [InlineData("e11.9", true)]
    [InlineData(" I10 ", true)]
    [InlineData("E11.65", true)]
    [InlineData("diabetes", false)]
    [InlineData("1A00", false)]
    [InlineData("E1", false)]
    public void Recognises_icd10_code_shaped_text(string text, bool expected) => IcdResponseParser.IsIcd10Code(text).ShouldBe(expected);

    [Fact]
    public void Removes_highlighting_markup_and_decodes_entities() =>
        IcdResponseParser.CleanTitle("<em class='found'>Crohn</em>&#39;s disease &amp; colitis ").ShouldBe("Crohn's disease & colitis");
}
