using System.Text.Json;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Features.Mar;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A discharge ends the stay's medicines. In the production-style run, a patient's ceftriaxone line stayed active after
/// her discharge and the next morning's dose stayed due on the MAR, a legal record, for a patient who had gone home. Now
/// the stay's lines stop and the doses from the discharge on are cancelled; a dose that fell due before it stays for the
/// nurse to document, as when a line is stopped by hand.
/// </summary>
public sealed class DischargeMedicationTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_discharge_stops_the_stays_medicines_and_cancels_the_doses_still_to_come()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patient.Id);
        var orders = $"/api/v1/nursing/admissions/{admission.Id}/medication-orders";

        // Four doses a day from now on, and one given at once: its dose is due before the discharge.
        var qid = await nurse.PostAsync<MedicationOrderDto>(orders, Order("QID"));
        var stat = await nurse.PostAsync<MedicationOrderDto>(orders, Order("STAT"));
        var before = await DosesAsync(admission.Id);
        before.Where(d => d.MedicationOrderId == qid.Id).ShouldAllBe(d => d.Status == MarStatus.Due);
        before.Count(d => d.MedicationOrderId == qid.Id).ShouldBeGreaterThan(0);
        before.ShouldContain(d => d.MedicationOrderId == stat.Id && d.Status == MarStatus.Due);

        // She leaves the ward unseen: recorded at once, without billing clearance.
        await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/complete",
            new CompleteDischargeRequest(DischargeType.Absconded, DischargedAt: null, "Not in the ward at the 6 pm round; security informed.", NotifyPatient: false));

        var stopped = await nurse.GetAsync<List<MedicationOrderDto>>($"{orders}?activeOnly=false");
        stopped.Select(o => o.Id).ShouldBe([qid.Id, stat.Id], ignoreOrder: true);
        stopped.ShouldAllBe(o => o.Status == MedicationOrderStatus.Stopped && o.StopReason == "Patient discharged");

        var after = await DosesAsync(admission.Id);
        after.Where(d => d.MedicationOrderId == qid.Id).ShouldAllBe(d => d.Status == MarStatus.Cancelled);
        after.Single(d => d.MedicationOrderId == stat.Id).Status.ShouldBe(MarStatus.Due, "a dose due before the discharge is the nurse's to document");

        // The discharge's handler changes them in the discharge's own save, so the data audit records them as it records
        // any change: each line's stop and each cancelled dose is in the audit trail with its reason.
        var auditor = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        foreach (var order in stopped)
        {
            (await LastUpdateAsync(auditor, nameof(MedicationOrder), order.Id)).GetProperty("stopReason").GetProperty("to").GetString()
                .ShouldBe("Patient discharged");
        }

        foreach (var dose in after.Where(d => d.MedicationOrderId == qid.Id))
        {
            (await LastUpdateAsync(auditor, nameof(MedicationAdministration), dose.Id)).GetProperty("reason").GetProperty("to").GetString()
                .ShouldBe("Patient discharged");
        }

        // The chart stays open 24 hours for late entries, but nothing becomes due again: no new line, no dose to give.
        await ShouldBeRefusedAsync(nurse, orders, Order("QID"));
        var adHoc = $"/api/v1/nursing/admissions/{admission.Id}/mar";
        await ShouldBeRefusedAsync(nurse, adHoc, AdHoc(givenNow: false, at: null));
        await ShouldBeRefusedAsync(nurse, adHoc, AdHoc(givenNow: true, at: null));
        (await DosesAsync(admission.Id)).Count.ShouldBe(after.Count);

        // A dose given before the discharge can still be recorded.
        var stay = await nurse.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admission.Id}");
        await nurse.PostAsync<MarEntryDto>(adHoc, AdHoc(givenNow: true, at: stay.DischargedAt!.Value.AddMinutes(-30)));
        (await DosesAsync(admission.Id)).ShouldContain(d => d.IsAdHoc && d.Status == MarStatus.Given);

        await CleanBedAsync(nurse, admission.Id);
    }

    /// <summary>The details of the newest update in a record's audit trail, as Administration › Audit log shows it.</summary>
    private static async Task<JsonElement> LastUpdateAsync(ApiClient auditor, string entityType, Guid id)
    {
        var trail = await auditor.GetAsync<PagedResult<AuditEventDto>>($"/api/v1/admin/audit?entityType={entityType}&entityId={id}&pageSize=50");
        return JsonSerializer.Deserialize<JsonElement>(trail.Items.First(e => e.Action == entityType + ".update").Details!);
    }

    private static AddAdHocDoseRequest AdHoc(bool givenNow, DateTimeOffset? at) => new(
        MedicationOrderId: null, DrugName: "Ondansetron 4 mg Injection", Dose: "4 mg", Route: "IV", ScheduledAt: at, GivenNow: givenNow,
        Reason: null, BatchNumber: null, WitnessName: null, Notes: null);

    private static async Task ShouldBeRefusedAsync(ApiClient nurse, string path, object body)
    {
        using var refused = await nurse.SendRawAsync(HttpMethod.Post, path, body);
        refused.StatusCode.ShouldBe(System.Net.HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("medication.discharged");
    }

    private static AddMedicationOrderRequest Order(string frequency) => new(
        DrugItemId: null, DrugName: "Pantoprazole 40 mg Injection", GenericName: "Pantoprazole", Strength: "40 mg", Dose: "40 mg", Route: "IV",
        Frequency: frequency, DurationDays: 3, StartAt: null, Instructions: null, IsControlledDrug: false, OrderedByPractitionerId: null);

    private async Task<List<MedicationAdministration>> DosesAsync(Guid admissionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<MedicationAdministration>()
            .IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .AsNoTracking()
            .Where(d => d.AdmissionId == admissionId)
            .ToListAsync(Ct);
    }

    private static async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var bed = (await frontOffice.GetAsync<List<AvailableBedDto>>($"/api/v1/beds/available?patientId={patientId}"))
            .First(b => b.IsEligible && b.Status == BedStatus.Vacant);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Observation", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    /// <summary>Housekeeping cleans the bed the discharge released: the wards' beds are shared by every test of the run.</summary>
    private static async Task CleanBedAsync(ApiClient nurse, Guid admissionId)
    {
        var stay = await nurse.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admissionId}");
        var task = (await nurse.GetAsync<List<HousekeepingTaskDto>>($"/api/v1/beds/housekeeping?wardId={stay.WardId}"))
            .Single(t => t.BedId == stay.BedId && t.AdmissionId == stay.Id);
        await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/start");
        (await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/complete", new BedNoteRequest("Cleaned")))
            .Status.ShouldBe(HousekeepingStatus.Completed);
    }
}
