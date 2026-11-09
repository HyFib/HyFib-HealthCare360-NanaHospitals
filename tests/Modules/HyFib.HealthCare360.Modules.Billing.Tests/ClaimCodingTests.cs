using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>The codes a new insurance claim takes where the desk typed none (MB-03).</summary>
public sealed class ClaimCodingTests
{
    private const string Excision = "0HBU0ZZ Excision of breast lump, left (excision of left breast, open)";

    private static readonly PreAuthorization PreAuth = new()
    {
        Number = "NHPA26000001",
        DiagnosisCodes = "N63 Unspecified lump in breast",
        Procedures =
        [
            new() { LineNumber = 1, System = PreAuthProcedureSystem.Icd10Pcs, Code = "0HBU0ZZ", Name = "Excision of breast lump", Quantity = 1, Amount = 30_000m },
        ],
    };

    [Fact]
    public void Takes_the_coding_medical_records_verified_over_the_pre_authorisations()
    {
        var verified = new VerifiedClaimCoding("D24 Benign neoplasm of breast", Excision, TestData.Now);

        ClaimService.InitialCoding(verified, PreAuth).ShouldBe(("D24 Benign neoplasm of breast", Excision));
    }

    [Fact]
    public void Takes_no_procedures_when_medical_records_coded_none()
    {
        // The excision the pre-authorisation asked for was not done.
        var verified = new VerifiedClaimCoding("N63 Unspecified lump in breast", null, TestData.Now);

        ClaimService.InitialCoding(verified, PreAuth).ShouldBe(("N63 Unspecified lump in breast", (string?)null));
    }

    [Fact]
    public void Takes_the_pre_authorisations_codes_until_a_coding_is_verified() =>
        ClaimService.InitialCoding(null, PreAuth).ShouldBe((PreAuth.DiagnosisCodes, "0HBU0ZZ Excision of breast lump"));

    [Fact]
    public void Takes_the_coded_procedures_in_the_desks_order()
    {
        var preAuth = new PreAuthorization
        {
            Procedures =
            [
                new() { LineNumber = 2, System = PreAuthProcedureSystem.Icd10Pcs, Code = "0FT44ZZ", Name = "Laparoscopic cholecystectomy" },
                new() { LineNumber = 1, System = PreAuthProcedureSystem.Hbp, Code = "SG039A", Name = "Cholecystectomy" },
            ],
            LegacyProcedureText = "typed before coding",
        };

        ClaimService.InitialCoding(null, preAuth).Procedures.ShouldBe("SG039A Cholecystectomy; 0FT44ZZ Laparoscopic cholecystectomy");
        preAuth.ProcedureText(16).ShouldBe("SG039A; 0FT44ZZ");
    }

    [Fact]
    public void Takes_the_text_typed_before_procedures_were_coded()
    {
        var preAuth = new PreAuthorization { LegacyProcedureText = "Lap chole planned" };

        ClaimService.InitialCoding(null, preAuth).Procedures.ShouldBe("Lap chole planned");
    }

    [Fact]
    public void Has_no_codes_without_a_verified_coding_or_a_pre_authorisation() =>
        ClaimService.InitialCoding(null, null).ShouldBe(((string?)null, (string?)null));
}
