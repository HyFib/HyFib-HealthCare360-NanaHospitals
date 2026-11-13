using System.Globalization;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Interop.Features;

/// <summary>
/// <c>/api/v1/abdm</c> (SRS EHR-08), only while ABDM health records are switched on (<see cref="FeatureKeys.Abdm"/>):
/// one record as an ABDM FHIR document (<c>application/fhir+json</c>, a file to save, never cached), and the gateway's set-up for
/// administrators. Errors are the API's usual problem details: 404 for a record that does not exist in the caller's
/// facility, 422 <c>abdm.not_exportable</c> for one not signed or released yet, 403 for a restricted record (or
/// <c>abdm.restricted_duplicate</c> for a restricted duplicate whose records it holds), and 503 <c>pdf.unavailable</c>
/// when its printed PDF could not be made just now.
/// </summary>
internal static class AbdmEndpoints
{
    public const string BasePath = "/api/v1/abdm";

    public static void Map(IEndpointRouteBuilder app)
    {
        var abdm = app.MapGroup(BasePath).WithTags("ABDM").RequireFeature(FeatureKeys.Abdm);

        var bundles = abdm.MapGroup("/bundles").RequirePermission(InteropPermissions.AbdmExport);
        bundles.MapGet("/discharge-summaries/{id:guid}", (Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct) =>
            ExportAsync(AbdmRecordType.DischargeSummary, id, pretty, service, ct));
        bundles.MapGet("/lab-reports/{id:guid}", (Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct) =>
            ExportAsync(AbdmRecordType.LabReport, id, pretty, service, ct));
        bundles.MapGet("/op-consultations/{id:guid}", (Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct) =>
            ExportAsync(AbdmRecordType.OpConsultation, id, pretty, service, ct));
        bundles.MapGet("/prescriptions/{id:guid}", (Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct) =>
            ExportAsync(AbdmRecordType.Prescription, id, pretty, service, ct));

        // The radiology report's own id, as the FHIR API's DiagnosticReport and the patient's timeline name it.
        bundles.MapGet("/radiology-reports/{id:guid}", (Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct) =>
            ExportAsync(AbdmRecordType.RadiologyReport, id, pretty, service, ct));

        // The cardiology report's own id, as the FHIR API's DiagnosticReport names it.
        bundles.MapGet("/cardiology-reports/{id:guid}", (Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct) =>
            ExportAsync(AbdmRecordType.CardiologyReport, id, pretty, service, ct));

        // A vaccine dose given here, by its own id, as the FHIR API's Immunization and the patient's Immunisations tab name it.
        bundles.MapGet("/immunizations/{id:guid}", (Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct) =>
            ExportAsync(AbdmRecordType.Immunization, id, pretty, service, ct));

        // A signed clinical note by its own id, as the FHIR API's DocumentReference and the patient's Notes tab name it; it
        // carries the addenda signed to it.
        bundles.MapGet("/clinical-notes/{id:guid}", (Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct) =>
            ExportAsync(AbdmRecordType.ClinicalNote, id, pretty, service, ct));

        // A finalised bill by its own id, as Billing's bill screen names it.
        bundles.MapGet("/invoices/{id:guid}", (Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct) =>
            ExportAsync(AbdmRecordType.Invoice, id, pretty, service, ct));

        // A remote monitoring enrolment's home readings measured in a month (yyyy-MM; the month of the latest reading
        // when none is given).
        bundles.MapGet("/home-readings/{id:guid}", (Guid id, string? month, bool? pretty, AbdmBundleService service, CancellationToken ct) =>
            ExportReadingsAsync(id, month, pretty, service, ct));

        abdm.MapGet("/gateway", async (AbdmGatewayConfiguration gateway, CancellationToken ct) => TypedResults.Ok(await gateway.StatusAsync(ct)))
            .RequirePermission(PlatformPermissions.IntegrationsManage);

        // HIP-initiated linking: completed records linked to the patient's ABHA as care contexts (AbdmLinkingService), by
        // staff here or by the hospital itself as they are completed (AbdmAutoLinking). The gateway answers later through
        // its callbacks (AbdmGatewayCallbackEndpoints); the request shows where it has got to, and one that failed (or
        // that ABDM never answered, AbdmUnansweredRequests) is sent again with /retry, as a new request.
        var links = abdm.MapGroup("/links").RequirePermission(InteropPermissions.AbdmExport);
        links.MapPost("", async (AbdmLinkInput input, AbdmLinkingService linking, CancellationToken ct) =>
                (await linking.StartAsync(input, ct)).ToHttpResult(view => TypedResults.Accepted($"{BasePath}/links/{view.Id}", view)))
            .WithValidation<AbdmLinkInput>();
        links.MapGet("/{id:guid}", async (Guid id, AbdmLinkingService linking, CancellationToken ct) =>
            (await linking.GetAsync(id, ct)).ToOk());
        links.MapPost("/{id:guid}/retry", async (Guid id, AbdmLinkingService linking, CancellationToken ct) =>
            (await linking.RetryAsync(id, ct)).ToHttpResult(view => TypedResults.Accepted($"{BasePath}/links/{view.Id}", view)));
        abdm.MapGet("/patients/{patientId:guid}/links", async (Guid patientId, AbdmLinkingService linking, CancellationToken ct) =>
                (await linking.ListAsync(patientId, ct)).ToOk())
            .RequirePermission(InteropPermissions.AbdmExport);

        // Scan and share: the counter's queue of patients who shared their ABHA profile (AbdmProfileShareService). The desk
        // registers them from it; the full details (ABHA number, mobile) need the contact permission, as a patient's edit does.
        var shares = abdm.MapGroup("/shares").RequirePermission(PatientsPermissions.Register);
        shares.MapGet("", async (AbdmProfileShareService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(ct)));
        shares.MapGet("/{id:guid}", async (Guid id, AbdmProfileShareService service, CancellationToken ct) => (await service.GetAsync(id, ct)).ToOk())
            .RequirePermission(PatientsPermissions.ContactRead);
        shares.MapPost("/{id:guid}/registered", async (Guid id, AbdmShareRegisteredInput input, AbdmProfileShareService service, CancellationToken ct) =>
                (await service.RegisteredAsync(id, input, ct)).ToOk())
            .WithValidation<AbdmShareRegisteredInput>();
        shares.MapPost("/{id:guid}/dismiss", async (Guid id, AbdmProfileShareService service, CancellationToken ct) =>
            (await service.DismissAsync(id, ct)).ToOk());

        // Checking a patient's ABHA with ABDM at the desk (AbhaVerificationService): a code to the mobile number linked to
        // the ABHA on the record, which the patient reads out. Identity checks need the identity permission, as Aadhaar's do.
        abdm.MapGet("/patients/{patientId:guid}/abha-checks", async (Guid patientId, AbhaVerificationService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(patientId, ct)))
            .RequirePermission(PatientsPermissions.Read);
        abdm.MapPost("/patients/{patientId:guid}/abha-checks", async (Guid patientId, AbhaVerificationStart input, AbhaVerificationService service, CancellationToken ct) =>
                (await service.StartAsync(patientId, input, ct)).ToOk())
            .RequirePermission(PatientsPermissions.IdentityVerify)
            .WithValidation<AbhaVerificationStart>();
        // The hospital as HIU (ABDM M3): asking the patient to share their records at other providers.
        abdm.MapGet("/patients/{patientId:guid}/consent-requests", async (Guid patientId, AbdmConsentRequestService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(patientId, ct)))
            .RequirePermission(InteropPermissions.AbdmRecordsRequest);
        abdm.MapPost("/patients/{patientId:guid}/consent-requests", async (Guid patientId, AbdmConsentRequestInput input, AbdmConsentRequestService service, CancellationToken ct) =>
                (await service.RequestAsync(patientId, input, ct)).ToOk())
            .RequirePermission(InteropPermissions.AbdmRecordsRequest)
            .WithValidation<AbdmConsentRequestInput>();
        abdm.MapPost("/patients/{patientId:guid}/consent-requests/{id:guid}/retry", async (Guid patientId, Guid id, AbdmConsentRequestService service, CancellationToken ct) =>
                (await service.RetryAsync(patientId, id, ct)).ToOk())
            .RequirePermission(InteropPermissions.AbdmRecordsRequest);
        abdm.MapPost("/patients/{patientId:guid}/consent-artefacts/{artefactId}/records-requests", async (Guid patientId, string artefactId, AbdmHealthInformationService service, CancellationToken ct) =>
                (await service.RequestAsync(patientId, artefactId, ct)).ToOk())
            .RequirePermission(InteropPermissions.AbdmRecordsRequest);
        abdm.MapGet("/patients/{patientId:guid}/received-records", async (Guid patientId, AbdmHealthInformationService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(patientId, ct)))
            .RequirePermission(InteropPermissions.AbdmRecordsRequest);
        abdm.MapGet("/patients/{patientId:guid}/received-records/{recordId:guid}", async (Guid patientId, Guid recordId, AbdmHealthInformationService service, CancellationToken ct) =>
                (await service.ReadAsync(patientId, recordId, ct)).ToOk())
            .RequirePermission(InteropPermissions.AbdmRecordsRequest);
        abdm.MapPost("/abha-checks/{id:guid}/code", async (Guid id, AbhaVerificationCode input, AbhaVerificationService service, CancellationToken ct) =>
                (await service.VerifyAsync(id, input, ct)).ToOk())
            .RequirePermission(PatientsPermissions.IdentityVerify)
            .WithValidation<AbhaVerificationCode>();
    }

    private static async Task<IResult> ExportAsync(AbdmRecordType type, Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct)
    {
        try
        {
            var export = await service.ExportAsync(type, id, ct);
            return export.IsSuccess
                ? new FhirResult(export.Value.Bundle, StatusCodes.Status200OK, pretty == true, fileName: export.Value.FileName)
                : export.Error!.ToProblem();
        }
        catch (PdfRenderException ex)
        {
            return DocumentResults.PdfUnavailable($"{ex.Message} The ABDM record holds the printed copy: try again in a moment.");
        }
    }

    private static async Task<IResult> ExportReadingsAsync(Guid id, string? month, bool? pretty, AbdmBundleService service, CancellationToken ct)
    {
        DateOnly? day = null;
        if (!string.IsNullOrWhiteSpace(month))
        {
            if (!DateOnly.TryParseExact(month.Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
            {
                return Error.Field("month", "Give the month as year and month, for example 2026-09.").ToProblem();
            }

            day = first;
        }

        var export = await service.ExportReadingsAsync(id, day, ct);
        return export.IsSuccess
            ? new FhirResult(export.Value.Bundle, StatusCodes.Status200OK, pretty == true, fileName: export.Value.FileName)
            : export.Error!.ToProblem();
    }
}
