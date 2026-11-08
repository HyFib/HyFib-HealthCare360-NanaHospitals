using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The stays the discharge tests of maker-checker slice 8 close (Expired, Absconded, LAMA, DAMA): a new patient admitted
/// to a bed of their own, the discharge sent and its answer read, and the bed cleaned afterwards, as the run's other tests
/// read the same wards.
/// </summary>
internal static class DischargeStays
{
    public const string Admissions = "/api/v1/inpatient/admissions";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A new patient admitted by the front office under the doctor to a new bed of the men's general ward.</summary>
    public static async Task<(PatientCreated Patient, AdmissionCreated Stay)> AdmitAsync(HospitalServer server, ApiClient frontOffice, ApiClient doctor, bool mlc = false)
    {
        var patient = await BirthsAndDeathsTests.RegisterAsync(frontOffice, Gender.Male, new DateOnly(1950, 1, 1));
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWM");
        var bed = await TestBeds.AddAsync(server, ward.Id);
        var stay = await frontOffice.PostAsync<AdmissionCreated>(Admissions, new AdmitPatientRequest(
            PatientId: patient.Id, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Community-acquired pneumonia", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: mlc, MlcNumber: mlc ? $"MLC/{Random.Shared.Next(10_000, 99_999)}" : null, ExpectedDischargeDate: null, AttendantName: null,
            AttendantRelation: null, AttendantPhone: null, AdmittedAt: null, Notes: null));
        return (patient, stay);
    }

    /// <summary>The nurse initiates the discharge and management lets the patient go with his dues: the stay is cleared by billing.</summary>
    public static async Task InitiateAndClearAsync(HospitalServer server, ApiClient nurse, Guid admissionId)
    {
        await nurse.PostAsync<DischargeResult>($"{Admissions}/{admissionId}/discharge/initiate", new InitiateDischargeRequest(null, null));
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var management = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admissionId}");
        await management.PostAsync($"/api/v1/billing/ip-accounts/{account.Id}/discharge-approval",
            new ApproveDischargeWithDuesRequest("Family to pay at the follow-up visit", account.Version));
    }

    public static string Complete(Guid admissionId) => $"{Admissions}/{admissionId}/discharge/complete";

    /// <summary>The answer is 202 with the request a second person decides.</summary>
    public static async Task<ApprovalRequestDto> AcceptedAsync(ApiClient api, HttpMethod method, string path, object body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return await ApiClient.ReadAsync<ApprovalRequestDto>(response, method, path);
    }

    /// <summary>The answer is a refusal: its status and code.</summary>
    public static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, HttpMethod method, string path, object? body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        response.IsSuccessStatusCode.ShouldBeFalse(await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    /// <summary>The latest audit entry of <paramref name="action"/> about the patient.</summary>
    public static async Task<string> AuditAsync(HospitalServer server, Guid patientId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId && e.Action == action)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => e.Summary ?? string.Empty)
            .FirstAsync(Ct);
    }

    /// <summary>Housekeeping cleans the bed the stay released.</summary>
    public static async Task CleanBedAsync(ApiClient nurse, Guid admissionId)
    {
        var stay = await nurse.GetAsync<AdmissionDetail>($"{Admissions}/{admissionId}");
        var task = (await nurse.GetAsync<List<HousekeepingTaskDto>>($"/api/v1/beds/housekeeping?wardId={stay.WardId}"))
            .Single(t => t.BedId == stay.BedId && t.AdmissionId == stay.Id);
        await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/start");
        await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/complete", new BedNoteRequest("Cleaned"));
    }

    /// <summary>A stay made only for a test, left open by it, is cancelled and its bed cleaned.</summary>
    public static async Task CancelAsync(ApiClient frontOffice, ApiClient nurse, Guid admissionId)
    {
        using (var cancelled = await frontOffice.SendRawAsync(HttpMethod.Post, $"{Admissions}/{admissionId}/cancel", new CancelAdmissionRequest("Admitted only for the test")))
        {
            cancelled.EnsureSuccessStatusCode();
        }

        await CleanBedAsync(nurse, admissionId);
    }
}
