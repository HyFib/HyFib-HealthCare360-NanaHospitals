using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services.Catalogue;

namespace HyFib.HealthCare360.Modules.Clinical.Tests.Catalogue;

public sealed class ClamlReaderTests
{
    private static readonly Lazy<ClamlClassification> Sample = new(() => ClamlReader.Read(CatalogueSamples.Stream(CatalogueSamples.Claml), CatalogueFileLimits.ForSamples));

    private static ClassificationCode Code(string code) => Sample.Value.Codes.Single(c => c.Code == code);

    /// <summary>A category and every code under it, in code order.</summary>
    private static List<string> Family(string category) =>
        [.. Sample.Value.Codes.Select(c => c.Code).Where(c => c.StartsWith(category, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    [Fact]
    public void Reads_the_title_even_though_the_file_names_an_external_dtd()
    {
        var title = Sample.Value.Title;

        title.Name.ShouldBe("ICD-10");
        title.Version.ShouldBe("2019");
        title.Text.ShouldStartWith("International Statistical Classification of Diseases");
    }

    [Fact]
    public void Reads_categories_and_their_subdivisions_with_preferred_titles()
    {
        Code("A00").Title.ShouldBe("Cholera");
        Code("A00.0").Title.ShouldBe("Cholera due to Vibrio cholerae 01, biovar cholerae");
        Code("A00.9").Title.ShouldBe("Cholera, unspecified", "the English label is chosen over another language");
        Sample.Value.CategoriesInFile.ShouldBe(18);
        Sample.Value.Codes.Count.ShouldBe(42);
    }

    [Fact]
    public void Files_each_code_under_its_chapter_and_nearest_block()
    {
        Code("A00.0").ShouldSatisfyAllConditions(c => c.Chapter.ShouldBe("I"), c => c.Block.ShouldBe("A00-A09"));
        Code("C00").ShouldSatisfyAllConditions(c => c.Chapter.ShouldBe("II"), c => c.Block.ShouldBe("C00-C14"));
        Code("E11.9").ShouldSatisfyAllConditions(c => c.Chapter.ShouldBe("IV"), c => c.Block.ShouldBe("E10-E14"));
        Code("W00.00").ShouldSatisfyAllConditions(c => c.Chapter.ShouldBe("XX"), c => c.Block.ShouldBe("W00-X59"));
    }

    [Fact]
    public void Reads_chapters_and_blocks_including_a_block_of_blocks()
    {
        Sample.Value.Chapters.Select(c => c.Number).ShouldBe(["I", "II", "IV", "V", "VI", "XIII", "XX"]);
        var neoplasms = Sample.Value.Chapters.Single(c => c.Number == "II");
        neoplasms.ShouldSatisfyAllConditions(c => c.Start.ShouldBe("C00"), c => c.End.ShouldBe("C97"), c => c.Title.ShouldBe("Neoplasms"));

        var outer = Sample.Value.Blocks.Single(b => b.Code == "C00-C97");
        var inner = Sample.Value.Blocks.Single(b => b.Code == "C00-C14");
        outer.ShouldSatisfyAllConditions(b => b.Parent.ShouldBeNull(), b => b.HoldsCategories.ShouldBeFalse(), b => b.Chapter.ShouldBe("II"));
        inner.ShouldSatisfyAllConditions(b => b.Parent.ShouldBe("C00-C97"), b => b.HoldsCategories.ShouldBeTrue(), b => b.Title.ShouldBe("Lip, oral cavity and pharynx"));
        Sample.Value.Blocks.Count.ShouldBe(11);
    }

    [Fact]
    public void Inclusion_terms_become_search_terms_and_exclusions_do_not()
    {
        Code("A00.0").IncludedTerms.ShouldBe("Classical cholera");
        Code("A17.0").IncludedTerms.ShouldBe("Tuberculosis of meninges (cerebral)(spinal)", "fragments are joined with a space");
        Code("E10").IncludedTerms.ShouldBe("Juvenile-onset diabetes (mellitus)");
        Code("A00").IncludedTerms.ShouldBeNull();
    }

    [Fact]
    public void Marks_dagger_and_asterisk_codes_and_shows_references_as_printed()
    {
        Code("A17.0").Usage.ShouldBe(IcdUsage.Dagger);
        Code("A17.0").Title.ShouldBe("Tuberculous meningitis (G01*)");
        Code("G01").Usage.ShouldBe(IcdUsage.Asterisk);
        Code("A17").Usage.ShouldBeNull();
    }

    [Fact]
    public void A_shared_fourth_character_list_makes_the_codes_and_their_titles()
    {
        Family("E10").ShouldBe(["E10", "E10.0", "E10.2", "E10.9"]);
        Code("E10.0").ShouldSatisfyAllConditions(
            c => c.Title.ShouldBe("Type 1 diabetes mellitus, with coma"),
            c => c.FromModifier.ShouldBeTrue(),
            c => c.IsBillable.ShouldBeTrue());
        Code("E11.9").Title.ShouldBe("Type 2 diabetes mellitus, without complications");
        Code("E10").IsBillable.ShouldBeFalse("a category with fourth-character subdivisions is not codable");
    }

    [Fact]
    public void A_made_code_takes_the_modifier_class_usage_and_inclusions()
    {
        Code("E10.2").ShouldSatisfyAllConditions(
            c => c.Usage.ShouldBe(IcdUsage.Dagger),
            c => c.IncludedTerms.ShouldBe("Diabetic nephropathy (N08.3*)"));
        Code("E10.0").Usage.ShouldBeNull();
    }

    [Fact]
    public void All_false_limits_a_modifier_to_its_valid_classes() =>
        Family("E14").ShouldBe(["E14", "E14.0", "E14.9"]);

    [Fact]
    public void Nested_modifier_classes_add_a_fifth_character_under_the_fourth()
    {
        Family("F10")
            .ShouldBe(["F10", "F10.2", "F10.20", "F10.21", "F10.9"]);
        Code("F10.20").Title.ShouldBe("Mental and behavioural disorders due to use of alcohol, dependence syndrome, currently abstinent");
        Code("F10").IsBillable.ShouldBeFalse();
        Code("F10.2").IsBillable.ShouldBeTrue("fifth characters are for optional use, so the fourth-character code stays codable");
        Code("F10.21").IsBillable.ShouldBeTrue();
    }

    [Fact]
    public void A_modifier_on_a_chapter_reaches_the_codes_at_the_bottom_of_its_hierarchy()
    {
        Family("M54").ShouldBe(["M54", "M54.5", "M54.50", "M54.56"]);
        Code("M54.56").Title.ShouldBe("Low back pain, lumbar region");
        Code("M54").IsBillable.ShouldBeFalse();
        Code("M54.5").IsBillable.ShouldBeTrue("the site is an optional fifth character");
    }

    [Fact]
    public void A_site_code_on_a_category_without_subdivisions_takes_the_fourth_place_and_stays_optional()
    {
        Family("M45").ShouldBe(["M45", "M45.0", "M45.6"]);
        Code("M45.6").Title.ShouldBe("Ankylosing spondylitis, lumbar region");
        Code("M45").IsBillable.ShouldBeTrue();
    }

    [Fact]
    public void Exclude_modifier_stops_an_inherited_modifier()
    {
        Family("M99").ShouldBe(["M99"]);
        Code("M99").IsBillable.ShouldBeTrue();

        // Y06 excludes the place of occurrence but keeps the activity, which its subdivision takes as the fifth character.
        Family("Y06").ShouldBe(["Y06", "Y06.0", "Y06.00", "Y06.09"]);
    }

    [Fact]
    public void Two_modifiers_apply_one_after_the_other_by_position()
    {
        Family("W00")
            .ShouldBe(["W00", "W00.0", "W00.00", "W00.09", "W00.9", "W00.90", "W00.99"]);
        Code("W00.09").Title.ShouldBe("Fall on same level involving ice and snow, home, during unspecified activity");
        Code("W00").IsBillable.ShouldBeFalse("the place of occurrence is a fourth character");
        Code("W00.9").IsBillable.ShouldBeTrue("the activity is an optional fifth character");
    }

    [Theory]
    [InlineData("E11", ".0", "E11.0")]
    [InlineData("M45", "6", "M45.6")]
    [InlineData("M54.1", "6", "M54.16")]
    [InlineData("F10", ".20", "F10.20")]
    [InlineData("F10.2", "0", "F10.20")]
    public void Characters_are_added_after_a_dot_once_there_are_three(string code, string characters, string expected) =>
        ClamlReader.Compose(code, characters).ShouldBe(expected);

    [Theory]
    [InlineData("With coma", "Type 2 diabetes mellitus, with coma")]
    [InlineData("HIV disease", "Type 2 diabetes mellitus, HIV disease")]
    [InlineData(null, "Type 2 diabetes mellitus")]
    public void Made_titles_read_as_one_phrase(string? characters, string expected) =>
        ClamlReader.ComposeTitle("Type 2 diabetes mellitus", characters).ShouldBe(expected);

    [Fact]
    public void An_entity_from_outside_the_file_is_never_read()
    {
        const string canary = "HC360-CANARY-7F3A";
        var local = Path.Combine(Path.GetTempPath(), $"hc360-claml-{Guid.NewGuid():N}.txt");
        File.WriteAllText(local, canary);
        try
        {
            var xml = $"""
                <?xml version="1.0"?>
                <!DOCTYPE ClaML [ <!ENTITY ext SYSTEM "file:///{local.Replace('\\', '/')}"> ]>
                <ClaML><Title name="ICD-10" version="&ext;"/></ClaML>
                """;

            var error = Should.Throw<CatalogueFileException>(() => ClamlReader.Read(CatalogueSamples.Stream(xml), CatalogueFileLimits.ForSamples));

            error.Message.ShouldStartWith("The file is not valid XML");
            error.Message.ShouldNotContain(canary);
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public void Entity_expansion_bombs_are_refused()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE ClaML [
              <!ENTITY a "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa">
              <!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">
              <!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">
              <!ENTITY d "&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;">
            ]>
            <ClaML><Title name="&d;"/></ClaML>
            """;

        Should.Throw<CatalogueFileException>(() => ClamlReader.Read(CatalogueSamples.Stream(xml), CatalogueFileLimits.ForSamples));
    }

    [Fact]
    public void Predefined_entities_and_character_references_still_read() =>
        ClamlReader.Read(CatalogueSamples.Stream(CatalogueSamples.Claml.Replace("Cholera, unspecified", "Cholera &amp; &#x63;holera, unspecified", StringComparison.Ordinal)),
                CatalogueFileLimits.ForSamples)
            .Codes.Single(c => c.Code == "A00.9").Title.ShouldBe("Cholera & cholera, unspecified");

    [Fact]
    public void A_real_file_full_of_ampersands_is_not_taken_for_an_entity_bomb()
    {
        var many = string.Concat(Enumerable.Repeat("A &amp; B; ", 3000));

        var result = ClamlReader.Read(CatalogueSamples.Stream(CatalogueSamples.Claml.Replace("Classical cholera", many, StringComparison.Ordinal)),
            CatalogueFileLimits.ForSamples);

        result.Codes.Single(c => c.Code == "A00.0").IncludedTerms!.ShouldStartWith("A & B; A & B;");
    }

    [Fact]
    public void A_file_that_is_not_claml_is_refused_with_a_reason()
    {
        var error = Should.Throw<CatalogueFileException>(() => ClamlReader.Read(CatalogueSamples.Stream("<ICD10PCS.tabular/>"), CatalogueFileLimits.ForSamples));

        error.Message.ShouldContain("not a ClaML file");
    }

    [Fact]
    public void Broken_xml_is_refused() =>
        Should.Throw<CatalogueFileException>(() => ClamlReader.Read(CatalogueSamples.Stream("<ClaML><Class code=\"A00\" kind=\"category\">"), CatalogueFileLimits.ForSamples))
            .Message.ShouldStartWith("The file is not valid XML");

    [Fact]
    public void A_partial_file_cannot_withdraw_the_catalogue()
    {
        var error = Should.Throw<CatalogueFileException>(() => ClamlReader.Read(CatalogueSamples.Stream(CatalogueSamples.Claml), new CatalogueFileLimits()));

        error.Message.ShouldContain("only 18 categories");
    }

    [Fact]
    public void The_class_limit_stops_an_oversized_file() =>
        Should.Throw<CatalogueFileException>(() =>
                ClamlReader.Read(CatalogueSamples.Stream(CatalogueSamples.Claml), CatalogueFileLimits.ForSamples with { MaxClasses = 10 }))
            .Message.ShouldContain("more than 10 classes");

    [Fact]
    public void The_code_limit_stops_runaway_modifiers() =>
        Should.Throw<CatalogueFileException>(() =>
                ClamlReader.Read(CatalogueSamples.Stream(CatalogueSamples.Claml), CatalogueFileLimits.ForSamples with { MaxCodes = 20 }))
            .Message.ShouldContain("more than 20 codes");

    [Fact]
    public void Another_classifications_codes_are_refused()
    {
        const string xml = """
            <ClaML>
              <Class code="01" kind="chapter"><Rubric kind="preferred"><Label xml:lang="en">Certain infectious or parasitic diseases</Label></Rubric></Class>
              <Class code="1A00" kind="category"><SuperClass code="01"/><Rubric kind="preferred"><Label xml:lang="en">Cholera</Label></Rubric></Class>
              <Class code="1A01" kind="category"><SuperClass code="01"/></Class>
              <Class code="1A02" kind="category"><SuperClass code="01"/></Class>
              <Class code="1A03" kind="category"><SuperClass code="01"/></Class>
              <Class code="1A04" kind="category"><SuperClass code="01"/></Class>
              <Class code="1A05" kind="category"><SuperClass code="01"/></Class>
              <Class code="1A06" kind="category"><SuperClass code="01"/></Class>
              <Class code="1A07" kind="category"><SuperClass code="01"/></Class>
              <Class code="1A08" kind="category"><SuperClass code="01"/></Class>
              <Class code="1A09" kind="category"><SuperClass code="01"/></Class>
              <Class code="1A0Y" kind="category"><SuperClass code="01"/></Class>
            </ClaML>
            """;

        Should.Throw<CatalogueFileException>(() => ClamlReader.Read(CatalogueSamples.Stream(xml), CatalogueFileLimits.ForSamples))
            .Message.ShouldContain("not ICD-10 codes");
    }

    [Fact]
    public void A_modifier_that_is_used_but_not_defined_is_reported()
    {
        var xml = CatalogueSamples.Claml.Replace("<ModifiedBy code=\"S05F10_4\"/>", "<ModifiedBy code=\"S99MISSING\"/>", StringComparison.Ordinal);

        var result = ClamlReader.Read(CatalogueSamples.Stream(xml), CatalogueFileLimits.ForSamples);

        result.Warnings.ShouldContain(w => w.Contains("S99MISSING", StringComparison.Ordinal));
        result.Codes.Single(c => c.Code == "F10").IsBillable.ShouldBeTrue("without its subdivisions F10 is a code at the bottom of the hierarchy");
    }
}
