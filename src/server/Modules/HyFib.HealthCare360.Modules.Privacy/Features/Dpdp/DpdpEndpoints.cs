using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Services;

namespace HyFib.HealthCare360.Modules.Privacy.Features.Dpdp;

/// <summary>
/// The DPDP duties added to the privacy module: the erasure ledger (s.8(7), backups), a guardian's verifiable consent for
/// a child (s.9), consent managers and their artefacts (s.6(7)), and the Significant Data Fiduciary register (s.10).
/// </summary>
internal static class DpdpEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        MapErasureLedger(app);
        MapGuardianConsent(app);
        MapConsentManagers(app);
        MapSdf(app);
    }

    private static void MapErasureLedger(IEndpointRouteBuilder app)
    {
        var ledger = app.MapGroup("/api/v1/privacy/erasure-ledger").WithTags("Privacy");
        ledger.MapGet("/", async (ErasureLedgerReplay replay, CancellationToken ct) => TypedResults.Ok(await replay.StatusAsync(ct)))
            .RequirePermission(PrivacyPermissions.RequestsRead);
        ledger.MapPost("/replay", async (ErasureLedgerReplay replay, CancellationToken ct) =>
                await replay.RunAsync(ct) is { } outcome
                    ? TypedResults.Ok(outcome)
                    : Error.Conflict("privacy.replay_running", "Another server is applying the erasure ledger now. Try again in a minute.").ToProblem())
            .RequirePermission(PrivacyPermissions.RequestsManage);
    }

    private static void MapGuardianConsent(IEndpointRouteBuilder app)
    {
        var guardian = app.MapGroup("/api/v1/privacy/patients/{patientId:guid}/guardian-consent").WithTags("Privacy");
        guardian.MapGet("/", async (Guid patientId, PatientAccessGuard access, GuardianConsentService consents, CancellationToken ct) =>
                await access.EnsureCanOpenAsync(patientId, ct) is { IsFailure: true } denied
                    ? denied.Error!.ToProblem()
                    : await consents.GetAsync(patientId, ct) is { } view ? TypedResults.Ok(view) : Error.NotFound("Patient", patientId).ToProblem())
            .RequireAnyPermission(PrivacyPermissions.RequestsLog, PrivacyPermissions.RequestsRead);
        guardian.MapPost("/", async (Guid patientId, StartGuardianConsentRequest r, PatientAccessGuard access, GuardianConsentService consents, CancellationToken ct) =>
                await access.EnsureCanOpenAsync(patientId, ct) is { IsFailure: true } denied
                    ? denied.Error!.ToProblem()
                    : (await consents.StartAsync(patientId, r, ct)).ToCreated(c => $"/api/v1/privacy/patients/{patientId}/guardian-consent"))
            .RequirePermission(PrivacyPermissions.RequestsLog).WithValidation<StartGuardianConsentRequest>();
        guardian.MapPost("/{consentId:guid}/verify", async (Guid patientId, Guid consentId, VerifyGuardianCodeRequest r, PatientAccessGuard access,
                    GuardianConsentService consents, CancellationToken ct) =>
                await access.EnsureCanOpenAsync(patientId, ct) is { IsFailure: true } denied
                    ? denied.Error!.ToProblem()
                    : (await consents.VerifyAsync(patientId, consentId, r, ct)).ToOk())
            .RequirePermission(PrivacyPermissions.RequestsLog).WithValidation<VerifyGuardianCodeRequest>();
        guardian.MapPost("/{consentId:guid}/revoke", async (Guid patientId, Guid consentId, RevokeGuardianConsentRequest r, PatientAccessGuard access,
                    GuardianConsentService consents, CancellationToken ct) =>
                await access.EnsureCanOpenAsync(patientId, ct) is { IsFailure: true } denied
                    ? denied.Error!.ToProblem()
                    : (await consents.RevokeAsync(patientId, consentId, r, ct)).ToNoContent())
            .RequirePermission(PrivacyPermissions.RequestsLog).WithValidation<RevokeGuardianConsentRequest>();
    }

    private static void MapConsentManagers(IEndpointRouteBuilder app)
    {
        var managers = app.MapGroup("/api/v1/privacy/consent-managers").WithTags("Privacy").RequirePermission(PrivacyPermissions.ConsentManagersManage);
        managers.MapGet("/", async (ConsentManagerService cm, CancellationToken ct) => TypedResults.Ok(await cm.ListAsync(ct)));
        managers.MapPost("/", async (SaveConsentManagerRequest r, ConsentManagerService cm, CancellationToken ct) =>
                (await cm.CreateAsync(r, ct)).ToCreated(c => $"/api/v1/privacy/consent-managers/{c.Id}"))
            .WithValidation<SaveConsentManagerRequest>();
        managers.MapPost("/{id:guid}/deactivate", async (Guid id, ConsentManagerService cm, CancellationToken ct) => (await cm.DeactivateAsync(id, ct)).ToNoContent());
        managers.MapPost("/{id:guid}/simulate", async (Guid id, SimulateConsentEventRequest r, ConsentManagerService cm, CancellationToken ct) =>
                (await cm.SimulateAsync(id, r, ct)).ToOk())
            .WithValidation<SimulateConsentEventRequest>();

        app.MapGet("/api/v1/privacy/patients/{patientId:guid}/consent-artefacts", async (Guid patientId, PatientAccessGuard access, ConsentManagerService cm, CancellationToken ct) =>
                await access.EnsureCanOpenAsync(patientId, ct) is { IsFailure: true } denied ? denied.Error!.ToProblem() : TypedResults.Ok(await cm.ForPatientAsync(patientId, ct)))
            .WithTags("Privacy").RequireAnyPermission(PrivacyPermissions.RequestsRead, PrivacyPermissions.RequestsLog);

        // At the desk, on the patient's word (s.6(4)): withdrawn here, then sent to the consent manager.
        app.MapPost("/api/v1/privacy/consent-artefacts/{id:guid}/withdraw", async (Guid id, ConsentManagerService cm, CancellationToken ct) =>
                (await cm.WithdrawAsync(id, ConsentWithdrawalSource.Staff, null, ct)).ToOk())
            .WithTags("Privacy").RequirePermission(PrivacyPermissions.RequestsLog);

        // The consent managers' own calls: anonymous, and trusted only once their signature verifies.
        app.MapPost("/api/v1/consent-managers/{code}/events", async (string code, HttpRequest request, ConsentManagerService cm, CancellationToken ct) =>
            {
                if (request.ContentLength > ConsentManagerProtocol.MaxBodyBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                using var buffer = new MemoryStream();
                var read = new byte[8192];
                int n;
                while ((n = await request.Body.ReadAsync(read, ct)) > 0)
                {
                    if (buffer.Length + n > ConsentManagerProtocol.MaxBodyBytes)
                    {
                        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    }

                    buffer.Write(read, 0, n);
                }

                var result = await cm.HandleAsync(code, buffer.ToArray(), request.Headers[ConsentManagerProtocol.TimestampHeader].ToString(),
                    request.Headers[ConsentManagerProtocol.SignatureHeader].ToString(), ct);
                return result.IsFailure && result.Error!.Type == ErrorType.Forbidden
                    ? Results.Problem(title: result.Error.Message, statusCode: StatusCodes.Status401Unauthorized, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code })
                    : result.ToOk();
            })
            .WithTags("Privacy")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Partner);

        var portal = app.MapGroup("/api/v1/portal/privacy/consent-artefacts").WithTags("Patient Portal").RequirePermission(PatientsPermissions.Portal)
            .RequireFeature(FeatureKeys.PatientPortal);
        portal.MapGet("/", async (Guid? patientId, PortalPatientScope scope, ConsentManagerService cm, CancellationToken ct) =>
        {
            if (patientId is { } id)
            {
                return await scope.EnsureAccessAsync(id, ct) is { IsFailure: true } denied ? denied.Error!.ToProblem() : TypedResults.Ok(await cm.ForPatientAsync(id, ct));
            }

            return TypedResults.Ok(await cm.ForPatientsAsync(await scope.AccessiblePatientIdsAsync(ct), ct));
        });
        portal.MapPost("/{id:guid}/withdraw", async (Guid id, PortalPatientScope scope, ConsentManagerService cm, CancellationToken ct) =>
        {
            var accessible = (await scope.AccessiblePatientIdsAsync(ct)).ToHashSet();
            return (await cm.WithdrawAsync(id, ConsentWithdrawalSource.Portal, accessible.Contains, ct)).ToOk();
        });
    }

    private static void MapSdf(IEndpointRouteBuilder app)
    {
        var sdf = app.MapGroup("/api/v1/privacy/sdf").WithTags("Privacy").RequirePermission(PrivacyPermissions.SdfManage);
        sdf.MapGet("/", async (SdfService service, CancellationToken ct) => TypedResults.Ok(await service.OverviewAsync(ct)));
        sdf.MapGet("/settings", async (SdfService service, CancellationToken ct) => TypedResults.Ok(await service.SettingsAsync(ct)));
        sdf.MapPut("/settings", async (SdfSettings r, SdfService service, CancellationToken ct) =>
            {
                await service.SaveSettingsAsync(r, ct);
                return TypedResults.NoContent();
            })
            .WithValidation<SdfSettings>().FirstSaveCreates();

        sdf.MapGet("/dpias", async (bool? includeRetired, SdfService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(includeRetired == true, ct)));
        sdf.MapGet("/dpias/{id:guid}", async (Guid id, SdfService service, CancellationToken ct) =>
            await service.DetailAsync(id, ct) is { } detail ? TypedResults.Ok(detail) : Error.NotFound(nameof(Dpia), id).ToProblem());
        sdf.MapPost("/dpias", async (SaveDpiaRequest r, SdfService service, CancellationToken ct) =>
                (await service.CreateAsync(r, ct)).ToCreated(d => $"/api/v1/privacy/sdf/dpias/{d.Id}"))
            .WithValidation<SaveDpiaRequest>();
        sdf.MapPut("/dpias/{id:guid}", async (Guid id, SaveDpiaRequest r, SdfService service, CancellationToken ct) => (await service.UpdateAsync(id, r, ct)).ToOk())
            .WithValidation<SaveDpiaRequest>().RequireVersion();

        // Approved by someone else (202, the approvals inbox): never by whoever sent it.
        sdf.MapPost("/dpias/{id:guid}/submit", async (Guid id, SubmitDpiaRequest r, SdfService service, CancellationToken ct) =>
                (await service.SubmitAsync(id, r, ct)).ToHttpResult(asked => TypedResults.Accepted($"/api/v1/approvals/{asked.Id}", asked)))
            .WithValidation<SubmitDpiaRequest>();
        sdf.MapPost("/dpias/{id:guid}/retire", async (Guid id, RetireDpiaRequest r, SdfService service, CancellationToken ct) => (await service.RetireAsync(id, r, ct)).ToNoContent())
            .WithValidation<RetireDpiaRequest>();

        sdf.MapGet("/audits", async (SdfService service, CancellationToken ct) => TypedResults.Ok(await service.AuditsAsync(ct)));
        sdf.MapPost("/audits", async (SaveDataAuditRequest r, SdfService service, CancellationToken ct) =>
                (await service.SaveAuditAsync(null, r, ct)).ToCreated(a => $"/api/v1/privacy/sdf/audits/{a.Id}"))
            .WithValidation<SaveDataAuditRequest>();
        sdf.MapPut("/audits/{id:guid}", async (Guid id, SaveDataAuditRequest r, SdfService service, CancellationToken ct) => (await service.SaveAuditAsync(id, r, ct)).ToOk())
            .WithValidation<SaveDataAuditRequest>().RequireVersion();
    }
}
