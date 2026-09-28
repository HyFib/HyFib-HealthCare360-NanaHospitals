using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>An HTTP status with the FHIR <c>OperationOutcome</c> that explains it.</summary>
internal sealed record FhirError(int StatusCode, R4.OperationOutcome Outcome)
{
    /// <summary>Machine-readable reason (<c>urn:hc360:fhir-issue</c> code).</summary>
    public string? Reason => Outcome.Issue.FirstOrDefault()?.Details?.Coding.FirstOrDefault()?.Code;
}

/// <summary>
/// Every error of the facade is an <c>OperationOutcome</c> with the matching HTTP status and a machine-readable reason
/// in <c>issue.details</c> (<c>urn:hc360:fhir-issue</c>). Messages never contain patient data.
/// </summary>
internal static class FhirOutcomes
{
    public const string ConsentRequired = "consent-required";
    public const string RestrictedRecord = "restricted-record";
    public const string PatientMerged = "patient-merged";
    public const string Deleted = "deleted";
    public const string NotFound = "not-found";
    public const string UnknownResourceType = "unknown-resource-type";
    public const string InvalidParameter = "invalid-parameter";
    public const string UnsupportedParameter = "unsupported-parameter";
    public const string PatientRequired = "patient-required";
    public const string NotAcceptable = "not-acceptable";
    public const string AuthenticationRequired = "authentication-required";
    public const string PermissionRequired = "permission-required";
    public const string ReadOnly = "read-only";
    public const string RecordsWithheld = "records-withheld";
    public const string ServerError = "server-error";

    public static R4.OperationOutcome Create(R4.OperationOutcome.IssueSeverity severity, R4.OperationOutcome.IssueType type, string message, string reason)
    {
        var outcome = new R4.OperationOutcome();
        outcome.Issue.Add(new R4.OperationOutcome.IssueComponent
        {
            Severity = severity,
            Code = type,
            Details = new R4.CodeableConcept { Coding = [FhirElements.Coding(FhirSystems.Issue, reason)], Text = message },
            Diagnostics = message,
        });
        return outcome;
    }

    public static FhirError Error(int status, R4.OperationOutcome.IssueType type, string message, string reason) =>
        new(status, Create(R4.OperationOutcome.IssueSeverity.Error, type, message, reason));

    public static FhirError BadRequest(string message, string reason = InvalidParameter) =>
        Error(StatusCodes.Status400BadRequest, R4.OperationOutcome.IssueType.Invalid, message, reason);

    public static FhirError Unsupported(string message) =>
        Error(StatusCodes.Status400BadRequest, R4.OperationOutcome.IssueType.NotSupported, message, UnsupportedParameter);

    public static FhirError ResourceNotFound(string resourceType, string id) =>
        Error(StatusCodes.Status404NotFound, R4.OperationOutcome.IssueType.NotFound, $"{resourceType}/{id} was not found.", NotFound);

    public static FhirError UnknownType(string resourceType) =>
        Error(StatusCodes.Status404NotFound, R4.OperationOutcome.IssueType.NotSupported,
            $"Resource type '{Printable(resourceType)}' is not served. Supported: {string.Join(", ", FhirResourceTypes.Names)}.", UnknownResourceType);

    public static FhirError NotAcceptableFormat() =>
        Error(StatusCodes.Status406NotAcceptable, R4.OperationOutcome.IssueType.NotSupported,
            "This server returns JSON only (application/fhir+json).", NotAcceptable);

    public static FhirError ReadOnlyServer() =>
        Error(StatusCodes.Status405MethodNotAllowed, R4.OperationOutcome.IssueType.NotSupported,
            "This FHIR endpoint is read-only: it supports read, search (GET or POST _search) and metadata.", ReadOnly);

    public static FhirError Unauthenticated() =>
        Error(StatusCodes.Status401Unauthorized, R4.OperationOutcome.IssueType.Login,
            "Sign in first: send an HC360 access token as 'Authorization: Bearer <token>'.", AuthenticationRequired);

    public static FhirError NoPermission() =>
        Error(StatusCodes.Status403Forbidden, R4.OperationOutcome.IssueType.Forbidden,
            "The fhir.read permission is required to use the FHIR API.", PermissionRequired);

    public static FhirError Unexpected(string referenceId) =>
        Error(StatusCodes.Status500InternalServerError, R4.OperationOutcome.IssueType.Exception,
            $"The server could not complete the request. Quote reference {referenceId} if you contact support.", ServerError);

    /// <summary>The error returned for a record the sharing rules do not allow (reads only; searches exclude it).</summary>
    public static FhirError Denied(SharingDecision decision, string resourceType, string id, Guid? mergedIntoPatientId = null) => decision switch
    {
        SharingDecision.Deleted => Error(StatusCodes.Status410Gone, R4.OperationOutcome.IssueType.Deleted,
            "The patient record this resource belongs to was deleted.", Deleted),
        SharingDecision.Merged => Error(StatusCodes.Status410Gone, R4.OperationOutcome.IssueType.Deleted,
            mergedIntoPatientId is { } target
                ? $"This patient record was merged into Patient/{FhirIds.Of(target)}."
                : "This patient record was merged into another record. Search by ABHA number to find the current record.",
            PatientMerged),
        SharingDecision.NoConsent => Error(StatusCodes.Status403Forbidden, R4.OperationOutcome.IssueType.Forbidden,
            "The patient has not consented to sharing their health record (no active record-sharing consent is on file).", ConsentRequired),
        SharingDecision.Restricted => Error(StatusCodes.Status403Forbidden, R4.OperationOutcome.IssueType.Forbidden,
            "This is a restricted record. Access needs the patients.restricted.read permission or an emergency (break-the-glass) grant.", RestrictedRecord),
        _ => ResourceNotFound(resourceType, id),
    };

    /// <summary>
    /// Added to a patient-scoped search that returned nothing because the patient's records may not be shared. The same
    /// wording is used whatever the reason (no consent, restricted, unknown patient), so it reveals nothing further.
    /// </summary>
    public static R4.OperationOutcome Withheld() => Create(R4.OperationOutcome.IssueSeverity.Information, R4.OperationOutcome.IssueType.Suppressed,
        "No records were returned for this patient. Records are shared only for patients with an active record-sharing consent, and restricted records need additional authorisation.",
        RecordsWithheld);

    /// <summary>Echoes a client-supplied value safely (bounded and printable).</summary>
    public static string Printable(string value)
    {
        var printable = new string(value.Where(c => !char.IsControl(c)).Take(64).ToArray());
        return printable.Length < value.Length ? printable + "…" : printable;
    }
}
