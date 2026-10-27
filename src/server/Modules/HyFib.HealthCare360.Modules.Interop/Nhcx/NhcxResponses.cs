using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Services;
using Hl7.Fhir.Serialization;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Nhcx;

/// <summary>What a ClaimResponse answers: a pre-authorisation (also an enhancement of one), a predetermination, or a claim.</summary>
internal enum NhcxResponseUse
{
    Preauthorization = 0,
    Predetermination,
    Claim,
}

/// <summary>Where the insurer has got to (FHIR's <c>remittance-outcome</c>).</summary>
internal enum NhcxOutcome
{
    /// <summary>Received, not decided yet: another response follows.</summary>
    Queued = 0,

    /// <summary>Decided.</summary>
    Complete,

    /// <summary>Decided in part: some lines are still being looked at, or were refused for errors.</summary>
    Partial,

    /// <summary>Not processed: the request has errors (<see cref="NhcxClaimDecision.Errors"/>).</summary>
    Error,
}

/// <summary>One figure the insurer gives for a line or for the whole: the category (submitted, eligible, benefit, copay...), an amount or a value, and why.</summary>
internal sealed record NhcxAdjudication(string Category, decimal? Amount, decimal? Value, string? Reason);

/// <summary>The insurer's decision on one line of the request, by the line's sequence there.</summary>
internal sealed record NhcxItemDecision(int Sequence, IReadOnlyList<NhcxAdjudication> Adjudications)
{
    public decimal? Amount(string category) => Adjudications.FirstOrDefault(a => a.Category == category)?.Amount;
}

/// <summary>An insurer's ClaimResponse as Billing records it.</summary>
internal sealed record NhcxClaimDecision
{
    /// <summary>The ClaimResponse's id, unique per response.</summary>
    public required string ResponseId { get; init; }

    public required NhcxResponseUse Use { get; init; }
    public required NhcxOutcome Outcome { get; init; }

    /// <summary>The reference to the request as the insurer quotes it (for HC360's requests, <c>urn:uuid:</c> of the Claim).</summary>
    public string? Request { get; init; }

    public DateTimeOffset? Created { get; init; }

    /// <summary>The insurer's words on the decision.</summary>
    public string? Disposition { get; init; }

    /// <summary>The approval reference the claim must quote, and how long the approval holds.</summary>
    public string? PreAuthRef { get; init; }

    public DateTimeOffset? PreAuthFrom { get; init; }
    public DateTimeOffset? PreAuthTo { get; init; }

    /// <summary>The totals by adjudication category (<c>submitted</c>, <c>eligible</c>, <c>benefit</c>, <c>copay</c>, <c>deductible</c>...).</summary>
    public required IReadOnlyDictionary<string, decimal> Totals { get; init; }

    public required IReadOnlyList<NhcxItemDecision> Items { get; init; }

    /// <summary>What the insurer pays (a settled claim), when and under which reference.</summary>
    public decimal? PaymentAmount { get; init; }

    public DateOnly? PaymentDate { get; init; }
    public string? PaymentReference { get; init; }

    /// <summary>The insurer's letter (approval or settlement), when one came with the response: its code and whether a file did.</summary>
    public string? FormCode { get; init; }

    public bool HasForm { get; init; }

    /// <summary>Why the request was not processed (or not all of it), as the insurer words or codes it.</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>
    /// What the insurer approves or pays: the <c>benefit</c> total, else the <c>eligible</c> one; NRCeS's own examples give
    /// the approved amount under <c>eligpercent</c>, so that is read last when it is an amount.
    /// </summary>
    public decimal? Approved => Totals.TryGetValue("benefit", out var benefit) ? benefit
        : Totals.TryGetValue("eligible", out var eligible) ? eligible
        : Totals.TryGetValue("eligpercent", out var percent) ? percent
        : null;

    public decimal? Submitted => Totals.TryGetValue("submitted", out var submitted) ? submitted : null;
}

/// <summary>What the insurer allows and has used for one benefit of a service (money, or a count).</summary>
internal sealed record NhcxBenefit(string Type, decimal? Allowed, decimal? Used, bool IsMoney);

/// <summary>The insurer's answer for one service asked about.</summary>
internal sealed record NhcxEligibilityItemDecision(
    string? Code, string? Display, bool? Excluded, bool? AuthorizationRequired, IReadOnlyList<NhcxBenefit> Benefits);

/// <summary>The insurer's answer for one policy: in force or not, for which period, and per service.</summary>
internal sealed record NhcxCoverageDecision(
    string? Coverage, bool? InForce, DateOnly? From, DateOnly? To, IReadOnlyList<NhcxEligibilityItemDecision> Items);

/// <summary>An insurer's CoverageEligibilityResponse as Billing records it.</summary>
internal sealed record NhcxEligibilityDecision
{
    public required string ResponseId { get; init; }
    public required NhcxOutcome Outcome { get; init; }
    public required IReadOnlyList<NhcxEligibilityPurpose> Purposes { get; init; }
    public string? Request { get; init; }
    public DateTimeOffset? Created { get; init; }
    public string? Disposition { get; init; }
    public string? PreAuthRef { get; init; }
    public required IReadOnlyList<NhcxCoverageDecision> Insurances { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>In force when every policy the insurer answered for says so; null when it did not say.</summary>
    public bool? InForce => Insurances.Count == 0 || Insurances.Any(i => i.InForce is null) ? null : Insurances.All(i => i.InForce == true);
}

/// <summary>
/// Reads what an insurer sends back through NHCX, once the envelope is opened: a <c>ClaimResponseBundle</c> (the answer to
/// a pre-authorisation, an enhancement of one, a predetermination or a claim) or a <c>CoverageEligibilityResponseBundle</c>
/// (the answer to an eligibility check, <see cref="NhcxEligibilityBundle"/>), into what Billing records: the outcome, the
/// insurer's words, the approval reference and period, the totals and each line's figures, the payment, and the errors.
/// The insurer's JSON is read as FHIR R4; an element the model does not know is skipped rather than refusing the whole
/// answer, but a body that is not a Bundle of the expected kind, or carries no response, is refused.
/// </summary>
internal static class NhcxResponses
{
    public const string ClaimResponseBundleProfile = AbdmProfiles.Base + "ClaimResponseBundle";
    public const string EligibilityResponseBundleProfile = AbdmProfiles.Base + "CoverageEligibilityResponseBundle";

    private const string Adjudication = "http://terminology.hl7.org/CodeSystem/adjudication";

    private static readonly JsonSerializerOptions Fhir = new JsonSerializerOptions().ForFhir(R4.ModelInfo.ModelInspector);

    /// <summary>The ClaimResponses of a ClaimResponseBundle (the profile allows several; one per request is usual).</summary>
    public static Result<IReadOnlyList<NhcxClaimDecision>> ReadClaimResponses(string json)
    {
        var bundle = ReadBundle(json);
        if (bundle.IsFailure)
        {
            return bundle.Error!;
        }

        var responses = bundle.Value.Entry.Select(e => e.Resource).OfType<R4.ClaimResponse>().ToList();
        return responses.Count == 0
            ? Error.Validation("nhcx.no_claim_response", "The insurer's answer carries no ClaimResponse.")
            : responses.Select(ClaimDecision).ToList();
    }

    /// <summary>The CoverageEligibilityResponse of a CoverageEligibilityResponseBundle (the profile allows exactly one).</summary>
    public static Result<NhcxEligibilityDecision> ReadEligibilityResponse(string json)
    {
        var bundle = ReadBundle(json);
        if (bundle.IsFailure)
        {
            return bundle.Error!;
        }

        var responses = bundle.Value.Entry.Select(e => e.Resource).OfType<R4.CoverageEligibilityResponse>().ToList();
        return responses.Count switch
        {
            0 => Error.Validation("nhcx.no_eligibility_response", "The insurer's answer carries no CoverageEligibilityResponse."),
            > 1 => Error.Validation("nhcx.no_eligibility_response", "The insurer's answer carries more than one CoverageEligibilityResponse."),
            _ => EligibilityDecision(responses[0]),
        };
    }

    private static Result<R4.Bundle> ReadBundle(string json)
    {
        R4.Resource? resource;
        try
        {
            resource = JsonSerializer.Deserialize<R4.Resource>(json, Fhir);
        }
        catch (DeserializationFailedException ex) when (ex.PartialResult is R4.Resource partial)
        {
            // Elements the model does not know, or values out of shape, are left out; what could be read is used.
            resource = partial;
        }
        catch (Exception ex) when (ex is JsonException or DeserializationFailedException)
        {
            return Error.Validation("nhcx.response_json", "The insurer's answer is not FHIR JSON.");
        }

        return resource is R4.Bundle bundle
            ? bundle
            : Error.Validation("nhcx.response_bundle", $"The insurer's answer is a {resource?.TypeName ?? "nothing"}, not a Bundle.");
    }

    private static NhcxClaimDecision ClaimDecision(R4.ClaimResponse response) => new()
    {
        ResponseId = response.Id ?? string.Empty,
        Use = response.Use switch
        {
            R4.ClaimUseCode.Claim => NhcxResponseUse.Claim,
            R4.ClaimUseCode.Predetermination => NhcxResponseUse.Predetermination,
            _ => NhcxResponseUse.Preauthorization,
        },
        Outcome = Outcome(response.Outcome),
        Request = response.Request?.Reference,
        Created = Instant(response.CreatedElement?.Value),
        Disposition = FhirElements.Clean(response.Disposition),
        PreAuthRef = FhirElements.Clean(response.PreAuthRef),
        PreAuthFrom = Instant(response.PreAuthPeriod?.Start),
        PreAuthTo = Instant(response.PreAuthPeriod?.End),
        Totals = response.Total
            .Select(t => (Category: Category(t.Category), t.Amount?.Value))
            .Where(t => t.Category is not null && t.Value is not null)
            .GroupBy(t => t.Category!)
            .ToDictionary(g => g.Key, g => g.First().Value!.Value),
        Items = [.. response.Item.Select(i => new NhcxItemDecision(i.ItemSequence ?? 0, [.. i.Adjudication.Select(Adjudicated)]))],
        PaymentAmount = response.Payment?.Amount?.Value,
        PaymentDate = DateOnly.TryParse(response.Payment?.Date, out var paid) ? paid : null,
        PaymentReference = FhirElements.Clean(response.Payment?.Identifier?.Value),
        FormCode = response.FormCode?.Coding.FirstOrDefault()?.Code,
        HasForm = response.Form is { } form && (form.Data is { Length: > 0 } || !string.IsNullOrWhiteSpace(form.Url)),
        Errors = [.. response.Error.Select(e => Words(e.Code)).OfType<string>()],
    };

    private static NhcxEligibilityDecision EligibilityDecision(R4.CoverageEligibilityResponse response) => new()
    {
        ResponseId = response.Id ?? string.Empty,
        Outcome = Outcome(response.Outcome),
        Purposes = [.. response.Purpose.OfType<R4.CoverageEligibilityResponse.EligibilityResponsePurpose>().Select(p => p switch
        {
            R4.CoverageEligibilityResponse.EligibilityResponsePurpose.Benefits => NhcxEligibilityPurpose.Benefits,
            R4.CoverageEligibilityResponse.EligibilityResponsePurpose.AuthRequirements => NhcxEligibilityPurpose.AuthRequirements,
            R4.CoverageEligibilityResponse.EligibilityResponsePurpose.Discovery => NhcxEligibilityPurpose.Discovery,
            _ => NhcxEligibilityPurpose.Validation,
        })],
        Request = response.Request?.Reference,
        Created = Instant(response.CreatedElement?.Value),
        Disposition = FhirElements.Clean(response.Disposition),
        PreAuthRef = FhirElements.Clean(response.PreAuthRef),
        Insurances = [.. response.Insurance.Select(i => new NhcxCoverageDecision(
            i.Coverage?.Reference,
            i.Inforce,
            DateOnly.TryParse(i.BenefitPeriod?.Start?[..Math.Min(10, i.BenefitPeriod.Start.Length)], out var from) ? from : null,
            DateOnly.TryParse(i.BenefitPeriod?.End?[..Math.Min(10, i.BenefitPeriod.End.Length)], out var to) ? to : null,
            [.. i.Item.Select(item => new NhcxEligibilityItemDecision(
                item.ProductOrService?.Coding.FirstOrDefault()?.Code,
                item.ProductOrService?.Coding.FirstOrDefault()?.Display ?? item.ProductOrService?.Text,
                item.Excluded,
                item.AuthorizationRequired,
                [.. item.Benefit.Select(Benefit)]))]))],
        Errors = [.. response.Error.Select(e => Words(e.Code)).OfType<string>()],
    };

    private static NhcxBenefit Benefit(R4.CoverageEligibilityResponse.BenefitComponent b)
    {
        var type = b.Type?.Coding.FirstOrDefault()?.Code ?? b.Type?.Text ?? "benefit";
        return b.Allowed is R4.Money or null && b.Used is R4.Money or null
            ? new NhcxBenefit(type, (b.Allowed as R4.Money)?.Value, (b.Used as R4.Money)?.Value, true)
            : new NhcxBenefit(type, Count(b.Allowed), Count(b.Used), false);
    }

    private static decimal? Count(R4.DataType? value) => value switch
    {
        R4.UnsignedInt u => u.Value,
        R4.Money m => m.Value,
        _ => null,
    };

    private static NhcxAdjudication Adjudicated(R4.ClaimResponse.AdjudicationComponent a) =>
        new(Category(a.Category) ?? "other", a.Amount?.Value, a.Value, Words(a.Reason));

    /// <summary>The adjudication category's code when it is FHIR's (or has no system), else its text.</summary>
    private static string? Category(R4.CodeableConcept? concept) =>
        concept?.Coding.FirstOrDefault(c => c.System is null or Adjudication)?.Code
        ?? concept?.Coding.FirstOrDefault()?.Code
        ?? FhirElements.Clean(concept?.Text);

    private static string? Words(R4.CodeableConcept? concept) =>
        FhirElements.Clean(concept?.Text)
        ?? concept?.Coding.Select(c => FhirElements.Clean(c.Display) ?? FhirElements.Clean(c.Code)).FirstOrDefault(s => s is not null);

    private static NhcxOutcome Outcome(R4.ClaimProcessingCodes? outcome) => outcome switch
    {
        R4.ClaimProcessingCodes.Complete => NhcxOutcome.Complete,
        R4.ClaimProcessingCodes.Partial => NhcxOutcome.Partial,
        R4.ClaimProcessingCodes.Error => NhcxOutcome.Error,
        _ => NhcxOutcome.Queued,
    };

    private static DateTimeOffset? Instant(string? value) => AbdmDataFlowService.Instant(value);
}
