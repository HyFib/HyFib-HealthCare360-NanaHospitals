using System.Net;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Mar;
using HyFib.HealthCare360.Modules.Inpatient.Features.Nursing;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain.Audits;
using HyFib.HealthCare360.Modules.Quality.Domain.Hai;
using HyFib.HealthCare360.Modules.Quality.Features.Audits;
using HyFib.HealthCare360.Modules.Quality.Features.Capas;
using HyFib.HealthCare360.Modules.Quality.Features.Hai;
using HyFib.HealthCare360.Modules.Quality.Features.Indicators;
using HyFib.HealthCare360.Modules.Quality.Services;
using HyFib.HealthCare360.Modules.Quality.Services.Indicators;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using static HyFib.HealthCare360.IntegrationTests.DischargeStays;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The Quality workstream over the real API on the seeded hospital: falls, pressure injuries and missed doses raise incidents
/// and count in the computed NABH indicators (with the records behind them); a device-associated infection raised from a
/// positive culture is confirmed only against the NHSN checklist and counts per 1,000 device days; and an internal audit
/// closes only when its CAPAs are verified by someone other than their owner. Other tests write to the same hospital, so
/// the checks look for this test's own records in the drill-down rather than at the totals.
/// </summary>
public sealed class QualitySurveillanceTests(HospitalServer server)
{
    private const string Indicators = "/api/v1/quality/indicators";
    private const string Hai = "/api/v1/quality/hai";
    private const string Audits = "/api/v1/quality/audits";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

    [Fact]
    public async Task Falls_pressure_injuries_and_missed_doses_raise_incidents_and_count_in_the_computed_indicators()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var quality = await server.SignInAsync(HospitalServer.Users.QualityManager);
        var (_, stay) = await AdmitAsync(server, frontOffice, doctor);
        var now = DateTimeOffset.UtcNow;
        await MoveAdmissionBackAsync(stay.Id, TimeSpan.FromHours(6));

        // A fall raises an incident in the register; a pressure injury present on admission is documented and raises none.
        var fall = await nurse.PostAsync<NursingSafetyEventDto>($"/api/v1/nursing/admissions/{stay.Id}/safety-events", new RecordSafetyEventRequest(
            NursingSafetyEventKind.Fall, now.AddMinutes(-30), "Ward GWM, bathroom", "Found on the floor beside the commode, alert, no visible injury.",
            "Doctor informed, observations every hour", FallInjury.Minor, Witnessed: false, null, null, false));
        fall.IncidentNumber.ShouldNotBeNull();
        var onAdmission = await nurse.PostAsync<NursingSafetyEventDto>($"/api/v1/nursing/admissions/{stay.Id}/safety-events", new RecordSafetyEventRequest(
            NursingSafetyEventKind.PressureInjury, now.AddMinutes(-20), "Ward GWM", "Reddened sacrum noted at the admission skin check.",
            null, null, null, PressureInjuryStage.Stage1, "Sacrum", PresentOnAdmission: true));
        onAdmission.IncidentNumber.ShouldBeNull();
        var acquired = await nurse.PostAsync<NursingSafetyEventDto>($"/api/v1/nursing/admissions/{stay.Id}/safety-events", new RecordSafetyEventRequest(
            NursingSafetyEventKind.PressureInjury, now.AddMinutes(-10), "Ward GWM", "Blister over the right heel found at the evening turn.",
            "Heel offloaded", null, null, PressureInjuryStage.Stage2, "Right heel", PresentOnAdmission: false));
        acquired.IncidentNumber.ShouldNotBeNull();

        // A pressure injury needs its stage; a fall cannot be present on admission.
        (await RefuseAsync(nurse, HttpMethod.Post, $"/api/v1/nursing/admissions/{stay.Id}/safety-events", new RecordSafetyEventRequest(
            NursingSafetyEventKind.PressureInjury, now, "Ward", "No stage given at all here.", null, null, null, null, "Sacrum", false))).Status
            .ShouldBe(HttpStatusCode.BadRequest);

        // A dose recorded as missed is an error of omission: it raises a medication-error incident; a refusal does not.
        var dose = await nurse.PostAsync<MarEntryDto>($"/api/v1/nursing/admissions/{stay.Id}/mar",
            new AddAdHocDoseRequest(null, "Paracetamol 500 mg Tablet", "1 tablet", "Oral", now.AddHours(-2), false, null, null, null, null));
        await nurse.PostAsync<MarEntryDto>($"/api/v1/nursing/mar/{dose.Id}/record",
            new RecordAdministrationRequest(MarStatus.Missed, null, "Patient was in radiology at the round", null, null, null, dose.Version));
        var refused = await nurse.PostAsync<MarEntryDto>($"/api/v1/nursing/admissions/{stay.Id}/mar",
            new AddAdHocDoseRequest(null, "Paracetamol 500 mg Tablet", "1 tablet", "Oral", now.AddHours(-1), false, null, null, null, null));
        await nurse.PostAsync<MarEntryDto>($"/api/v1/nursing/mar/{refused.Id}/record",
            new RecordAdministrationRequest(MarStatus.Refused, null, "Patient declined", null, null, null, refused.Version));

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var raised = await db.Set<Incident>().IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.SourceRecordId == fall.Id || i.SourceRecordId == acquired.Id || i.SourceRecordId == dose.Id || i.SourceRecordId == refused.Id
                            || i.SourceRecordId == onAdmission.Id)
                .ToListAsync(Ct);
            raised.Select(i => (i.Type, i.Severity, i.SourceModule)).ShouldBe(
                [(IncidentType.PatientFall, IncidentSeverity.Mild, "Inpatient"), (IncidentType.PressureInjury, IncidentSeverity.Mild, "Inpatient"),
                    (IncidentType.MedicationError, IncidentSeverity.NoHarm, "Inpatient")],
                ignoreOrder: true);
            raised.ShouldAllBe(i => i.PatientId != null && !i.IsSentinel && i.Status == IncidentStatus.Reported);

            // One record raises one incident, however often it is reported.
            var register = scope.ServiceProvider.GetRequiredService<IIncidentRegister>();
            var again = await register.ReportAsync(new ReportedSafetyEvent(raised[0].FacilityId, raised[0].PatientId!.Value, SafetyEventKind.PatientFall,
                IncidentHarm.Mild, now, "Ward", "The same fall reported again", null, "Inpatient", nameof(NursingSafetyEvent), fall.Id, "Fall"), Ct);
            again.Value.IncidentNumber.ShouldBe(fall.IncidentNumber);
        }

        // The quality team computes the month: falls and pressure injuries per 1,000 patient days, medication errors too.
        var list = await quality.GetAsync<List<IndicatorDto>>(Indicators);
        var fallRate = list.Single(i => i.Code == "FALL-RATE");
        var ulcers = list.Single(i => i.Code == "PRESSURE-ULCER");
        var medErrors = list.Single(i => i.Code == "MED-ERR-1000");
        new[] { fallRate, ulcers, medErrors }.ShouldAllBe(i => i.IsComputed);
        list.Single(i => i.Code == "HAND-HYG").IsComputed.ShouldBeFalse("HC360 has no hand-hygiene observations: entered by hand");

        var computed = await quality.PostAsync<List<ComputedIndicator>>($"{Indicators}/compute", new ComputeIndicatorsRequest(Today));
        computed.Single(c => c.Code == "FALL-RATE").Outcome.ShouldBe(ComputeOutcome.Stored);
        computed.ShouldNotContain(c => c.Code == "HAND-HYG");

        var falls = await quality.GetAsync<IndicatorDrilldown>($"{Indicators}/{fallRate.Id}/drilldown?period={Today:yyyy-MM-dd}");
        falls.Records.ShouldContain(r => r.Role == MeasureRole.Numerator && r.Reference == $"Admission {stay.AdmissionNumber}");
        falls.Denominator.ShouldBeGreaterThan(0);
        falls.Note!.ShouldContain("in-patient days");
        falls.Stored.ShouldNotBeNull().IsComputed.ShouldBeTrue();

        var pressure = await quality.GetAsync<IndicatorDrilldown>($"{Indicators}/{ulcers.Id}/drilldown?period={Today:yyyy-MM-dd}");
        var mine = pressure.Records.Where(r => r.Reference == $"Admission {stay.AdmissionNumber}").ToList();
        mine.Select(r => r.Role).ShouldBe([MeasureRole.Excluded, MeasureRole.Numerator], ignoreOrder: true);

        var errors = await quality.GetAsync<IndicatorDrilldown>($"{Indicators}/{medErrors.Id}/drilldown?period={Today:yyyy-MM-dd}");
        errors.Records.ShouldContain(r => r.Role == MeasureRole.Numerator && r.Detail!.Contains("Inpatient", StringComparison.Ordinal));

        // A computed indicator takes no value by hand; one HC360 cannot compute does, and its hand value is never overwritten.
        (await RefuseAsync(quality, HttpMethod.Put, $"{Indicators}/{fallRate.Id}/values", new RecordIndicatorValueRequest(Today, 1, 100, null))).Code
            .ShouldBe("indicator.computed");
        (await RefuseAsync(quality, HttpMethod.Get, $"{Indicators}/{list.Single(i => i.Code == "HAND-HYG").Id}/drilldown", null)).Code
            .ShouldBe("indicator.not_computed");
    }

    [Fact]
    public async Task A_positive_culture_on_a_catheterised_patient_alerts_infection_control_and_counts_only_once_it_meets_the_definition()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var quality = await server.SignInAsync(HospitalServer.Users.QualityManager);
        var (patient, stay) = await AdmitAsync(server, frontOffice, doctor);
        var now = DateTimeOffset.UtcNow;
        await MoveAdmissionBackAsync(stay.Id, TimeSpan.FromDays(6));

        // The nurse records the urinary catheter put in five days ago; the front office cannot.
        (await RefuseAsync(frontOffice, HttpMethod.Post, $"{Hai}/devices",
            new InsertDeviceRequest(null, stay.AdmissionNumber, InvasiveDevice.UrinaryCatheter, "Foley 14 Fr", now.AddDays(-5), null))).Status.ShouldBe(HttpStatusCode.Forbidden);
        var catheter = await nurse.PostAsync<DeviceEpisodeDto>($"{Hai}/devices",
            new InsertDeviceRequest(null, stay.AdmissionNumber, InvasiveDevice.UrinaryCatheter, "Foley 14 Fr", now.AddDays(-5), null));
        catheter.DeviceDays.ShouldBe(6);
        (await nurse.GetAsync<List<DeviceEpisodeDto>>($"{Hai}/devices?inPlace=true")).ShouldContain(d => d.Id == catheter.Id);

        // The laboratory verifies a urine culture that grew E. coli: the scan raises a suspected CAUTI and alerts the team.
        var collectedAt = now.AddHours(-3);
        await VerifiedUrineCultureAsync(patient.Id, stay.Id, collectedAt, "Escherichia coli");
        var dayOfDevice = QualityClock.LocalDate(collectedAt).DayNumber - QualityClock.LocalDate(now.AddDays(-5)).DayNumber + 1;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<HaiService>().ScanCulturesAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);
            (await scope.ServiceProvider.GetRequiredService<HaiService>().ScanCulturesAsync(Ct)).ShouldBe(0, "a culture raises its case once");
        }

        var cases = await quality.GetAsync<List<HaiCaseListItem>>($"{Hai}/cases?status=Suspected&type=Cauti");
        var listed = cases.Single(c => c.Uhid == patient.Uhid);
        listed.Origin.ShouldBe(HaiCaseOrigin.CultureAlert);
        var alert = (await quality.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=100")).Items;
        alert.ShouldContain(n => n.Title.Contains(listed.CaseNumber, StringComparison.Ordinal));

        var detail = await quality.GetAsync<HaiCaseDetail>($"{Hai}/cases/{listed.Id}");
        (detail.Device!.Id, detail.DeviceEligible, detail.DayOfDevice, detail.MeetsDefinition).ShouldBe((catheter.Id, true, dayOfDevice, false));

        // Not confirmed while the checklist misses the definition; dysuria does not count while the catheter is in.
        (await RefuseAsync(quality, HttpMethod.Post, $"{Hai}/cases/{listed.Id}/confirm", new DecideHaiCaseRequest(null, detail.Version))).Code
            .ShouldBe("hai.definition_not_met");
        var reviewed = await quality.PutAsync<HaiCaseDetail>($"{Hai}/cases/{listed.Id}",
            new ReviewHaiCaseRequest(["culture", "dysuria"], detail.EventOn, catheter.Id, null, null, null, null, detail.Version));
        reviewed.MeetsDefinition.ShouldBeFalse();
        reviewed = await quality.PutAsync<HaiCaseDetail>($"{Hai}/cases/{listed.Id}",
            new ReviewHaiCaseRequest(["culture", "fever"], detail.EventOn, catheter.Id, null, null, null, "Fever 38.6 °C on the day", reviewed.Version));
        reviewed.MeetsDefinition.ShouldBeTrue();
        var confirmed = await quality.PostAsync<HaiCaseDetail>($"{Hai}/cases/{listed.Id}/confirm", new DecideHaiCaseRequest("Meets SUTI 1a", reviewed.Version));
        confirmed.Status.ShouldBe(HaiCaseStatus.Confirmed);

        // It counts in this month's CAUTI rate and the catheter days in its denominator.
        var rates = await quality.GetAsync<List<HaiRateRow>>($"{Hai}/rates?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        rates.Single().Cauti.ShouldBeGreaterThanOrEqualTo(1);
        rates.Single().UrinaryCatheterDays.ShouldBeGreaterThanOrEqualTo(Math.Min(6, Today.Day));
        var cauti = (await quality.GetAsync<List<IndicatorDto>>(Indicators)).Single(i => i.Code == "CAUTI");
        var drill = await quality.GetAsync<IndicatorDrilldown>($"{Indicators}/{cauti.Id}/drilldown?period={Today:yyyy-MM-dd}");
        drill.Records.ShouldContain(r => r.Reference == confirmed.CaseNumber && r.Uhid == patient.Uhid);

        // The catheter comes out; it cannot come out twice.
        var removed = await nurse.PostAsync<DeviceEpisodeDto>($"{Hai}/devices/{catheter.Id}/remove", new RemoveDeviceRequest(now, DeviceRemovalReason.Planned, catheter.Version));
        removed.RemovedAt.ShouldNotBeNull();
        (await RefuseAsync(nurse, HttpMethod.Post, $"{Hai}/devices/{catheter.Id}/remove", new RemoveDeviceRequest(now, DeviceRemovalReason.Planned, removed.Version))).Code
            .ShouldBe("hai.device_removed");
    }

    [Fact]
    public async Task An_internal_audit_closes_only_when_its_capas_are_verified_by_someone_other_than_their_owner()
    {
        var quality = await server.SignInAsync(HospitalServer.Users.QualityManager);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var owner = await server.SignInAsync(HospitalServer.Users.SecondNurse);
        var nurseId = (await nurse.GetAsync<AuthUser>("/api/v1/auth/me")).Id;
        var ownerId = (await owner.GetAsync<AuthUser>("/api/v1/auth/me")).Id;

        var templates = await quality.GetAsync<List<AuditTemplateDto>>($"{Audits}/templates?chapter=Hic");
        var hic = templates.Single(t => t.Code == "HIC-BASIC");
        hic.Items.Count.ShouldBeGreaterThan(3);
        (await quality.GetAsync<List<AuditTemplateDto>>($"{Audits}/templates")).Select(t => t.Chapter).Distinct().Count().ShouldBe(10);

        // The quality manager schedules a monthly audit led by the ward nurse, who records its findings.
        var audit = await quality.PostAsync<AuditDetail>(Audits, new ScheduleAuditRequest(hic.Id, Today, nurseId, null, "Ward GWM", AuditRecurrence.Monthly));
        (await nurse.GetAsync<List<AuditListItem>>($"{Audits}/mine")).ShouldContain(a => a.Id == audit.Id);
        (await RefuseAsync(owner, HttpMethod.Get, $"{Audits}/{audit.Id}", null)).Status.ShouldBe(HttpStatusCode.Forbidden);

        foreach (var finding in audit.Findings.Skip(1))
        {
            audit = await nurse.PostAsync<AuditDetail>($"{Audits}/{audit.Id}/findings", new RecordFindingRequest(finding.ItemKey, FindingResult.Compliant, null, audit.Version));
        }

        var gap = audit.Findings[0];
        audit = await nurse.PostAsync<AuditDetail>($"{Audits}/{audit.Id}/findings",
            new RecordFindingRequest(gap.ItemKey, FindingResult.NonCompliant, "Hand-rub dispensers empty at 3 of 12 beds", audit.Version));
        audit.Status.ShouldBe(AuditStatus.InProgress);
        (await RefuseAsync(nurse, HttpMethod.Post, $"{Audits}/{audit.Id}/complete", new CompleteAuditRequest(null, audit.Version))).Code.ShouldBe("audit.incomplete");

        // The quality team raises the CAPA for the non-compliance, owned by the second nurse; then the audit completes and the next is scheduled.
        (await RefuseAsync(nurse, HttpMethod.Post, $"{Audits}/{audit.Id}/findings/{gap.Id}/capa",
            new RaiseFindingCapaRequest(CapaType.Corrective, "Refill and check dispensers each shift", ownerId, Today.AddDays(7), audit.Version))).Status
            .ShouldBe(HttpStatusCode.Forbidden);
        audit = await quality.PostAsync<AuditDetail>($"{Audits}/{audit.Id}/findings/{gap.Id}/capa",
            new RaiseFindingCapaRequest(CapaType.Corrective, "Refill and check dispensers each shift", ownerId, Today.AddDays(7), audit.Version));
        var capaId = audit.Findings.Single(f => f.Id == gap.Id).CapaId!.Value;
        audit = await nurse.PostAsync<AuditDetail>($"{Audits}/{audit.Id}/complete", new CompleteAuditRequest("One gap in hand hygiene supplies", audit.Version));
        audit.Status.ShouldBe(AuditStatus.Completed);
        audit.CompliancePercent.ShouldBe(Math.Round((hic.Items.Count - 1) * 100m / hic.Items.Count, 1, MidpointRounding.AwayFromZero));
        var next = await quality.GetAsync<AuditDetail>($"{Audits}/{audit.NextAuditId}");
        (next.ScheduledOn, next.Status).ShouldBe((Today.AddMonths(1), AuditStatus.Scheduled));

        // Not closed while the CAPA is open; the owner does it but cannot verify it; the quality manager verifies, then closes.
        (await RefuseAsync(quality, HttpMethod.Post, $"{Audits}/{audit.Id}/close", new AuditStepRequest(audit.Version))).Code.ShouldBe("audit.capas_open");
        await owner.PostAsync($"/api/v1/quality/capas/{capaId}/start", new { });
        await owner.PostAsync($"/api/v1/quality/capas/{capaId}/complete", new CompleteCapaRequest("Dispensers refilled; checklist on each trolley"));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            // The owner holding the quality role still could not verify their own action: the CAPA register refuses it.
            var capa = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Capa>().IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == capaId, Ct);
            capa.Verify(true, "self", ownerId, DateTimeOffset.UtcNow).Error!.Code.ShouldBe("capa.self_verification");
        }

        await quality.PostAsync($"/api/v1/quality/capas/{capaId}/verify", new VerifyCapaRequest(true, "Spot check of 12 beds: all dispensers full"));
        audit = await quality.PostAsync<AuditDetail>($"{Audits}/{audit.Id}/close", new AuditStepRequest(audit.Version));
        audit.Status.ShouldBe(AuditStatus.Closed);
    }

    /// <summary>The answer is a refusal: its status and, when it has a body, its code.</summary>
    private static async Task<(HttpStatusCode Status, string? Code)> RefuseAsync(ApiClient api, HttpMethod method, string path, object? body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeFalse(text);
        if (text.Length == 0)
        {
            return (response.StatusCode, null);
        }

        var problem = JsonSerializer.Deserialize<JsonElement>(text, ApiClient.Json);
        return (response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    /// <summary>Moves a new stay's admission back in time, so a device can be recorded as put in days ago.</summary>
    private async Task MoveAdmissionBackAsync(Guid admissionId, TimeSpan by)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await db.Set<Admission>().IgnoreQueryFilters().Where(a => a.Id == admissionId).ExecuteUpdateAsync(s => s.SetProperty(a => a.AdmittedAt, a => a.AdmittedAt - by), Ct);
    }

    /// <summary>A urine culture the laboratory verified with an organism, as the lab's own screens leave it.</summary>
    private async Task<Guid> VerifiedUrineCultureAsync(Guid patientId, Guid admissionId, DateTimeOffset collectedAt, string organism)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var test = await db.Set<LabTest>().IgnoreQueryFilters().AsNoTracking().FirstAsync(t => t.Code == "LAB-CULTURE-URINE", Ct);
        var facilityId = await db.Set<Admission>().IgnoreQueryFilters().Where(a => a.Id == admissionId).Select(a => a.FacilityId).SingleAsync(Ct);
        var order = new LabOrder
        {
            FacilityId = facilityId,
            AccessionNumber = $"QS{Random.Shared.Next(1_000_000, 9_999_999)}",
            PatientId = patientId,
            AdmissionId = admissionId,
            PatientClass = PatientClass.InPatient,
            OrderedAt = collectedAt.AddMinutes(-30),
            Status = LabOrderStatus.Completed,
        };
        var sample = new LabSample
        {
            FacilityId = facilityId,
            LabOrderId = order.Id,
            PatientId = patientId,
            Barcode = $"9{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}",
            SpecimenType = SpecimenType.Urine,
            Status = SampleStatus.Completed,
            CollectedAt = collectedAt,
        };
        var item = new LabOrderItem
        {
            FacilityId = facilityId,
            LabOrderId = order.Id,
            LabTestId = test.Id,
            TestCode = test.Code,
            TestName = test.Name,
            SampleId = sample.Id,
            Status = LabOrderItemStatus.Released,
        };
        var result = new LabResult
        {
            FacilityId = facilityId,
            LabOrderId = order.Id,
            LabOrderItemId = item.Id,
            LabTestId = test.Id,
            SampleId = sample.Id,
            PatientId = patientId,
            TestCode = test.Code,
            TestName = test.Name,
            ResultType = LabResultType.Culture,
            Status = LabResultStatus.Verified,
            ValueText = "Growth",
            Organism = organism,
            ColonyCount = "> 10^5 CFU/ml",
            VerifiedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(order, sample, item, result);
        await db.SaveChangesAsync(Ct);
        return result.Id;
    }
}
