using HyFib.HealthCare360.Modules.Interop.Nhcx;
using HyFib.HealthCare360.Testing.Abdm;
using System.Text.Json.Nodes;
using Hl7.Fhir.Serialization;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests.Nhcx;

/// <summary>
/// HC360's NHCX pre-authorisation requests and claims meet the NRCeS ClaimBundle and Claim profiles (ndhm.in 6.5.0), as
/// Firely's validator checks them; the IG's own claim examples pass the same validator, so it is set up as NRCeS meant.
/// </summary>
public sealed class NhcxClaimBundleTests
{
    private const string PreauthorizationExample = "Bundle-ClaimBundle-preauthorization-example-01.json";
    private const string SettlementExample = "Bundle-ClaimBundle-settlement-example-01.json";

    private static readonly Guid PreAuthId = Guid.Parse("01923a4b-0000-7000-8000-0000000ac001");
    private static readonly Guid ClaimId = Guid.Parse("01923a4b-0000-7000-8000-0000000ac002");
    private static readonly DateTimeOffset Admitted = TestData.Now.AddDays(-3);

    [Theory]
    [InlineData(PreauthorizationExample)]
    [InlineData(SettlementExample)]
    public void The_igs_own_claim_bundle_examples_validate(string example)
    {
        var bundle = AbdmProfileValidator.Example(example).ShouldBeOfType<R4.Bundle>();

        var result = AbdmProfileValidator.Validate(bundle, NhcxClaimBundle.ClaimBundleProfile);

        result.Errors.ShouldBeEmpty(result.Describe());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_request_meets_the_claim_bundle_profile(bool claim)
    {
        var bundle = NhcxClaimBundle.Build(Request(claim ? NhcxClaimUse.Claim : NhcxClaimUse.Preauthorization)).Value;

        var result = AbdmProfileValidator.Validate(bundle, NhcxClaimBundle.ClaimBundleProfile);

        result.Errors.ShouldBeEmpty(result.Describe());
        bundle.Entry.Select(e => e.Resource.TypeName).ShouldBe(["Claim", "Patient", "Organization", "Organization", "Coverage", "Practitioner"]);
        bundle.Entry.ShouldAllBe(e => e.FullUrl == "urn:uuid:" + e.Resource.Id);
    }

    [Fact]
    public void A_broken_request_fails_the_profile()
    {
        var bundle = NhcxClaimBundle.Build(Request(NhcxClaimUse.Preauthorization)).Value;
        var claim = (R4.Claim)bundle.Entry[0].Resource;
        claim.Insurer = null;
        claim.Diagnosis[0].Type.Clear();

        var result = AbdmProfileValidator.Validate(bundle, NhcxClaimBundle.ClaimBundleProfile);

        result.Describe().ShouldContain("Missing required member: 'insurer'");
        result.Errors.Count.ShouldBe(2, result.Describe());
        result.Describe().ShouldContain("Missing required member: 'type' at Bundle.entry[0].resource[0].diagnosis[0]");
    }

    [Fact]
    public void A_preauthorization_says_what_the_insurer_is_asked_to_approve()
    {
        var bundle = NhcxClaimBundle.Build(Request(NhcxClaimUse.Preauthorization)).Value;
        var claim = (R4.Claim)bundle.Entry[0].Resource;

        bundle.Type.ShouldBe(R4.Bundle.BundleType.Collection);
        bundle.Meta.Profile.ShouldBe([NhcxClaimBundle.ClaimBundleProfile]);
        claim.Use.ShouldBe(R4.ClaimUseCode.Preauthorization);
        claim.Identifier.Single().Value.ShouldBe("NHPA2600042");
        claim.Type.Coding.Single().Code.ShouldBe("737481003", "an admission is inpatient care");
        claim.Diagnosis.Select(d => d.Diagnosis).OfType<R4.CodeableConcept>().Select(c => c.Coding.Single().Code).ShouldBe(["K35.8", "R10.3"]);
        claim.Diagnosis.ShouldAllBe(d => d.Type.Single().Coding.Single().Code == "148006", "before treatment the diagnosis is preliminary");
        claim.Item.Select(i => i.Net.Value).ShouldBe([35000m, 12000m, 9000m]);
        claim.Total.Value.ShouldBe(56000m, "the total is the sum of the lines");
        claim.Total.Currency.ShouldBe(R4.Money.Currencies.INR);
        claim.Insurance.Single().PreAuthRef.ShouldBeEmpty();
    }

    [Fact]
    public void A_claim_quotes_the_approval_and_states_final_diagnoses()
    {
        var claim = (R4.Claim)NhcxClaimBundle.Build(Request(NhcxClaimUse.Claim)).Value.Entry[0].Resource;

        claim.Use.ShouldBe(R4.ClaimUseCode.Claim);
        claim.Diagnosis.ShouldAllBe(d => d.Type.Single().Coding.Single().Code == "89100005");
        claim.Insurance.Single().PreAuthRef.ShouldBe(["SH/PA/889123"]);
        claim.BillablePeriod.End.ShouldNotBeNull();
    }

    [Fact]
    public void The_patient_is_sent_without_what_the_insurer_does_not_need()
    {
        var bundle = NhcxClaimBundle.Build(Request(NhcxClaimUse.Preauthorization)).Value;
        var patient = bundle.Entry.Select(e => e.Resource).OfType<R4.Patient>().Single();

        patient.Contact.ShouldBeEmpty();
        patient.MaritalStatus.ShouldBeNull();
        patient.Communication.ShouldBeEmpty();
        patient.Identifier.Select(i => i.Value).ShouldContain(AbdmTestData.Patient().Uhid);
    }

    [Fact]
    public void A_policy_held_by_someone_else_names_them_as_holder()
    {
        var request = Request(NhcxClaimUse.Preauthorization) with
        {
            Policy = Policy() with { RelationToHolder = "Wife", PolicyHolderName = "Rahul Kulkarni" },
        };

        var bundle = NhcxClaimBundle.Build(request).Value;
        var coverage = bundle.Entry.Select(e => e.Resource).OfType<R4.Coverage>().Single();

        coverage.Relationship.Coding.Single().Code.ShouldBe("spouse");
        coverage.PolicyHolder.Display.ShouldBe("Rahul Kulkarni");
        coverage.PolicyHolder.Reference.ShouldBeNull();
        coverage.Subscriber.ShouldBeNull();
        coverage.SubscriberId.ShouldBe("SH-77120-02");
        var result = AbdmProfileValidator.Validate(bundle, NhcxClaimBundle.ClaimBundleProfile);
        result.Errors.ShouldBeEmpty(result.Describe());
    }

    [Fact]
    public void The_same_request_built_twice_is_the_same_bundle()
    {
        var serializer = new FhirJsonSerializer();

        var first = serializer.SerializeToString(NhcxClaimBundle.Build(Request(NhcxClaimUse.Claim)).Value);
        var second = serializer.SerializeToString(NhcxClaimBundle.Build(Request(NhcxClaimUse.Claim)).Value);

        second.ShouldBe(first);
    }

    [Fact]
    public void A_claim_fills_every_claim_element_the_igs_example_fills()
    {
        var example = (R4.Claim)((R4.Bundle)AbdmProfileValidator.Example(PreauthorizationExample)).Entry[0].Resource;
        var ours = (R4.Claim)NhcxClaimBundle.Build(Request(NhcxClaimUse.Preauthorization)).Value.Entry[0].Resource;

        // HC360 keeps a pre-authorisation's procedures as text, not codes, so it asks for them as services.
        var expected = example.EnumerateElements().Select(e => e.Key).Except(["procedure"]);

        expected.Except(ours.EnumerateElements().Select(e => e.Key)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("no diagnosis", "nhcx.claim_diagnosis")]
    [InlineData("not icd-10", "nhcx.claim_diagnosis")]
    [InlineData("no lines", "nhcx.claim_lines")]
    [InlineData("zero quantity", "nhcx.claim_lines")]
    [InlineData("nothing to pay", "nhcx.claim_lines")]
    [InlineData("ends before it starts", "nhcx.claim_period")]
    [InlineData("policy lapsed", "nhcx.claim_policy")]
    [InlineData("no number", "nhcx.claim_number")]
    public void A_request_the_insurer_would_reject_is_refused(string problem, string code)
    {
        var request = Request(NhcxClaimUse.Claim);
        request = problem switch
        {
            "no diagnosis" => request with { Diagnoses = [] },
            "not icd-10" => request with { Diagnoses = [new("Appendicitis")] },
            "no lines" => request with { Lines = [] },
            "zero quantity" => request with { Lines = [new("SRG-APP", "Appendicectomy", 0, 35000)] },
            "nothing to pay" => request with { Lines = [new("CNS-GEN", "Consultation (waived)", 1, 0)] },
            "ends before it starts" => request with { PeriodEnd = Admitted.AddDays(-1) },
            "policy lapsed" => request with { Policy = Policy() with { ValidTo = DateOnly.FromDateTime(Admitted.AddDays(-10).Date) } },
            _ => request with { Number = " " },
        };

        NhcxClaimBundle.Build(request).Error!.Code.ShouldBe(code);
    }

    /// <summary>
    /// Firely does not report a code outside an extensible or example binding, so the claim types and diagnosis types
    /// HC360 sends are checked against the IG's own value sets (code and display, as published) here.
    /// </summary>
    [Theory]
    [InlineData(false, "ValueSet-ndhm-claim-type.json")]
    [InlineData(true, "ValueSet-ndhm-claim-type.json")]
    [InlineData(false, "ValueSet-ndhm-diagnostic-type.json")]
    [InlineData(true, "ValueSet-ndhm-diagnostic-type.json")]
    public void Every_type_sent_is_on_the_igs_list(bool final, string valueSet)
    {
        var listed = JsonNode.Parse(File.ReadAllText(Path.Combine(AbdmProfileValidator.PackageDirectory, valueSet)))!["compose"]!["include"]!.AsArray()
            .SelectMany(include => include!["concept"]!.AsArray().Select(c => ((string)include["system"]!, (string)c!["code"]!, (string)c["display"]!)))
            .ToList();
        var settings = final ? [NhcxCareSetting.Inpatient] : Enum.GetValues<NhcxCareSetting>();

        foreach (var setting in settings)
        {
            var claim = (R4.Claim)NhcxClaimBundle.Build(Request(final ? NhcxClaimUse.Claim : NhcxClaimUse.Preauthorization) with { Setting = setting }).Value.Entry[0].Resource;
            var sent = valueSet.Contains("claim-type", StringComparison.Ordinal) ? [claim.Type] : claim.Diagnosis.SelectMany(d => d.Type);

            foreach (var coding in sent.SelectMany(c => c.Coding))
            {
                listed.ShouldContain((coding.System!, coding.Code!, coding.Display!), $"{setting}: {coding.Code} {coding.Display}");
            }
        }
    }

    private static NhcxPolicyRow Policy() => new()
    {
        Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ac003"),
        PolicyNumber = "P/181100/01/2026/004512",
        MemberId = "SH-77120-02",
        RelationToHolder = "Self",
        ValidFrom = DateOnly.FromDateTime(TestData.Now.AddMonths(-4).Date),
        ValidTo = DateOnly.FromDateTime(TestData.Now.AddMonths(8).Date),
        CreatedAt = TestData.Now.AddMonths(-4),
    };

    private static NhcxClaimRequest Request(NhcxClaimUse use) => new()
    {
        Use = use,
        SourceId = use == NhcxClaimUse.Claim ? ClaimId : PreAuthId,
        Number = use == NhcxClaimUse.Claim ? "NHCL2600017" : "NHPA2600042",
        Created = use == NhcxClaimUse.Claim ? Admitted.AddDays(3) : Admitted.AddHours(2),
        Setting = NhcxCareSetting.Inpatient,
        PeriodStart = Admitted,
        PeriodEnd = use == NhcxClaimUse.Claim ? Admitted.AddDays(3) : null,
        Patient = AbdmTestData.Patient(),
        Facility = AbdmTestData.Facilities[0],
        Payer = new NhcxPayerRow
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ac004"),
            Code = "STAR",
            Name = "Star Health and Allied Insurance",
            CreatedAt = TestData.Now.AddYears(-2),
        },
        Policy = Policy(),
        Doctor = AbdmTestData.Practitioners[0],
        Diagnoses = [new("K35.8", "Acute appendicitis, other and unspecified"), new("r10.3"), new("K35.8")],
        Lines =
        [
            new("SRG-APP", "Laparoscopic appendicectomy", 1, 35000),
            new("ROOM-SEMI", "Semi-private room", 3, 4000),
            new("OT-CHG", "Operation theatre charges", 1, 9000),
        ],
        PreAuthorizationReference = use == NhcxClaimUse.Claim ? "SH/PA/889123" : null,
    };
}
