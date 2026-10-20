using System.Text.Json;
using Firely.Fhir.Validation;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Specification.Terminology;
using R4 = Hl7.Fhir.Model;

// Shared by the Interop module's tests and the integration tests (which link this file and TestData), hence a namespace
// of its own.
namespace HyFib.HealthCare360.Testing.Abdm;

/// <summary>What the validator found, and the value sets whose codes it could not check offline.</summary>
internal sealed record ProfileValidation(R4.OperationOutcome Outcome, IReadOnlyList<string> UncheckedValueSets)
{
    public IReadOnlyList<R4.OperationOutcome.IssueComponent> Errors { get; } =
        [.. Outcome.Issue.Where(i => i.Severity is R4.OperationOutcome.IssueSeverity.Error or R4.OperationOutcome.IssueSeverity.Fatal)];

    public IReadOnlyList<R4.OperationOutcome.IssueComponent> Warnings { get; } =
        [.. Outcome.Issue.Where(i => i.Severity is R4.OperationOutcome.IssueSeverity.Warning)];

    /// <summary>Every issue on its own line, for assertion messages.</summary>
    public string Describe() =>
        Outcome.Issue.Count == 0
            ? "no issues"
            : string.Join(Environment.NewLine, Outcome.Issue.Select(i =>
                $"{i.Severity} [{i.Code}] {i.Details?.Text ?? i.Diagnostics} at {string.Join(", ", i.Expression ?? [])}"));
}

/// <summary>
/// Firely's FHIR validator (Firely.Fhir.Validation.R4) with the NRCeS "FHIR Implementation Guide for ABDM" (package
/// <c>ndhm.in</c> 6.5.0, the files in <c>TestData/ndhm.in-6.5.0/package</c>) over the FHIR R4 core definitions
/// (specification.zip of Hl7.Fhir.Specification.R4). It checks every structural, cardinality, slicing, fixed-value,
/// reference-target and invariant (FHIRPath) rule of the profiles, following references inside a document bundle.
/// <para>
/// Terminology: bindings are checked offline (Firely's LocalTerminologyService) against the value sets that can be
/// expanded from the core specification and the IG files: FHIR and HL7 code systems, MIME types, languages and the IG's
/// own code systems. Checking a code against an external terminology (SNOMED CT, LOINC, ICD-10) needs a terminology
/// server, which these tests do not have, so such a check is skipped: the validator reports it as a warning and
/// <see cref="ProfileValidation.UncheckedValueSets"/> lists it, so a skipped check is never mistaken for a passed one. (In
/// the NRCeS profiles those bindings are of example or preferred strength, which validators do not enforce; the codes the
/// profiles fix, such as the SNOMED CT document and section codes, are checked as fixed values.)
/// </para>
/// <para>The validator and its resolvers are not thread-safe, so validations run one at a time.</para>
/// </summary>
internal static class AbdmProfileValidator
{
    public const string DocumentBundle = "https://nrces.in/ndhm/fhir/r4/StructureDefinition/DocumentBundle";

    private static readonly Lock Gate = new();
    private static readonly List<string> Unchecked = [];
    private static readonly Lazy<Validator> Instance = new(Create);

    public static string PackageDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "TestData", "ndhm.in-6.5.0", "package");

    public static string ExampleDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "TestData", "ndhm.in-6.5.0", "example");

    /// <summary>Validates the resource against its <c>meta.profile</c> (and the core definition of its type).</summary>
    public static ProfileValidation Validate(R4.Resource resource) => Validate(resource, profile: null);

    /// <summary>Validates the resource against <paramref name="profile"/> as well as its own <c>meta.profile</c>.</summary>
    public static ProfileValidation Validate(R4.Resource resource, string? profile)
    {
        lock (Gate)
        {
            Unchecked.Clear();
            var outcome = Instance.Value.Validate(resource, profile);
            return new ProfileValidation(outcome, [.. Unchecked.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
        }
    }

    /// <summary>Reads one of the IG's own examples (FHIR JSON, as published).</summary>
    public static R4.Resource Example(string fileName) =>
        JsonSerializer.Deserialize<R4.Resource>(File.ReadAllText(Path.Combine(ExampleDirectory, fileName)),
            new JsonSerializerOptions().ForFhir(R4.ModelInfo.ModelInspector))!;

    private static Validator Create()
    {
        var resolver = new CachedResolver(new MultiResolver(
            new DirectorySource(PackageDirectory, new DirectorySourceSettings { IncludeSubDirectories = false }),
            new ZipSource(Path.Combine(AppContext.BaseDirectory, "specification.zip"))));
        var settings = new ValidationSettings
        {
            HandleValidateCodeServiceFailure = Skip,
        };
        return new Validator(resolver, LocalTerminologyService.CreateDefaultForCore(resolver), null, settings);
    }

    /// <summary>A code the offline terminology service cannot check (an external code system): skipped, with a warning.</summary>
    private static TerminologyServiceExceptionResult Skip(ValidateCodeParameters parameters, FhirOperationException exception)
    {
        Unchecked.Add(parameters.Url?.Value ?? "(no value set)");
        return TerminologyServiceExceptionResult.Warning;
    }
}
