using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services.Catalogue;

namespace HyFib.HealthCare360.Modules.Clinical.Tests.Catalogue;

/// <summary>What an import does to the codes already in the catalogue (the rules, without the database).</summary>
public sealed class CatalogueChangesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static ClassificationCode FileCode(string code, string title, bool billable = true, IcdUsage? usage = null, string? included = null) =>
        new(code, title, "IV", "E10-E14", billable, usage, included, FromModifier: false);

    private static IcdCode Stored(string code, string title, CodeSource source, string? keywords = null)
    {
        var row = new IcdCode { Code = code, Title = title, Release = CodeReleases.Icd10, Chapter = "IV", Block = "E10-E14", Source = source, Keywords = keywords };
        row.RefreshSearchText();
        return row;
    }

    [Fact]
    public void A_new_code_is_added_from_the_file()
    {
        var added = new List<IcdCode>();

        var outcome = Icd10Changes.Apply([], [FileCode("E11.9", "Type 2 diabetes mellitus, without complications", included: "Maturity-onset diabetes")], Now, added.Add, dryRun: false);

        outcome.Added.ShouldBe(1);
        var code = added.ShouldHaveSingleItem();
        code.ShouldSatisfyAllConditions(
            c => c.Source.ShouldBe(CodeSource.Import),
            c => c.IsActive.ShouldBeTrue(),
            c => c.Release.ShouldBe(CodeReleases.Icd10),
            c => c.SearchText.ShouldContain("maturity-onset diabetes"));
    }

    [Fact]
    public void A_starter_code_takes_the_file_title_and_keeps_its_search_words()
    {
        var seed = Stored("E11", "Non-insulin-dependent diabetes mellitus", CodeSource.Seed, "type 2 diabetes T2DM NIDDM DM");

        var outcome = Icd10Changes.Apply([seed], [FileCode("E11", "Type 2 diabetes mellitus", billable: false)], Now, _ => { }, dryRun: false);

        outcome.Updated.ShouldBe(1);
        seed.Title.ShouldBe("Type 2 diabetes mellitus");
        seed.IsBillable.ShouldBeFalse();
        seed.Keywords.ShouldBe("type 2 diabetes T2DM NIDDM DM");
        seed.Source.ShouldBe(CodeSource.Import);
        seed.SearchText.ShouldContain("t2dm");
    }

    [Fact]
    public void An_unchanged_code_is_counted_but_not_touched()
    {
        var row = Stored("E11", "Type 2 diabetes mellitus", CodeSource.Import);
        row.IsBillable = false;

        var outcome = Icd10Changes.Apply([row], [FileCode("E11", "Type 2 diabetes mellitus", billable: false)], Now, _ => { }, dryRun: false);

        outcome.Unchanged.ShouldBe(1);
        outcome.Updated.ShouldBe(0);
    }

    [Fact]
    public void A_code_the_file_no_longer_has_is_withdrawn_not_deleted()
    {
        var gone = Stored("E15", "Nondiabetic hypoglycaemic coma", CodeSource.Import);

        var outcome = Icd10Changes.Apply([gone], [FileCode("E11", "Type 2 diabetes mellitus")], Now, _ => { }, dryRun: false);

        outcome.Withdrawn.ShouldBe(1);
        gone.IsActive.ShouldBeFalse();
        gone.WithdrawnAt.ShouldBe(Now);
        gone.IsDeleted.ShouldBeFalse();
    }

    [Fact]
    public void A_withdrawn_code_comes_back_when_a_later_file_has_it()
    {
        var withdrawn = Stored("E15", "Nondiabetic hypoglycaemic coma", CodeSource.Import);
        withdrawn.IsActive = false;
        withdrawn.WithdrawnAt = Now.AddYears(-1);

        var outcome = Icd10Changes.Apply([withdrawn], [FileCode("E15", "Nondiabetic hypoglycaemic coma")], Now, _ => { }, dryRun: false);

        outcome.Reinstated.ShouldBe(1);
        withdrawn.IsActive.ShouldBeTrue();
        withdrawn.WithdrawnAt.ShouldBeNull();
    }

    [Fact]
    public void A_code_an_administrator_switched_off_stays_off()
    {
        var off = Stored("R69", "Illness, unspecified", CodeSource.Seed);
        off.IsActive = false;

        var outcome = Icd10Changes.Apply([off], [FileCode("R69", "Illness, unspecified")], Now, _ => { }, dryRun: false);

        outcome.Unchanged.ShouldBe(1);
        off.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void A_code_switched_off_stays_off_when_one_file_drops_it_and_the_next_has_it_again()
    {
        var off = Stored("E15", "Nondiabetic hypoglycaemic coma", CodeSource.Import);
        off.IsActive = false;

        var first = Icd10Changes.Apply([off], [FileCode("E11", "Type 2 diabetes mellitus")], Now, _ => { }, false);
        var second = Icd10Changes.Apply([off], [FileCode("E15", off.Title)], Now.AddYears(1), _ => { }, false);

        first.Withdrawn.ShouldBe(0);
        second.Reinstated.ShouldBe(0);
        off.IsActive.ShouldBeFalse();
        off.WithdrawnAt.ShouldBeNull();
    }

    [Fact]
    public void A_procedure_code_switched_off_stays_off_when_one_file_drops_it_and_the_next_has_it_again()
    {
        var off = new ProcedureCode
        {
            Code = "0DTJ0ZZ",
            Title = "Resection of Appendix, Open Approach",
            Source = CodeSource.Import,
            IsActive = false,
        };
        var used = new ProcedureCode
        {
            Code = "0DTJ4ZZ",
            Title = "Resection of Appendix, Percutaneous Endoscopic Approach",
            Source = CodeSource.Import,
        };
        PcsCode[] without = [new("BW03ZZZ", "Plain Radiography of Chest", "Imaging")];
        PcsCode[] with = [new("0DTJ0ZZ", off.Title, "Resection"), new("0DTJ4ZZ", used.Title, "Resection")];

        var first = PcsChanges.Apply([off, used], without, Now, _ => { }, dryRun: false);
        var second = PcsChanges.Apply([off, used], with, Now.AddYears(1), _ => { }, dryRun: false);

        first.Withdrawn.ShouldBe(1, "only the code in use is withdrawn");
        second.Reinstated.ShouldBe(1);
        off.IsActive.ShouldBeFalse();
        off.WithdrawnAt.ShouldBeNull();
        used.IsActive.ShouldBeTrue();
        used.WithdrawnAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(CodeSource.Manual)]
    [InlineData(CodeSource.Online)]
    public void Codes_added_by_hand_or_saved_online_are_left_alone(CodeSource source)
    {
        var inFile = Stored("E11.9", "Local wording", source);
        var notInFile = Stored("E99", "Hospital code", source);

        var outcome = Icd10Changes.Apply([inFile, notInFile], [FileCode("E11.9", "Type 2 diabetes mellitus, without complications")], Now, _ => { }, dryRun: false);

        outcome.LeftAlone.ShouldBe(2);
        inFile.Title.ShouldBe("Local wording");
        notInFile.IsActive.ShouldBeTrue();
        notInFile.WithdrawnAt.ShouldBeNull();
    }

    [Fact]
    public void A_dry_run_counts_without_adding()
    {
        var added = new List<IcdCode>();

        var outcome = Icd10Changes.Apply([], [FileCode("E11", "Type 2 diabetes mellitus"), FileCode("E11.9", "Type 2 diabetes mellitus, without complications")],
            Now, added.Add, dryRun: true);

        outcome.Added.ShouldBe(2);
        added.ShouldBeEmpty();
    }

    [Fact]
    public void Dagger_and_asterisk_marks_and_inclusion_terms_are_kept_up_to_date()
    {
        var row = Stored("E10.2", "Type 1 diabetes mellitus, with kidney complications", CodeSource.Import);

        Icd10Changes.Apply([row], [FileCode("E10.2", row.Title, usage: IcdUsage.Dagger, included: "Diabetic nephropathy (N08.3*)")], Now, _ => { }, dryRun: false)
            .Updated.ShouldBe(1);

        row.Usage.ShouldBe(IcdUsage.Dagger);
        row.IncludedTerms.ShouldBe("Diabetic nephropathy (N08.3*)");
    }

    [Fact]
    public void Procedure_codes_from_the_starter_list_keep_their_everyday_titles()
    {
        var seed = new ProcedureCode
        {
            Code = "0DTJ4ZZ",
            Title = "Laparoscopic appendicectomy (resection of appendix, percutaneous endoscopic)",
            Category = "General surgery",
            Source = CodeSource.Seed,
        };
        var imported = new ProcedureCode { Code = "0DTJ0ZZ", Title = "Old title", Category = "Old", Source = CodeSource.Import };
        var added = new List<ProcedureCode>();

        var outcome = PcsChanges.Apply([seed, imported],
        [
            new PcsCode("0DTJ4ZZ", "Resection of Appendix, Percutaneous Endoscopic Approach", "Gastrointestinal System, Resection"),
            new PcsCode("0DTJ0ZZ", "Resection of Appendix, Open Approach", "Gastrointestinal System, Resection"),
            new PcsCode("BW03ZZZ", "Plain Radiography of Chest", "Imaging"),
        ], Now, added.Add, dryRun: false);

        outcome.ShouldSatisfyAllConditions(o => o.Added.ShouldBe(1), o => o.Updated.ShouldBe(1), o => o.Unchanged.ShouldBe(1));
        seed.Title.ShouldStartWith("Laparoscopic appendicectomy");
        seed.Category.ShouldBe("General surgery");
        imported.Title.ShouldBe("Resection of Appendix, Open Approach");
        added.ShouldHaveSingleItem().Source.ShouldBe(CodeSource.Import);
    }

    [Fact]
    public void Procedure_codes_the_file_no_longer_has_are_withdrawn()
    {
        var seed = new ProcedureCode { Code = "0W9G3ZZ", Title = "Abdominal paracentesis", Source = CodeSource.Seed };
        var manual = new ProcedureCode { Code = "LOCAL01", Title = "Hospital procedure", Source = CodeSource.Manual };

        var outcome = PcsChanges.Apply([seed, manual], [new PcsCode("BW03ZZZ", "Plain Radiography of Chest", "Imaging")], Now, _ => { }, dryRun: false);

        outcome.Withdrawn.ShouldBe(1);
        outcome.LeftAlone.ShouldBe(1);
        seed.IsActive.ShouldBeFalse();
        manual.IsActive.ShouldBeTrue();
    }
}

public sealed class IcdClassificationProviderTests
{
    [Fact]
    public void Without_an_import_the_built_in_chapters_and_blocks_are_used() =>
        IcdClassificationProvider.From([]).ShouldBeSameAs(IcdClassificationSet.BuiltIn);

    [Fact]
    public void After_an_import_the_files_chapters_and_category_blocks_are_used()
    {
        IcdClassificationEntry Entry(IcdClassificationKind kind, string code, string start, string end, string title, bool holds = false) =>
            new() { Release = CodeReleases.Icd10, Kind = kind, Code = code, Start = start, End = end, Title = title, ChapterNumber = "II", HoldsCategories = holds };

        var set = IcdClassificationProvider.From(
        [
            Entry(IcdClassificationKind.Chapter, "II", "C00", "D48", "Neoplasms"),
            Entry(IcdClassificationKind.Block, "C00-C97", "C00", "C97", "Malignant neoplasms"),
            Entry(IcdClassificationKind.Block, "C00-C14", "C00", "C14", "Malignant neoplasms of lip, oral cavity and pharynx (from the file)", holds: true),
        ]);

        set.FromFile.ShouldBeTrue();
        set.ChapterOf("C01").ShouldNotBeNull().Number.ShouldBe("II");
        set.BlockOf("C01").ShouldNotBeNull().Title.ShouldBe("Malignant neoplasms of lip, oral cavity and pharynx (from the file)");
        set.Blocks.ShouldHaveSingleItem("a block of blocks is not listed");
        set.BlockOf("E11.9").ShouldBeNull();
    }
}

public sealed class CodeReleaseTests
{
    [Theory]
    [InlineData("ICD-10 2019", CodeReleases.Icd10)]
    [InlineData("ICD-10", CodeReleases.Icd10)]
    [InlineData("10", CodeReleases.Icd10)]
    [InlineData("ICD-11 MMS", CodeReleases.Icd11)]
    [InlineData("ICD-11 MMS 2025-01", CodeReleases.Icd11)]
    [InlineData("11", CodeReleases.Icd11)]
    [InlineData("ICD-10-PCS", CodeReleases.Icd10Pcs)]
    [InlineData("ICD-10-CM", null)]
    [InlineData("SNOMED", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Release_labels_are_normalised(string? label, string? expected) => CodeReleases.Normalize(label).ShouldBe(expected);

    [Theory]
    [InlineData("E11.9", CodeReleases.Icd10)]
    [InlineData("A00", CodeReleases.Icd10)]
    [InlineData("M54.16", CodeReleases.Icd10)]
    [InlineData("1A00", CodeReleases.Icd11)]
    [InlineData("5A11", CodeReleases.Icd11)]
    [InlineData("BA00.Z", CodeReleases.Icd11)]
    [InlineData("1A00&XN4F", CodeReleases.Icd11)]
    [InlineData("0DTJ4ZZ", null)]
    [InlineData("LOCAL", null)]
    [InlineData(null, null)]
    public void A_diagnosis_codes_release_follows_from_its_form(string? code, string? expected) => CodeReleases.OfDiagnosisCode(code).ShouldBe(expected);
}
