using System.Net;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Incidents;
using HyFib.HealthCare360.Modules.Quality.Features.Mvpi;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// An adverse event with a medical device over the real API, reported to the Materiovigilance Programme of India (MvPI):
/// a nurse reports the failed knee implant as an incident; the quality team raises the MvPI report from it, filled in with
/// the implant billed to the patient, saves it as a draft, records it as sent only once MvPI's questions are answered, and
/// then can neither change it nor send it again, only correct its reference with a reason or add a follow-up. The printed
/// form names the patient by initials.
/// </summary>
public sealed class MvpiDeviceReportTests(HospitalServer server)
{
    private const string Reports = "/api/v1/quality/mvpi";
    private const string Brand = "Attune";
    private const string Maker = "DePuy Synthes, imported by Johnson and Johnson Medical India";
    private const string Specification = "Primary total knee, cobalt-chromium femoral component, size 5";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_failed_implant_is_reported_from_its_incident_filled_from_the_bill_and_sent_to_mvpi()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var quality = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

        // The patient had a knee implant, billed from the theatre with its brand, maker, batch and serial number.
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice, Gender.Female, new DateOnly(1960, 1, 15));
        var batch = $"KN{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        var implantedAt = DateTimeOffset.UtcNow.AddDays(-42);
        await ChargeImplantAsync(patientId, visit.Id, batch, implantedAt);

        // A newer implant was charged by mistake and the charge cancelled (the pack went back unused): it is no device of hers.
        var returnedBatch = "RET" + batch[3..];
        var returned = await ChargeImplantAsync(patientId, visit.Id, returnedBatch, implantedAt.AddDays(1));
        await CancelChargeAsync(returned);

        // The nurse reports the loosened implant; anyone may report an incident, but not open the MvPI register.
        var incident = await nurse.PostAsync<IncidentReceipt>("/api/v1/quality/incidents", new ReportIncidentRequest(
            IncidentType.EquipmentFailure, IncidentSeverity.Moderate, DateTimeOffset.UtcNow.AddHours(-3), "Ward 3, bed 12", null, patientId, null,
            "The tibial component of the knee implant loosened six weeks after surgery; revision planned.", "Orthopaedic surgeon informed.", null,
            null, false, false));
        foreach (var path in new[] { Reports, $"{Reports}/prefill?incidentId={incident.Id}" })
        {
            using var refused = await nurse.SendRawAsync(HttpMethod.Get, path);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }

        // An equipment failure may go to MvPI: the incident offers it.
        var detail = await quality.GetAsync<IncidentDetail>($"/api/v1/quality/incidents/{incident.Id}");
        (detail.IsDeviceRelated, detail.MvpiReports.Count).ShouldBe((true, 0));

        // Raised from the incident: its day, place and account, the patient by initials, and the implant from the bill.
        var prefill = await quality.GetAsync<MvpiPrefill>($"{Reports}/prefill?incidentId={incident.Id}");
        (prefill.IncidentNumber, prefill.PatientId, prefill.Location, prefill.Sex).ShouldBe((incident.IncidentNumber, patientId, "Ward 3, bed 12", "Female"));
        prefill.PatientInitials!.Length.ShouldBeLessThan(8, "the form names the patient by initials");
        prefill.Description!.ShouldContain("Action taken: Orthopaedic surgeon informed.");
        prefill.Devices.ShouldContain(d => d.IsImplant && d.Batch == batch && d.Serial == "SN-A1");
        prefill.Devices.ShouldNotContain(d => d.Batch == returnedBatch, "a cancelled charge is no device of the patient's");
        var device = prefill.Device;
        (device.Name, device.Manufacturer, device.LotNumber, device.SerialNumber, device.Implantable, device.ImplantedOn)
            .ShouldBe((Brand, Maker, batch, "SN-A1", true, DateOnly.FromDateTime(implantedAt.UtcDateTime.AddHours(5.5))));
        device.GenericName!.ShouldContain(Specification);

        // Saved as a draft without the reporter's designation or where the implant is kept: it cannot be sent yet.
        var request = new SaveMvpiReportRequest(
            incident.Id, patientId, false, 64m, prefill.EventOn, prefill.AwareOn, prefill.Location, prefill.Description,
            MvpiSeriousness.Hospitalisation | MvpiSeriousness.RequiredIntervention, null, MvpiPatientOutcome.NotRecovered,
            device with { RiskClass = DeviceRiskClass.C }, null, "Quality");
        var draft = await quality.PostAsync<MvpiReportDto>(Reports, request);
        (draft.Status, draft.IncidentNumber, draft.Uhid, draft.AgeYears).ShouldBe((MvpiReportStatus.Draft, incident.IncidentNumber, prefill.Uhid, prefill.AgeYears));
        draft.Missing.Keys.ShouldBe(["device.keptAt", "reporterDesignation"], ignoreOrder: true);
        using (var incomplete = await quality.SendRawAsync(HttpMethod.Post, $"{Reports}/{draft.Id}/submit",
                   new SubmitMvpiReportRequest(today, null, "MDAE-TN-2026-0042", draft.Version)))
        {
            incomplete.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var problem = await incomplete.Content.ReadAsStringAsync(Ct);
            (problem.Contains("reporterDesignation"), problem.Contains("device.keptAt")).ShouldBe((true, true), problem);
        }

        // A report stays with the incident it was raised from: a correction cannot move it to another, or to none.
        using (var moved = await quality.SendRawAsync(HttpMethod.Put, $"{Reports}/{draft.Id}", request with { IncidentId = null, Version = draft.Version }))
        {
            moved.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await moved.Content.ReadAsStringAsync(Ct)).ShouldContain("A report stays with the incident it was raised from.");
        }

        // Completed and sent, with MvPI's reference and who sent it.
        var completed = await quality.PutAsync<MvpiReportDto>($"{Reports}/{draft.Id}", request with
        {
            Device = request.Device with { KeptAt = "Biomedical quarantine shelf 2" },
            ReporterDesignation = "Quality manager",
            Version = draft.Version,
        });
        completed.Missing.ShouldBeEmpty();
        var sent = await quality.PostAsync<MvpiReportDto>($"{Reports}/{draft.Id}/submit",
            new SubmitMvpiReportRequest(today, "Meena Iyer", "MDAE-TN-2026-0042", completed.Version));
        (sent.Status, sent.SubmittedOn, sent.SubmittedByName, sent.Reference).ShouldBe((MvpiReportStatus.Submitted, today, "Meena Iyer", "MDAE-TN-2026-0042"));

        // Sent, it does not change and is not sent again; only its reference is corrected, with the reason.
        using (var locked = await quality.SendRawAsync(HttpMethod.Put, $"{Reports}/{draft.Id}", request with { Version = sent.Version }))
        {
            locked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await locked.Content.ReadAsStringAsync(Ct)).ShouldContain("mvpi.submitted");
        }

        using (var again = await quality.SendRawAsync(HttpMethod.Post, $"{Reports}/{draft.Id}/submit",
                   new SubmitMvpiReportRequest(today, null, null, sent.Version)))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        var corrected = await quality.PostAsync<MvpiReportDto>($"{Reports}/{draft.Id}/reference",
            new CorrectMvpiReferenceRequest("MDAE-TN-2026-0024", "Digits swapped when it was typed", sent.Version));
        (corrected.Reference, corrected.ReferenceCorrectionReason).ShouldBe(("MDAE-TN-2026-0024", "Digits swapped when it was typed"));

        // The register lists it as sent, and the incident names it.
        (await quality.GetAsync<List<MvpiReportListItem>>($"{Reports}?status=Submitted&from={today.AddDays(-1):yyyy-MM-dd}"))
            .Single(r => r.Id == draft.Id).Reference.ShouldBe("MDAE-TN-2026-0024");
        (await quality.GetAsync<List<MvpiReportListItem>>($"{Reports}?status=Draft")).ShouldNotContain(r => r.Id == draft.Id);
        (await quality.GetAsync<IncidentDetail>($"/api/v1/quality/incidents/{incident.Id}")).MvpiReports.ShouldHaveSingleItem().Number.ShouldBe(draft.Number);

        // What is learned later goes on a follow-up report, a draft that names the one sent.
        var followUp = await quality.PostAsync<MvpiReportDto>($"{Reports}/{draft.Id}/follow-up");
        (followUp.Status, followUp.FollowUpOfNumber, followUp.FollowUpOfReference, followUp.Device.LotNumber)
            .ShouldBe((MvpiReportStatus.Draft, draft.Number, "MDAE-TN-2026-0024", batch));
        (await quality.GetAsync<MvpiReportDto>($"{Reports}/{draft.Id}")).FollowUps.ShouldHaveSingleItem().Id.ShouldBe(followUp.Id);

        // The printed MDAE form: the patient by initials, the implant, MvPI's reference; never the patient's name.
        var form = await quality.GetAsync<PrintableDocumentDto>($"{Reports}/{draft.Id}/document");
        form.Html.ShouldContain("Medical device adverse event reporting form (MvPI)");
        form.Html.ShouldContain(batch);
        form.Html.ShouldContain("MDAE-TN-2026-0024");
        form.Html.ShouldContain(sent.PatientInitials!);
        var name = await PatientNameAsync(patientId);
        form.Html.ShouldNotContain(name);

        // Opening and printing it are reads of the patient's record, and are audited as such.
        (await RecordAccessesAsync(patientId)).ShouldContain($"Printed MvPI report {draft.Number}");

        using var pdf = new HttpRequestMessage(HttpMethod.Get, $"{Reports}/{draft.Id}/pdf");
        pdf.Headers.Add(TestPdfRenderer.Header, TestPdfRenderer.Made);
        using var downloaded = await quality.Http.SendAsync(pdf, Ct);
        downloaded.StatusCode.ShouldBe(HttpStatusCode.OK);
        downloaded.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        TestPdfRenderer.PageOf(await downloaded.Content.ReadAsByteArrayAsync(Ct)).ShouldContain(batch);
    }

    [Fact]
    public async Task A_report_may_be_started_for_a_patient_or_for_no_patient_and_needs_the_permission()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var quality = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
        var (patientId, _) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);

        // A fall is no equipment failure, but the bed's rail gave way: the nurse says a device was involved, so the incident
        // may go to MvPI, until the quality team's review says otherwise.
        var fall = await nurse.PostAsync<IncidentReceipt>("/api/v1/quality/incidents", new ReportIncidentRequest(
            IncidentType.PatientFall, IncidentSeverity.NoHarm, DateTimeOffset.UtcNow.AddMinutes(-30), "Ward 2, bed 4", null, patientId, null,
            "The side rail of the bed gave way and the patient slid to the floor; no injury.", null, null, null, false, false, InvolvesDevice: true));
        var reported = await quality.GetAsync<IncidentDetail>($"/api/v1/quality/incidents/{fall.Id}");
        (reported.InvolvesDevice, reported.IsDeviceRelated).ShouldBe((true, true));
        await quality.PutAsync($"/api/v1/quality/incidents/{fall.Id}", new UpdateIncidentRequest(
            IncidentType.PatientFall, IncidentSeverity.NoHarm, null, false, "Ward 2, bed 4", null, patientId, null, reported.Version, InvolvesDevice: false));
        (await quality.GetAsync<IncidentDetail>($"/api/v1/quality/incidents/{fall.Id}")).IsDeviceRelated.ShouldBeFalse();

        // For a patient with nothing billed as a device: today's date, and the device to fill in by hand.
        var prefill = await quality.GetAsync<MvpiPrefill>($"{Reports}/prefill?patientId={patientId}");
        (prefill.IncidentId, prefill.EventOn, prefill.Devices.Count, prefill.Device.Name).ShouldBe((null, today, 0, null));

        // A monitor that failed on its daily check harmed nobody.
        var monitor = MvpiDevice.Blank with
        {
            Name = "IntelliVue MX450",
            Manufacturer = "Philips",
            SerialNumber = "DE12345678",
            Availability = DeviceAvailability.NotAvailable,
        };
        var request = new SaveMvpiReportRequest(
            null, null, true, null, today, today, "ICU", "The monitor showed a flat SpO2 trace on its morning check.", MvpiSeriousness.None, null,
            MvpiPatientOutcome.Unknown, monitor, "Biomedical engineer", "Biomedical engineering");
        using (var notTheirs = await nurse.SendRawAsync(HttpMethod.Post, Reports, request))
        {
            notTheirs.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var both = await quality.SendRawAsync(HttpMethod.Post, Reports, request with { PatientId = patientId }))
        {
            both.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var report = await quality.PostAsync<MvpiReportDto>(Reports, request);
        (report.NoPatient, report.PatientInitials, report.Missing.Count).ShouldBe((true, null, 0));
        (await quality.GetAsync<PrintableDocumentDto>($"{Reports}/{report.Id}/document")).Html.ShouldContain("No patient involved");
    }

    [Fact]
    public async Task A_report_on_a_restricted_record_is_closed_to_an_officer_who_may_not_open_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var officer = await server.SignInAsync(HospitalServer.Users.QualityManager);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
        var (patientId, _) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);

        var pump = MvpiDevice.Blank with
        {
            Name = "Volumat Agilia",
            Manufacturer = "Fresenius Kabi",
            SerialNumber = "VA-2231",
            Availability = DeviceAvailability.NotAvailable,
        };
        var request = new SaveMvpiReportRequest(
            null, patientId, false, null, today, today, "ICU, bed 3", "The infusion pump ran the heparin at twice the set rate for 20 minutes.",
            MvpiSeriousness.None, null, MvpiPatientOutcome.Recovered, pump, "Nursing superintendent", "Nursing");
        var draft = await officer.PostAsync<MvpiReportDto>(Reports, request);
        draft.Missing.ShouldBeEmpty();

        // The record is then made restricted: the officer, who may not open it, may neither look up the patient's devices
        // nor start a report for them, gets nothing of the report, and cannot record it as sent or correct its reference,
        // both of which answer with the whole report.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            await db.Set<Patient>().Where(p => p.Id == patientId).ExecuteUpdateAsync(p => p.SetProperty(x => x.IsVip, true), Ct);
        }

        var submit = new SubmitMvpiReportRequest(today, null, "MDAE-TN-2026-0031", draft.Version);
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, $"{Reports}/prefill?patientId={patientId}", null),
                     (HttpMethod.Post, Reports, request),
                     (HttpMethod.Get, $"{Reports}/{draft.Id}", null),
                     (HttpMethod.Get, $"{Reports}/{draft.Id}/document", null),
                     (HttpMethod.Put, $"{Reports}/{draft.Id}", request with { Version = draft.Version }),
                     (HttpMethod.Post, $"{Reports}/{draft.Id}/submit", submit),
                     (HttpMethod.Post, $"{Reports}/{draft.Id}/follow-up", null),
                 })
        {
            using var refused = await officer.SendRawAsync(method, path, body);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("patient.restricted");
        }

        // Who may open restricted records sends it; the officer still may not correct its reference.
        var sent = await admin.PostAsync<MvpiReportDto>($"{Reports}/{draft.Id}/submit", submit);
        sent.Status.ShouldBe(MvpiReportStatus.Submitted, "the officer's refused attempt sent nothing");
        using (var refused = await officer.SendRawAsync(HttpMethod.Post, $"{Reports}/{draft.Id}/reference",
                   new CorrectMvpiReferenceRequest("MDAE-TN-2026-0032", "Typed wrong", sent.Version)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await admin.GetAsync<MvpiReportDto>($"{Reports}/{draft.Id}")).Reference.ShouldBe("MDAE-TN-2026-0031");
    }

    /// <summary>Billing: the knee implant used in theatre, as the surgery record charges it (brand, maker, batch, serial).</summary>
    private async Task<string> ChargeImplantAsync(Guid patientId, Guid visitId, string batch, DateTimeOffset usedAt)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var poster = scope.ServiceProvider.GetRequiredService<IChargePoster>();
        var implant = await db.Set<ServiceItem>().Where(s => s.Code == "OT-IMPLANT").Select(s => s.Id).SingleAsync(Ct);
        var reference = Guid.CreateVersion7().ToString();
        var posted = await poster.PostAsync(new ChargeRequest
        {
            PatientId = patientId,
            VisitId = visitId,
            ServiceItemId = implant,
            UnitPriceOverride = 50_000m,
            Description = "Implant: Knee implant system",
            SourceModule = "tests.mvpi",
            SourceReference = reference,
            ServiceDate = usedAt,
            Device = new DeviceDetails(Brand, Maker, Specification, batch, "SN-A1", 60_000m, null),
        }, Ct);
        posted.IsSuccess.ShouldBeTrue(posted.Error?.Message);
        await db.SaveChangesAsync(Ct);
        return reference;
    }

    /// <summary>Billing: the charge cancelled before it was billed.</summary>
    private async Task CancelChargeAsync(string reference)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var cancelled = await scope.ServiceProvider.GetRequiredService<IChargePoster>().CancelAsync("tests.mvpi", reference, "Pack returned unused", Ct);
        cancelled.IsSuccess.ShouldBeTrue(cancelled.Error?.Message);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<List<string?>> RecordAccessesAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.PatientId == patientId && a.Action == "patient.accessed")
            .Select(a => a.Summary)
            .ToListAsync(Ct);
    }

    private async Task<string> PatientNameAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await db.Set<Patient>().IgnoreQueryFilters().Where(p => p.Id == patientId).Select(p => p.FullName).SingleAsync(Ct);
    }
}
