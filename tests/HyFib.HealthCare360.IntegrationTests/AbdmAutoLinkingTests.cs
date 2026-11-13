using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Automatic HIP-initiated linking over the real server, its outbox and a fake ABDM gateway (<see cref="AbdmAutoLinking"/>):
/// a record completed for a patient with an ABHA address is queued with the signature, and the outbox links it without
/// staff, through the same gateway calls and callbacks as staff's links. The outbox delivers at least once, so a job run
/// twice must send nothing the second time; records completed together share one link token; a prescription waits for
/// the doctor's DSC where the hospital requires it; and a patient with no ABHA address, or a VIP's record, is not queued.
/// The ABDM switch is hospital-wide, so these tests are in the feature-switch collection.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class AbdmAutoLinkingTests(HospitalServer server)
{
    private const string TokenPath = "/v3/token/generate-token";
    private const string CareContextPath = "/hip/v3/link/carecontext";

    /// <summary>The outbox takes a due message within its two-second poll; this allows for a busy test server.</summary>
    private static readonly TimeSpan OutboxWait = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeAbdmGateway Gateway => server.AbdmGateway;

    [Fact]
    public async Task A_signed_consultation_is_linked_without_staff_and_a_second_delivery_of_its_job_sends_nothing()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var medicalRecords = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var address = "anjali." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true, autoLink: true);

            // Signing queues one job, committed with the signature.
            var consultation = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: true);
            var queued = (await JobsAsync(consultation)).ShouldHaveSingleItem();
            queued.Read<AbdmAutoLinkJob>().ShouldBe(new AbdmAutoLinkJob(AbdmRecordType.OpConsultation, consultation, patient.Id));
            queued.ExpiresAt.ShouldNotBeNull().ShouldBeGreaterThan(queued.CreatedAt + TimeSpan.FromDays(1));

            // The outbox runs it: the gateway is asked for a link token for the address, under a request no one made.
            var tokenCall = await WaitForAsync(() => TokenCallsFor(address).SingleOrDefault());
            var request = (await RequestsAsync(patient.Id)).ShouldHaveSingleItem();
            (request.Status, request.TokenRequestId?.ToString("D"), request.CreatedBy).ShouldBe((AbdmLinkStatus.AwaitingToken, tokenCall.Header("REQUEST-ID"), (Guid?)null));
            var audit = (await AuditAsync(patient.Id, AbdmLinkingService.LinkRequested)).ShouldNotBeNull();
            (audit.EntityId, audit.UserId).ShouldBe((request.Id.ToString("D"), (Guid?)null));
            audit.Summary.ShouldNotBeNull().ShouldContain("automatically");

            // The gateway's answers link it, as they do staff's requests; staff see where it has got to.
            var linkToken = "it-auto-token-" + AbdmExportTests.Letters(12);
            await CallBackAsync(AbdmGatewayCallbackEndpoints.OnGenerateTokenPath, new { abhaAddress = address, linkToken, response = new { requestId = tokenCall.Header("REQUEST-ID") } });
            var linkCall = CareContextCallsWith(linkToken).ShouldHaveSingleItem();
            linkCall.Json().GetProperty("patient")[0].GetProperty("careContexts")[0].GetProperty("referenceNumber").GetString()
                .ShouldBe($"op-consultation/{consultation}");
            await CallBackAsync(AbdmGatewayCallbackEndpoints.OnAddCareContextsPath,
                new { abhaAddress = address, status = "SUCCESS", response = new { requestId = linkCall.Header("REQUEST-ID") } });
            var seen = await medicalRecords.GetAsync<AbdmLinkView>($"/api/v1/abdm/links/{request.Id}");
            (seen.Status, seen.Automatic, seen.Retryable).ShouldBe((AbdmLinkStatus.Linked, true, false));
            (await medicalRecords.GetAsync<List<AbdmLinkView>>($"/api/v1/abdm/patients/{patient.Id}/links")).ShouldHaveSingleItem().Id.ShouldBe(request.Id);

            // The outbox delivers at least once: the same job run again finds the record linked, and sends nothing.
            await DeliverAgainAsync(queued.Id);
            TokenCallsFor(address).Count.ShouldBe(1);
            CareContextCallsWith(linkToken).Count.ShouldBe(1);
            (await RequestsAsync(patient.Id)).Count.ShouldBe(1);
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task Records_completed_together_wait_for_one_link_token_rather_than_each_asking_for_their_own()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var address = "rohit." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true, autoLink: true);

            var first = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: true);
            var second = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: true);

            // One token is asked for; the other record's job waits for it, queued again for later.
            var tokenCall = await WaitForAsync(() => TokenCallsFor(address).SingleOrDefault());
            var waiting = await WaitForAsync(async () => (await JobsAsync(first, second)).FirstOrDefault(m => m.Status == OutboxStatus.Pending));
            waiting.NextAttemptAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10));

            // The token comes: the first record goes with it, and the waiting one, run now, goes with the same token.
            var linkToken = "it-shared-token-" + AbdmExportTests.Letters(12);
            await CallBackAsync(AbdmGatewayCallbackEndpoints.OnGenerateTokenPath, new { abhaAddress = address, linkToken, response = new { requestId = tokenCall.Header("REQUEST-ID") } });
            await RunNowAsync(waiting.Id);
            await WaitUntilAsync(() => CareContextCallsWith(linkToken).Count == 2);

            TokenCallsFor(address).Count.ShouldBe(1);
            CareContextCallsWith(linkToken).Select(c => c.Json().GetProperty("patient")[0].GetProperty("careContexts")[0].GetProperty("referenceNumber").GetString())
                .ShouldBe([$"op-consultation/{first}", $"op-consultation/{second}"], ignoreOrder: true);
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task A_prescription_waits_for_the_doctors_dsc_where_the_hospital_requires_one()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var address = "farhan." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));
        var consultation = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: false);
        var paracetamol = await AbdmExportTests.DrugAsync(doctor, "Paracetamol", "DRG-PCM500");
        var prescription = await AbdmExportTests.PrescribeAsync(doctor, patient.Id, consultation,
            AbdmExportTests.Tablet(paracetamol, DoseFrequency.TDS, days: 3, "After food"));
        var facilityId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true, autoLink: true);
            await DscTestPki.SetPolicyAsync(server, facilityId, "required", Ct);
            await AbdmExportTests.SignAsync(doctor, prescription);

            // Signed without the DSC the hospital requires, it may not be issued yet: the job waits, and nothing is sent.
            var waiting = await WaitForAsync(async () => (await JobsAsync(prescription.Id)).FirstOrDefault(m => m.Status == OutboxStatus.Pending && m.Attempts == 0
                && m.NextAttemptAt > DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5)));
            TokenCallsFor(address).ShouldBeEmpty();

            // Once it may be issued (here, the DSC no longer required), the job links it.
            await DscTestPki.SetPolicyAsync(server, facilityId, null, Ct);
            await RunNowAsync(waiting.Id);
            await WaitForAsync(() => TokenCallsFor(address).SingleOrDefault());
            var request = (await RequestsAsync(patient.Id)).ShouldHaveSingleItem();
            AbdmLinkingService.Records(request.CareContextsJson).ShouldHaveSingleItem().ReferenceNumber.ShouldBe($"prescription/{prescription.Id}");
        }
        finally
        {
            await DscTestPki.SetPolicyAsync(server, facilityId, null, Ct);
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task Nothing_is_queued_for_a_patient_without_an_abha_address_for_a_vip_or_while_automatic_linking_is_off()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var noAddress = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), null));
        var vip = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients",
            AbdmExportTests.Registration(AbdmExportTests.NewAbha(), "vip." + AbdmExportTests.Letters(8) + "@sbx") with { IsVip = true });
        var address = "suresh." + AbdmExportTests.Letters(8) + "@sbx";
        var ordinary = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true, autoLink: true);
            (await JobsAsync(await AbdmExportTests.ConsultAsync(doctor, noAddress.Id, sign: true))).ShouldBeEmpty();

            // A VIP's record opens to the doctor only through break-the-glass; it is not linked without staff either way.
            await doctor.PostAsync<BreakGlassGrant>($"/api/v1/patients/{vip.Id}/break-glass", new BreakGlassRequest("Collapsed at the OPD, needs the last consultation"));
            (await JobsAsync(await AbdmExportTests.ConsultAsync(doctor, vip.Id, sign: true))).ShouldBeEmpty();

            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true, autoLink: false);
            (await JobsAsync(await AbdmExportTests.ConsultAsync(doctor, ordinary.Id, sign: true))).ShouldBeEmpty();
            TokenCallsFor(address).ShouldBeEmpty();
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    private List<GatewayCall> TokenCallsFor(string address) =>
        [.. Gateway.CallsTo(TokenPath).Where(c => c.Json().GetProperty("abhaAddress").GetString() == address)];

    private List<GatewayCall> CareContextCallsWith(string linkToken) =>
        [.. Gateway.CallsTo(CareContextPath).Where(c => c.Header("X-LINK-TOKEN") == linkToken)];

    private async Task CallBackAsync(string path, object body)
    {
        using var answer = await AbdmLinkingTests.CallBackAsync(server.Anonymous(ownAddress: true), path, body, Gateway.Token());
        answer.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    /// <summary>The automatic-linking jobs of these records, oldest first, whatever their status.</summary>
    private async Task<List<OutboxMessage>> JobsAsync(params Guid[] recordIds)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == AbdmAutoLinking.MessageType)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(Ct);
        return [.. messages.Where(m => recordIds.Contains(m.Read<AbdmAutoLinkJob>().RecordId))];
    }

    private async Task<List<AbdmLinkRequest>> RequestsAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AbdmLinkRequest>()
            .IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => r.PatientId == patientId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(Ct);
    }

    /// <summary>Makes a waiting message due now, so the outbox takes it at its next poll.</summary>
    private async Task RunNowAsync(Guid messageId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>()
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(m => m.SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow), Ct);
    }

    /// <summary>Hands a delivered message to the outbox once more, as a dispatcher that failed before recording its delivery would.</summary>
    private async Task DeliverAgainAsync(Guid messageId)
    {
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>()
                .Where(m => m.Id == messageId)
                .ExecuteUpdateAsync(m => m
                    .SetProperty(x => x.Status, OutboxStatus.Pending)
                    .SetProperty(x => x.ProcessedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow), Ct);
        }

        await WaitForAsync(async () =>
        {
            await using var scope = server.Services.CreateAsyncScope();
            var message = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking()
                .FirstAsync(m => m.Id == messageId, Ct);
            return message.Status == OutboxStatus.Succeeded ? message : null;
        });
    }

    private async Task<AuditEvent?> AuditAsync(Guid patientId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.PatientId == patientId && a.Action == action)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(Ct);
    }

    private async Task SwitchAsync(bool enabled)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        (await admin.SetFeatureAsync(FeatureKeys.Abdm, new SetFeatureRequest(enabled))).Enabled.ShouldBe(enabled);
    }

    /// <summary>What <paramref name="probe"/> finds once it finds something, asked again until <see cref="OutboxWait"/> has passed.</summary>
    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe)
        where T : class
    {
        var until = DateTimeOffset.UtcNow + OutboxWait;
        while (true)
        {
            if (await probe() is { } found)
            {
                return found;
            }

            if (DateTimeOffset.UtcNow > until)
            {
                throw new TimeoutException($"Nothing came within {OutboxWait.TotalSeconds:0} s.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }
    }

    private static Task<T> WaitForAsync<T>(Func<T?> probe)
        where T : class =>
        WaitForAsync(() => Task.FromResult(probe()));

    private static Task WaitUntilAsync(Func<bool> condition) =>
        WaitForAsync(() => Task.FromResult(condition() ? "yes" : null));
}
