using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Nhcx;
using HyFib.HealthCare360.Testing.Abdm;
using System.Text.Json.Nodes;
using Hl7.Fhir.Serialization;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests.Nhcx;

/// <summary>
/// The rest of the NHCX exchange, offline: HC360's eligibility checks meet the NRCeS CoverageEligibilityRequestBundle
/// profile (ndhm.in 6.5.0), and the insurer's answers, ClaimResponseBundles and CoverageEligibilityResponseBundles, are
/// read into what Billing records. The IG's own examples of both answers validate against their profiles and are read
/// here figure by figure, so the reader is checked against what NRCeS says an insurer sends, not against HC360's guess.
/// </summary>
public sealed class NhcxResponseTests
{
    private static readonly Guid CheckId = Guid.Parse("01923a4b-0000-7000-8000-0000000ac101");

    [Theory]
    [InlineData("Bundle-ClaimResponseBundle-preauthorization-example-01.json", NhcxResponses.ClaimResponseBundleProfile)]
    [InlineData("Bundle-ClaimResponseBundle-settlement-example-01.json", NhcxResponses.ClaimResponseBundleProfile)]
    [InlineData("Bundle-ClaimResponseBundle-enhancement-example-01.json", NhcxResponses.ClaimResponseBundleProfile)]
    [InlineData("Bundle-ClaimResponseBundle-predetermination-example-01.json", NhcxResponses.ClaimResponseBundleProfile)]
    [InlineData("Bundle-CoverageEligibilityRequestBundle-validation-example-01.json", NhcxEligibilityBundle.BundleProfile)]
    [InlineData("Bundle-CoverageEligibilityRequestBundle-benefits-example-01.json", NhcxEligibilityBundle.BundleProfile)]
    [InlineData("Bundle-CoverageEligibilityResponseBundle-validation-example-01.json", NhcxResponses.EligibilityResponseBundleProfile)]
    [InlineData("Bundle-CoverageEligibilityResponseBundle-benefits-example-01.json", NhcxResponses.EligibilityResponseBundleProfile)]
    [InlineData("Bundle-CoverageEligibilityResponseBundle-auth-requirements-example-01.json", NhcxResponses.EligibilityResponseBundleProfile)]
    [InlineData("Bundle-CoverageEligibilityResponseBundle-discovery-example-01.json", NhcxResponses.EligibilityResponseBundleProfile)]
    public void The_igs_own_examples_validate(string example, string profile)
    {
        var bundle = AbdmProfileValidator.Example(example).ShouldBeOfType<R4.Bundle>();

        var result = AbdmProfileValidator.Validate(bundle, profile);

        result.Errors.ShouldBeEmpty(result.Describe());
    }

    [Theory]
    [InlineData(nameof(NhcxEligibilityPurpose.Validation))]
    [InlineData(nameof(NhcxEligibilityPurpose.Benefits))]
    [InlineData(nameof(NhcxEligibilityPurpose.AuthRequirements))]
    [InlineData(nameof(NhcxEligibilityPurpose.Discovery))]
    public void An_eligibility_check_meets_the_profile(string purpose)
    {
        var bundle = NhcxEligibilityBundle.Build(Check(Enum.Parse<NhcxEligibilityPurpose>(purpose))).Value;

        var result = AbdmProfileValidator.Validate(bundle, NhcxEligibilityBundle.BundleProfile);

        result.Errors.ShouldBeEmpty(result.Describe());
        bundle.Entry.Select(e => e.Resource.TypeName).ShouldBe(["CoverageEligibilityRequest", "Patient", "Practitioner", "Organization", "Organization", "Location", "Coverage"]);
        bundle.Entry.ShouldAllBe(e => e.FullUrl == "urn:uuid:" + e.Resource.Id);
    }

    [Fact]
    public void A_benefits_check_asks_per_service_with_its_diagnosis()
    {
        var bundle = NhcxEligibilityBundle.Build(Check(NhcxEligibilityPurpose.Benefits)).Value;
        var request = (R4.CoverageEligibilityRequest)bundle.Entry[0].Resource;

        request.Purpose.ShouldBe([R4.CoverageEligibilityRequest.EligibilityRequestPurpose.Benefits]);
        request.Identifier.Single().Value.ShouldBe("NHEL2600007");
        request.Insurance.Single().Focal.ShouldBe(true);
        request.Item.Select(i => i.ProductOrService.Coding.Single().Code).ShouldBe(["SRG-APP", "SG039A"]);
        request.Item[1].ProductOrService.Coding.Single().System.ShouldBe(NhcxClaimBundle.HbpSystem);
        request.Item[0].Diagnosis.Select(d => ((R4.CodeableConcept)d.Diagnosis).Coding.Single().Code).ShouldBe(["K35.8"]);
        var coverage = bundle.Entry.Select(e => e.Resource).OfType<R4.Coverage>().Single();
        coverage.Identifier.Single().Value.ShouldBe("P/181100/01/2026/004512", "the policy is named as on a claim, so the insurer matches it the same way");
    }

    [Fact]
    public void A_check_fills_every_request_element_the_igs_example_fills()
    {
        var example = (R4.CoverageEligibilityRequest)((R4.Bundle)AbdmProfileValidator.Example("Bundle-CoverageEligibilityRequestBundle-benefits-example-01.json")).Entry[0].Resource;
        var ours = (R4.CoverageEligibilityRequest)NhcxEligibilityBundle.Build(Check(NhcxEligibilityPurpose.Benefits)).Value.Entry[0].Resource;

        // The example states its language; HC360 writes English narratives and leaves the language to the bundle's reader.
        var expected = example.EnumerateElements().Select(e => e.Key).Except(["language"]);

        expected.Except(ours.EnumerateElements().Select(e => e.Key)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("benefits without services", "nhcx.eligibility_items")]
    [InlineData("approval without services", "nhcx.eligibility_items")]
    [InlineData("service without code", "nhcx.eligibility_items")]
    [InlineData("not icd-10", "nhcx.eligibility_diagnosis")]
    [InlineData("no number", "nhcx.eligibility_number")]
    public void A_check_the_insurer_would_reject_is_refused(string problem, string code)
    {
        var check = Check(NhcxEligibilityPurpose.Benefits);
        check = problem switch
        {
            "benefits without services" => check with { Items = [] },
            "approval without services" => check with { Purpose = NhcxEligibilityPurpose.AuthRequirements, Items = [] },
            "service without code" => check with { Items = [new(" ", "Appendicectomy", [])] },
            "not icd-10" => check with { Items = [new("SRG-APP", "Appendicectomy", [new("Appendicitis")])] },
            _ => check with { Number = "" },
        };

        NhcxEligibilityBundle.Build(check).Error!.Code.ShouldBe(code);
    }

    [Theory]
    [InlineData("preauthorization", nameof(NhcxResponseUse.Preauthorization), "123456", 70000, 3, "preauthapproval", true)]
    [InlineData("enhancement", nameof(NhcxResponseUse.Preauthorization), "98765432", 20000, 2, null, false)]
    [InlineData("predetermination", nameof(NhcxResponseUse.Predetermination), null, 70000, 3, null, false)]
    [InlineData("settlement", nameof(NhcxResponseUse.Claim), "96785763", 90000, 3, "claimapproval", true)]
    public void The_igs_claim_responses_are_read_figure_by_figure(
        string example, string use, string? approval, int approved, int items, string? formCode, bool hasForm)
    {
        var decision = NhcxResponses.ReadClaimResponses(ExampleJson($"Bundle-ClaimResponseBundle-{example}-example-01.json")).Value.ShouldHaveSingleItem();

        decision.Use.ShouldBe(Enum.Parse<NhcxResponseUse>(use));
        decision.Outcome.ShouldBe(NhcxOutcome.Complete);
        decision.PreAuthRef.ShouldBe(approval);
        decision.Submitted.ShouldBe(approved, "every example approves what was asked");
        decision.Approved.ShouldBe(approved);
        decision.Items.Count.ShouldBe(items);
        decision.Items.ShouldAllBe(i => i.Sequence > 0 && i.Amount("submitted") == i.Amount("eligpercent"));
        decision.FormCode.ShouldBe(formCode);
        decision.HasForm.ShouldBe(hasForm);
        decision.Errors.ShouldBeEmpty();
        decision.Request.ShouldStartWith("urn:uuid:");
    }

    [Fact]
    public void A_preauthorizations_approval_period_and_line_reasons_are_kept()
    {
        var decision = NhcxResponses.ReadClaimResponses(ExampleJson("Bundle-ClaimResponseBundle-preauthorization-example-01.json")).Value.Single();

        decision.PreAuthFrom.ShouldBe(DateTimeOffset.Parse("2025-03-08T15:32:26.605+05:30", System.Globalization.CultureInfo.InvariantCulture));
        decision.PreAuthTo.ShouldBe(DateTimeOffset.Parse("2025-04-08T15:32:26.605+05:30", System.Globalization.CultureInfo.InvariantCulture));
        decision.Disposition!.ShouldStartWith("The enclosed services are authorized");
        decision.Items[0].Adjudications.Single(a => a.Category == "eligpercent").Reason.ShouldBe("Product or Service requested is covered by the policy.");
        decision.Items.Select(i => i.Amount("submitted")).ShouldBe([10000m, 40000m, 20000m]);
    }

    [Theory]
    [InlineData("validation", nameof(NhcxEligibilityPurpose.Validation), 0)]
    [InlineData("discovery", nameof(NhcxEligibilityPurpose.Discovery), 0)]
    [InlineData("auth-requirements", nameof(NhcxEligibilityPurpose.AuthRequirements), 2)]
    [InlineData("benefits", nameof(NhcxEligibilityPurpose.Benefits), 2)]
    public void The_igs_eligibility_responses_are_read(string example, string purpose, int items)
    {
        var decision = NhcxResponses.ReadEligibilityResponse(ExampleJson($"Bundle-CoverageEligibilityResponseBundle-{example}-example-01.json")).Value;

        decision.Purposes.ShouldBe([Enum.Parse<NhcxEligibilityPurpose>(purpose)]);
        decision.Outcome.ShouldBe(NhcxOutcome.Complete);
        decision.InForce.ShouldBe(true);
        decision.Disposition.ShouldBe("Policy is currently in-force.");
        var insurance = decision.Insurances.ShouldHaveSingleItem();
        (insurance.From, insurance.To).ShouldBe((new DateOnly(2023, 9, 7), new DateOnly(2026, 8, 7)));
        insurance.Items.Count.ShouldBe(items);
    }

    [Fact]
    public void A_benefits_answer_says_what_is_allowed_and_used_per_service()
    {
        var decision = NhcxResponses.ReadEligibilityResponse(ExampleJson("Bundle-CoverageEligibilityResponseBundle-benefits-example-01.json")).Value;

        var consultation = decision.Insurances.Single().Items[0];
        (consultation.Code, consultation.Display).ShouldBe(("768839008", "Consultant"));
        consultation.Benefits.ShouldBe([new NhcxBenefit("benefit", 50000m, 0m, true)]);
    }

    [Fact]
    public void An_error_answer_lists_why_and_an_unknown_element_is_skipped()
    {
        var bundle = JsonNode.Parse(ExampleJson("Bundle-ClaimResponseBundle-settlement-example-01.json"))!;
        var response = bundle["entry"]!.AsArray().Select(e => e!["resource"]!).Single(r => (string?)r["resourceType"] == "ClaimResponse");
        response["outcome"] = "error";
        response["error"] = JsonNode.Parse("""[{ "code": { "text": "Policy number does not match the member" } }]""");
        response["unknownToFhir"] = true;
        var json = bundle.ToJsonString();

        var decision = NhcxResponses.ReadClaimResponses(json).Value.Single();

        decision.Outcome.ShouldBe(NhcxOutcome.Error);
        decision.Errors.ShouldBe(["Policy number does not match the member"]);
    }

    [Theory]
    [InlineData("not json", "nhcx.response_json")]
    [InlineData("""{ "resourceType": "Patient", "id": "p1" }""", "nhcx.response_bundle")]
    [InlineData("""{ "resourceType": "Bundle", "type": "collection", "entry": [] }""", "nhcx.no_claim_response")]
    public void An_answer_that_is_not_a_claim_response_bundle_is_refused(string json, string code)
    {
        NhcxResponses.ReadClaimResponses(json).Error!.Code.ShouldBe(code);
    }

    [Fact]
    public void An_eligibility_answer_must_carry_exactly_one_response()
    {
        NhcxResponses.ReadEligibilityResponse(ExampleJson("Bundle-ClaimResponseBundle-settlement-example-01.json")).Error!.Code.ShouldBe("nhcx.no_eligibility_response");
    }

    [Fact]
    public void The_same_check_built_twice_is_the_same_bundle()
    {
        var serializer = new FhirJsonSerializer();

        serializer.SerializeToString(NhcxEligibilityBundle.Build(Check(NhcxEligibilityPurpose.Benefits)).Value)
            .ShouldBe(serializer.SerializeToString(NhcxEligibilityBundle.Build(Check(NhcxEligibilityPurpose.Benefits)).Value));
    }

    private static string ExampleJson(string fileName) => File.ReadAllText(Path.Combine(AbdmProfileValidator.ExampleDirectory, fileName));

    private static NhcxEligibilityRequest Check(NhcxEligibilityPurpose purpose) => new()
    {
        Purpose = purpose,
        SourceId = CheckId,
        Number = "NHEL2600007",
        Created = TestData.Now.AddHours(-1),
        Patient = AbdmTestData.Patient(),
        Facility = AbdmTestData.Facilities[0],
        Payer = new NhcxPayerRow
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ac004"),
            Code = "STAR",
            Name = "Star Health and Allied Insurance",
            CreatedAt = TestData.Now.AddYears(-2),
        },
        Policy = new NhcxPolicyRow
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ac003"),
            PolicyNumber = "P/181100/01/2026/004512",
            MemberId = "SH-77120-02",
            RelationToHolder = "Self",
            ValidFrom = DateOnly.FromDateTime(TestData.Now.AddMonths(-4).Date),
            ValidTo = DateOnly.FromDateTime(TestData.Now.AddMonths(8).Date),
            CreatedAt = TestData.Now.AddMonths(-4),
        },
        Enterer = AbdmTestData.Practitioners[0],
        Items = purpose is NhcxEligibilityPurpose.Benefits or NhcxEligibilityPurpose.AuthRequirements
            ?
            [
                new("SRG-APP", "Laparoscopic appendicectomy", [new("K35.8", "Acute appendicitis, other and unspecified")]),
                new("SG039A", "Appendicectomy (laparoscopic)", [new("K35.8")]) { ServiceSystem = NhcxClaimBundle.HbpSystem },
            ]
            : [],
    };
}
