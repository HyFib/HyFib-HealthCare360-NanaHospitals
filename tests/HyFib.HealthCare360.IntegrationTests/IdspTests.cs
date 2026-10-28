using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Idsp;
using HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Idsp;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Notifiable diseases (IDSP) over the real API: a diagnosis puts the case on the week's P form and a released positive
/// test confirms the same case on the L form; an outbreak-prone disease alerts the surveillance officer at once; the
/// week's forms are reported to IHIP, a nil report too, once the week is over.
/// </summary>
public sealed class IdspTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_dengue_diagnosis_goes_on_the_P_form_and_a_positive_NS1_confirms_the_same_case_on_the_L_form()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);

        // The doctor diagnoses dengue, then confirms the diagnosis: one case, on the P form of this week.
        var encounter = await EncounterAsync(doctor, patient.Id);
        await DiagnoseAsync(doctor, encounter, "A90", DiagnosisType.Provisional);
        await DiagnoseAsync(doctor, encounter, "A90", DiagnosisType.Final);
        var presumed = await CaseOfAsync(mrd, patient.Id, "DENGUE");
        presumed.PresumedAt.ShouldNotBeNull();
        presumed.ConfirmedAt.ShouldBeNull();
        presumed.IcdCode.ShouldBe("A90");
        var pWeek = await WeekAsync(mrd, IdspWeek.Of(presumed.PresumedAt.Value));
        pWeek.Presumptive.Cases.Where(c => c.PatientId == patient.Id).ShouldHaveSingleItem().Uhid.ShouldBe(patient.Uhid);

        // The laboratory releases a positive NS1 antigen: the same case is confirmed, on the L form.
        var tube = await LabVerificationTests.ReceivedSampleAsync(server, "LAB-DENGUE-NS1", patient.Id);
        var lines = await LabVerificationTests.EnterAsync(lab, tube, new Dictionary<string, string> { ["LAB-DENGUE-NS1"] = "Positive" });
        await pathologist.PostAsync<VerificationOutcome>("/api/v1/lab/results/verify",
            new VerifyResultsRequest([.. lines.Select(l => l.ResultId)], null, null, Release: true));
        var confirmed = await CaseOfAsync(mrd, patient.Id, "DENGUE");
        confirmed.Id.ShouldBe(presumed.Id);
        (confirmed.TestName, confirmed.TestResult).ShouldBe(("Dengue NS1 antigen", "Positive"));
        var lWeek = await WeekAsync(mrd, IdspWeek.Of(confirmed.ConfirmedAt.ShouldNotBeNull()));
        lWeek.Laboratory.Cases.ShouldContain(c => c.Id == presumed.Id);

        // The line list to upload, and only for the surveillance officer.
        var week = IdspWeek.Of(confirmed.ConfirmedAt.Value);
        using (var export = await mrd.SendRawAsync(HttpMethod.Get, $"/api/v1/idsp/weeks/{week.Year}/{week.Week}/export?form=Laboratory"))
        {
            var csv = await export.Content.ReadAsStringAsync(Ct);
            csv.ShouldContain(patient.Uhid);
            csv.ShouldContain("Dengue NS1 antigen");
        }

        using (var notTheDoctors = await doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/idsp/weeks/{week.Year}/{week.Week}"))
        {
            notTheDoctors.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task A_cholera_case_alerts_the_surveillance_officer_at_once_and_a_ruled_out_case_leaves_the_form()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);

        var encounter = await EncounterAsync(doctor, patient.Id);
        await DiagnoseAsync(doctor, encounter, "A00.9", DiagnosisType.Provisional);
        var cholera = await CaseOfAsync(mrd, patient.Id, "CHOLERA");
        cholera.Immediate.ShouldBeTrue();
        cholera.AlertedAt.ShouldNotBeNull();

        var alert = await EventuallyAsync(async () => (await mrd.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=200")).Items
            .FirstOrDefault(n => n.Link == $"/mrd/idsp?case={cholera.Id}"), "The surveillance officer was not alerted.");
        (alert.Title, alert.Severity).ShouldBe(("Notifiable disease: Cholera", "critical"));
        alert.Body.ShouldNotBeNull().ShouldNotContain(patient.Uhid, Case.Sensitive, "the alert carries no patient details");
        (await mrd.GetAsync<IdspOverview>("/api/v1/idsp/weeks")).Immediate.ShouldContain(c => c.Id == cholera.Id);

        // Stool culture grew no vibrio: the officer discards the case with the reason, and it leaves the P form.
        var discarded = await mrd.PostAsync<IdspCaseRow>($"/api/v1/idsp/cases/{cholera.Id}/discard",
            new DiscardIdspCaseRequest("Stool culture negative for Vibrio cholerae", cholera.Version));
        (discarded.Status, discarded.DiscardReason).ShouldBe((NotifiableCaseStatus.Discarded, "Stool culture negative for Vibrio cholerae"));
        var week = await WeekAsync(mrd, IdspWeek.Of(cholera.PresumedAt.ShouldNotBeNull()));
        week.Presumptive.Cases.ShouldNotContain(c => c.Id == cholera.Id);
        week.Discarded.ShouldContain(c => c.Id == cholera.Id);

        using (var again = await mrd.SendRawAsync(HttpMethod.Post, $"/api/v1/idsp/cases/{cholera.Id}/discard",
                   new DiscardIdspCaseRequest("Again", discarded.Version)))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }
    }

    [Fact]
    public async Task A_week_is_reported_once_it_is_over_a_nil_report_too_and_a_case_added_after_shows_as_not_reported()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
        var current = IdspWeek.Of(today);

        (await RefusalAsync(mrd, current, IdspForm.Presumptive, new ReportIdspWeekRequest(today, null))).ShouldBe("idsp.week_not_over");

        // Last week's L form, as many cases as it has (none, in a quiet week: a nil report is still a report).
        var last = current.Previous;
        var before = await WeekAsync(mrd, last);
        var put = $"/api/v1/idsp/weeks/{last.Year}/{last.Week}/Laboratory";
        var sent = before.Laboratory.Report is { } earlier
            ? await mrd.PutAsync<IdspWeekDto>(put, new ReportIdspWeekRequest(today, "IHIP-L-7781", earlier.Version))
            : await mrd.PutAsync<IdspWeekDto>(put, new ReportIdspWeekRequest(today, "IHIP-L-7781"));
        sent.Laboratory.Report.ShouldNotBeNull().Reference.ShouldBe("IHIP-L-7781");
        sent.Laboratory.Report.CaseCount.ShouldBe(sent.Laboratory.Cases.Count);
        (sent.Laboratory.NotReported, sent.Laboratory.Overdue).ShouldBe((0, false));
        (await mrd.GetAsync<IdspOverview>("/api/v1/idsp/weeks")).Weeks.Single(w => w.Week == last.Week && w.Year == last.Year)
            .LaboratoryReported.ShouldBeTrue();

        // A sent date in the week itself is refused, and a second save without the version is stale.
        using (var early = await mrd.SendRawAsync(HttpMethod.Put, put, new ReportIdspWeekRequest(last.Sunday, null, sent.Laboratory.Report.Version)))
        {
            early.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var stale = await mrd.SendRawAsync(HttpMethod.Put, put, new ReportIdspWeekRequest(today, null)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        // An acute flaccid paralysis has no code to catch it: the officer adds it by hand, and is alerted.
        var child = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var afp = await mrd.PostAsync<IdspCaseRow>("/api/v1/idsp/cases", new AddIdspCaseRequest(child.Id, "AFP", today.AddDays(-3), null, null));
        (afp.Source, afp.Immediate, afp.OnsetDate).ShouldBe((NotifiableCaseSource.Manual, true, today.AddDays(-3)));
        afp.AlertedAt.ShouldNotBeNull();
        using (var twice = await mrd.SendRawAsync(HttpMethod.Post, "/api/v1/idsp/cases", new AddIdspCaseRequest(child.Id, "AFP", null, null, null)))
        {
            twice.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
    }

    [Fact]
    public async Task A_tuberculosis_diagnosis_waits_to_be_notified_on_Nikshay_off_the_IHIP_forms_until_its_Nikshay_id_is_recorded()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

        // The doctor suspects pulmonary tuberculosis, then confirms the diagnosis: one case.
        var encounter = await EncounterAsync(doctor, patient.Id);
        await DiagnoseAsync(doctor, encounter, "A16.2", DiagnosisType.Provisional);
        await DiagnoseAsync(doctor, encounter, "A15.0", DiagnosisType.Final);
        var register = await mrd.GetAsync<IdspTuberculosisDto>("/api/v1/idsp/tuberculosis");
        var tb = register.ToNotify.Where(c => c.PatientId == patient.Id).ShouldHaveSingleItem();
        (tb.DiseaseCode, tb.Disease, tb.Nikshay, tb.IcdCode, tb.NikshayId, tb.Uhid).ShouldBe(("TB", "Tuberculosis", true, "A16.2", null, patient.Uhid));

        // The surveillance officer is told to notify it on Ni-kshay.
        var alert = await EventuallyAsync(async () => (await mrd.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=200")).Items
            .FirstOrDefault(n => n.Link == $"/mrd/idsp?case={tb.Id}"), "The surveillance officer was not told to notify the case.");
        alert.Title.ShouldBe("Notify on Ni-kshay: Tuberculosis");
        alert.Body.ShouldNotBeNull().ShouldNotContain(patient.Uhid, Case.Sensitive, "the alert carries no patient details");

        // Tuberculosis is not on the IHIP forms: the week's P form does not carry it.
        (await WeekAsync(mrd, IdspWeek.Of(tb.PresumedAt.ShouldNotBeNull()))).Presumptive.Cases.ShouldNotContain(c => c.PatientId == patient.Id);

        // A day after today is refused; the officer records the Ni-kshay ID, and the case moves to the notified ones.
        using (var tomorrow = await mrd.SendRawAsync(HttpMethod.Post, $"/api/v1/idsp/cases/{tb.Id}/nikshay",
                   new RecordNikshayRequest("10234567", today.AddDays(1), tb.Version)))
        {
            tomorrow.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var notified = await mrd.PostAsync<IdspCaseRow>($"/api/v1/idsp/cases/{tb.Id}/nikshay", new RecordNikshayRequest("10234567", today, tb.Version));
        (notified.NikshayId, notified.NikshayNotifiedOn).ShouldBe(("10234567", today));
        register = await mrd.GetAsync<IdspTuberculosisDto>("/api/v1/idsp/tuberculosis");
        register.ToNotify.ShouldNotContain(c => c.Id == tb.Id);
        register.Notified.ShouldContain(c => c.Id == tb.Id);

        // The register names patients: it is the surveillance officer's, not the doctor's.
        using (var notTheDoctors = await doctor.SendRawAsync(HttpMethod.Get, "/api/v1/idsp/tuberculosis"))
        {
            notTheDoctors.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task A_tuberculosis_case_added_by_hand_also_asks_for_its_Nikshay_notification()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);

        // Diagnosed at another hospital and treated here: medical records adds the case by hand.
        var added = await mrd.PostAsync<IdspCaseRow>("/api/v1/idsp/cases", new AddIdspCaseRequest(patient.Id, "TB", null, null, null));
        var alert = await EventuallyAsync(async () => (await mrd.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=200")).Items
            .FirstOrDefault(n => n.Link == $"/mrd/idsp?case={added.Id}"), "The surveillance officer was not told to notify the case added by hand.");
        alert.Title.ShouldBe("Notify on Ni-kshay: Tuberculosis");
        (await mrd.GetAsync<IdspTuberculosisDto>("/api/v1/idsp/tuberculosis")).ToNotify.ShouldContain(c => c.Id == added.Id);
    }

    private static async Task<Guid> EncounterAsync(ApiClient doctor, Guid patientId) =>
        (await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patientId, VisitId: null, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, ChiefComplaint: "Fever"))).Id;

    private static Task<DiagnosisDto> DiagnoseAsync(ApiClient doctor, Guid encounterId, string code, DiagnosisType type) =>
        doctor.PostAsync<DiagnosisDto>($"/api/v1/emr/encounters/{encounterId}/diagnoses",
            new AddDiagnosisRequest(code, null, type, IsPrimary: false, null, DiagnosisStatus.Active, null));

    private static Task<IdspWeekDto> WeekAsync(ApiClient api, IdspWeek week) => api.GetAsync<IdspWeekDto>($"/api/v1/idsp/weeks/{week.Year}/{week.Week}");

    /// <summary>The patient's case of the disease, from the weeks that hold this week's events.</summary>
    private static async Task<IdspCaseRow> CaseOfAsync(ApiClient api, Guid patientId, string disease)
    {
        var current = IdspWeek.Of(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5)));
        foreach (var week in new[] { current, current.Previous })
        {
            var dto = await WeekAsync(api, week);
            if (dto.Presumptive.Cases.Concat(dto.Laboratory.Cases).FirstOrDefault(c => c.PatientId == patientId && c.DiseaseCode == disease) is { } row)
            {
                return await api.GetAsync<IdspCaseRow>($"/api/v1/idsp/cases/{row.Id}");
            }
        }

        throw new ShouldAssertException($"No {disease} case for the patient.");
    }

    private static async Task<string?> RefusalAsync(ApiClient api, IdspWeek week, IdspForm form, ReportIdspWeekRequest request)
    {
        using var response = await api.SendRawAsync(HttpMethod.Put, $"/api/v1/idsp/weeks/{week.Year}/{week.Week}/{form}", request);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    /// <summary>Polls until the outbox has delivered (it runs every two seconds), for at most a minute.</summary>
    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> read, string failure)
        where T : class
    {
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromMinutes(1); waited += TimeSpan.FromSeconds(1))
        {
            if (await read() is { } value)
            {
                return value;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }

        throw new ShouldAssertException(failure);
    }
}
