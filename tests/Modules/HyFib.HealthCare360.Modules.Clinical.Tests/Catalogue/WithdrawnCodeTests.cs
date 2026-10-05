using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Tests.Catalogue;

/// <summary>
/// A code an import withdrew (or MRD switched off) stays on the records that already have it: they can be saved again,
/// but the code cannot be added anew.
/// </summary>
public sealed class WithdrawnCodeTests
{
    private static readonly IcdCode Diabetes = new()
    {
        Code = "E11.9",
        Title = "Type 2 diabetes mellitus, without complications",
        Release = CodeReleases.Icd10,
        IsBillable = true,
    };

    private static readonly IcdCode Withdrawn = new()
    {
        Code = "E15",
        Title = "Nondiabetic hypoglycaemic coma",
        Release = CodeReleases.Icd10,
        IsBillable = true,
        IsActive = false,
        WithdrawnAt = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero),
    };

    private static readonly ProcedureCode OldProcedure = new()
    {
        Code = "0DTJ0ZZ",
        Title = "Resection of Appendix, Open Approach",
        System = CodeReleases.Icd10Pcs,
        IsActive = false,
    };

    private static readonly CodingLineRequest[] Lines =
    [
        new(CodingEntryKind.Diagnosis, "E11.9", true),
        new(CodingEntryKind.Diagnosis, "e15", false),
        new(CodingEntryKind.Procedure, "0DTJ0ZZ", false),
    ];

    [Fact]
    public void A_coding_that_already_has_a_withdrawn_code_can_be_saved_again()
    {
        HashSet<(CodingEntryKind, string)> recorded =
        [
            (CodingEntryKind.Diagnosis, "E11.9"),
            (CodingEntryKind.Diagnosis, "E15"),
            (CodingEntryKind.Procedure, "0DTJ0ZZ"),
        ];

        var lines = CodingService.ResolveLines(Lines, [Diabetes, Withdrawn], [OldProcedure], recorded);

        lines.IsSuccess.ShouldBeTrue();
        lines.Value.Select(l => l.Code).ShouldBe(["E11.9", "E15", "0DTJ0ZZ"]);
    }

    [Fact]
    public void A_withdrawn_code_cannot_be_added_to_a_coding()
    {
        HashSet<(CodingEntryKind, string)> recorded =
            [(CodingEntryKind.Diagnosis, "E11.9"), (CodingEntryKind.Procedure, "0DTJ0ZZ")];

        var lines = CodingService.ResolveLines(Lines, [Diabetes, Withdrawn], [OldProcedure], recorded);

        lines.Error!.Fields!["entries"].ShouldHaveSingleItem().ShouldContain("E15 is no longer in use");
    }

    [Fact]
    public void A_withdrawn_procedure_code_cannot_be_added_to_a_coding()
    {
        HashSet<(CodingEntryKind, string)> recorded = [(CodingEntryKind.Diagnosis, "E15")];

        var lines = CodingService.ResolveLines(Lines, [Diabetes, Withdrawn], [OldProcedure], recorded);

        lines.Error!.Fields!["entries"].ShouldHaveSingleItem()
            .ShouldContain("0DTJ0ZZ is no longer in use");
    }

    [Fact]
    public void A_discharge_summary_that_already_has_a_withdrawn_code_can_be_saved_again()
    {
        HashSet<string> inUse = ["E11.9"];
        HashSet<string> inCatalogue = ["E11.9", "E15"];

        DischargeSummaryService.CheckCodes(["E11.9", " e15 "], inUse, inCatalogue, ["E11.9", "E15"])
            .IsSuccess.ShouldBeTrue();
        DischargeSummaryService.CheckCodes(["E11.9", "E15"], inUse, inCatalogue, ["E11.9"])
            .Error!.Fields!["diagnosisCodes"].ShouldHaveSingleItem()
            .ShouldContain("No longer in use in the ICD catalogue: E15.");
        DischargeSummaryService.CheckCodes(["X99.9"], inUse, inCatalogue, [])
            .Error!.Fields!["diagnosisCodes"].ShouldHaveSingleItem()
            .ShouldContain("Unknown ICD code(s): X99.9.");
    }
}
