using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HyFib.HealthCare360.Modules.Clinical.Domain.TelemedicinePrescribing;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// What a doctor may prescribe in a teleconsultation, by the Telemedicine Practice Guidelines 2020 list the item master puts
/// each medicine on, over the real API and PostgreSQL: List O in any; List A in a first one only by video, and in a
/// follow-up; List B only in a follow-up, for the condition the doctor saw the patient in person for; a medicine on no list
/// as List B; the Prohibited List never. The doctor is told, medicine by medicine, why one is refused.
/// </summary>
public sealed class TelemedicineListTests(HospitalServer server)
{
    private const string Prescriptions = "/api/v1/clinical/prescriptions";
    private const string Urti = "J06.9";
    private const string Diabetes = "E11.9";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_first_teleconsultation_not_by_video_prescribes_only_list_O_and_says_why_for_each_other_medicine()
    {
        var doctor = await server.SignInAsync(Users.Doctor);
        var drugs = await DrugsAsync();
        var patient = await PatientAsync();
        var tele = await TeleconsultAsync(doctor, patient, byVideo: false, Urti);

        var refusal = await RefusedAsync(doctor, patient, tele, drugs.O, drugs.A, drugs.B, drugs.Unlisted, drugs.Morphine);

        refusal.Code.ShouldBe(ProhibitedCode, "the gravest reason names the refusal");
        refusal.Refused.ShouldBe([
            (drugs.A.Name, "A", ListANeedsVideoCode),
            (drugs.B.Name, "B", ListBNeedsFollowUpCode),
            (drugs.Unlisted.Name, "None", UnlistedCode),
            (drugs.Morphine.Name, "Prohibited", ProhibitedCode),
        ]);
        refusal.Detail.ShouldContain($"{drugs.A.Name} cannot be prescribed in this teleconsultation: it is on List A");
        (refusal.ByVideo, refusal.IsFollowUp).ShouldBe((false, false));

        (await SignAsync(doctor, patient, tele, drugs.O)).ShouldBe(PrescriptionStatus.Signed);
    }

    [Fact]
    public async Task A_first_teleconsultation_by_video_also_prescribes_list_A_until_the_call_falls_back_to_audio()
    {
        var doctor = await server.SignInAsync(Users.Doctor);
        var drugs = await DrugsAsync();
        var patient = await PatientAsync();
        var tele = await TeleconsultAsync(doctor, patient, byVideo: true, Urti);

        (await SignAsync(doctor, patient, tele, drugs.O, drugs.A)).ShouldBe(PrescriptionStatus.Signed);
        (await RefusedAsync(doctor, patient, tele, drugs.B, drugs.Unlisted)).Refused.ShouldBe([
            (drugs.B.Name, "B", ListBNeedsFollowUpCode),
            (drugs.Unlisted.Name, "None", UnlistedCode),
        ]);

        // The call drops to audio only: from then on it is not a video consultation.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            await db.Set<TelehealthSession>().Where(s => s.VisitId == tele.VisitId).ExecuteUpdateAsync(s => s.SetProperty(x => x.AudioOnlyUsed, true), Ct);
        }

        var refusal = await RefusedAsync(doctor, patient, tele, drugs.A);
        (refusal.Code, refusal.ByVideo).ShouldBe((ListANeedsVideoCode, false));
    }

    [Fact]
    public async Task A_follow_up_of_an_in_person_consultation_for_the_same_condition_prescribes_lists_A_and_B_and_unlisted_medicines()
    {
        var doctor = await server.SignInAsync(Users.Doctor);
        var drugs = await DrugsAsync();
        var patient = await PatientAsync();
        await InPersonAsync(doctor, patient, Urti);
        var tele = await TeleconsultAsync(doctor, patient, byVideo: false, Urti);

        (await SignAsync(doctor, patient, tele, drugs.O, drugs.A, drugs.B, drugs.Unlisted)).ShouldBe(PrescriptionStatus.Signed);

        var refusal = await RefusedAsync(doctor, patient, tele, drugs.Morphine);
        (refusal.Code, refusal.IsFollowUp).ShouldBe((ProhibitedCode, true), "the Prohibited List is never prescribed, follow-up or not");
    }

    [Fact]
    public async Task A_teleconsultation_for_another_condition_or_with_another_doctor_is_a_first_one()
    {
        var doctor = await server.SignInAsync(Users.Doctor);
        var colleague = await server.SignInAsync(Users.SecondDoctor);
        var drugs = await DrugsAsync();
        var patient = await PatientAsync();
        await InPersonAsync(doctor, patient, Diabetes);

        // The same doctor, for a cold: not the condition seen in person.
        var cold = await TeleconsultAsync(doctor, patient, byVideo: false, Urti);
        (await RefusedAsync(doctor, patient, cold, drugs.B)).Code.ShouldBe(ListBNeedsFollowUpCode);

        // Another doctor, for the diabetes: not the doctor who saw the patient (TPG 2020, section 3.4).
        var other = await TeleconsultAsync(colleague, patient, byVideo: false, Diabetes);
        (await RefusedAsync(colleague, patient, other, drugs.B)).IsFollowUp.ShouldBeFalse();

        // With no diagnosis recorded in the teleconsultation, it cannot be shown to be a follow-up.
        var undiagnosed = await TeleconsultAsync(doctor, patient, byVideo: false, icdCode: null);
        (await RefusedAsync(doctor, patient, undiagnosed, drugs.B)).Code.ShouldBe(ListBNeedsFollowUpCode);
    }

    [Fact]
    public async Task The_item_master_keeps_schedule_X_and_NDPS_medicines_on_the_prohibited_list_and_a_looser_list_waits_for_a_pharmacist()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(Users.Pharmacist);

        // Morphine is on the Prohibited List from the seed, by law.
        var morphine = (await admin.GetAsync<ItemDetail>($"/api/v1/inventory/items/by-code/DRG-MORPH10"));
        morphine.TelemedicineList.ShouldBe(TelemedicineList.Prohibited);
        (await StatusAsync(admin, HttpMethod.Put, $"/api/v1/inventory/items/{morphine.Id}", Save(morphine, TelemedicineList.O)))
            .ShouldBe((HttpStatusCode.BadRequest, "validation"));

        // A new medicine is saved on the list asked for; moved to a looser one, it keeps its list until a pharmacist approves.
        var drug = await DrugAsync(admin, TelemedicineList.B, "Loose");
        var item = await admin.GetAsync<ItemDetail>($"/api/v1/inventory/items/{drug.Id}");
        item.TelemedicineList.ShouldBe(TelemedicineList.B);
        using var asked = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/inventory/items/{drug.Id}",
            Save(item, TelemedicineList.O) with { ClassChangeReason = "The pharmacy and therapeutics committee moved it to List O." });
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var request = (await asked.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct));
        var waiting = await admin.GetAsync<ItemDetail>($"/api/v1/inventory/items/{drug.Id}");
        (waiting.TelemedicineList, waiting.PendingClass!.Asked.TelemedicineList).ShouldBe((TelemedicineList.B, TelemedicineList.O));

        await pharmacist.PostAsync<JsonElement>($"/api/v1/approvals/{request.GetProperty("id").GetGuid()}/approve",
            new ApproveRequest("Agreed.", request.GetProperty("version").GetUInt32()));
        (await admin.GetAsync<ItemDetail>($"/api/v1/inventory/items/{drug.Id}")).TelemedicineList.ShouldBe(TelemedicineList.O);

        // Made stricter, a list applies at once.
        var approved = await admin.GetAsync<ItemDetail>($"/api/v1/inventory/items/{drug.Id}");
        using var stricter = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/inventory/items/{drug.Id}", Save(approved, TelemedicineList.Prohibited));
        stricter.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.GetAsync<ItemDetail>($"/api/v1/inventory/items/{drug.Id}")).TelemedicineList.ShouldBe(TelemedicineList.Prohibited);
    }

    // ------------------------------------------------------------------ the medicines and the consultations

    private sealed record Drug(Guid Id, string Name);

    private sealed record Drugs(Drug O, Drug A, Drug B, Drug Unlisted, Drug Morphine);

    private sealed record Teleconsult(Guid VisitId, Guid EncounterId);

    private sealed record Refusal(string Code, string Detail, List<(string Name, string List, string Code)> Refused, bool ByVideo, bool IsFollowUp);

    /// <summary>A medicine on each list, new to the item master (a new item's list applies at once), and the seeded morphine.</summary>
    private async Task<Drugs> DrugsAsync()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var doctor = await server.SignInAsync(Users.Doctor);
        var morphine = (await doctor.GetAsync<List<PrescribableDrug>>("/api/v1/emr/drugs?search=Morphine")).Single(d => d.Code == "DRG-MORPH10");
        return new Drugs(
            await DrugAsync(admin, TelemedicineList.O, "Ocet"),
            await DrugAsync(admin, TelemedicineList.A, "Acet"),
            await DrugAsync(admin, TelemedicineList.B, "Bcet"),
            await DrugAsync(admin, null, "Ncet"),
            new Drug(morphine.Id, morphine.Name));
    }

    private static async Task<Drug> DrugAsync(ApiClient admin, TelemedicineList? list, string stem)
    {
        var generic = $"{stem}{Letters(8)}";
        var name = $"{generic} 10 mg Tablet";
        var created = await admin.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"TM-{Letters(10)}", Name: name, GenericName: generic, BrandName: null, Type: ItemType.Drug, CategoryId: null, Strength: "10 mg",
            DosageForm: "Tablet", PackSize: "Strip of 10", BaseUnit: "TAB", PurchaseUnit: "STRIP", PurchaseUnitFactor: 10, HsnCode: "3004", GstRate: 12m,
            Schedule: DrugSchedule.H, IsControlled: false, IsColdChain: false, VedClass: VedClass.Essential, ReorderLevel: 0, ReorderQuantity: 0,
            MinimumStock: 0, MaximumStock: 0, Manufacturer: null, Barcode: null, IsActive: true, Version: null, TelemedicineList: list));
        return new Drug(created.GetProperty("id").GetGuid(), name);
    }

    private async Task<Guid> PatientAsync()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        return (await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice)).Id;
    }

    /// <summary>The doctor sees the patient in person in the OP, and records the diagnosis.</summary>
    private async Task InPersonAsync(ApiClient doctor, Guid patientId, string icdCode)
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var departments = await frontOffice.GetAsync<List<LookupItem>>("/api/v1/masters/lookups/departments");
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.OutPatient, departments.Single(d => d.Code == "GM").Id, await PractitionerIdAsync(doctor), null, PayerType.Self, null,
            ReferralSource.Self, null, "Seen in the clinic", false, null, null, TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patientId, visit.Id, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, ChiefComplaint: "In person"));
        encounter.Type.ShouldBe(EncounterType.OutPatient);
        await DiagnoseAsync(doctor, encounter.Id, icdCode);
    }

    /// <summary>
    /// A teleconsultation of the doctor's: by video, a video call booked and started; otherwise a telehealth visit opened
    /// without one (a phone consultation). The doctor records <paramref name="icdCode"/> in it, when given.
    /// </summary>
    private async Task<Teleconsult> TeleconsultAsync(ApiClient doctor, Guid patientId, bool byVideo, string? icdCode)
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var practitionerId = await PractitionerIdAsync(doctor);
        Guid visitId;
        if (byVideo)
        {
            var slot = await TeleconsultationJourneyTests.FreeVideoSlotAsync(frontOffice, practitionerId);
            var appointment = await frontOffice.PostAsync<AppointmentDetail>("/api/v1/scheduling/appointments", new BookAppointmentRequest(
                patientId, practitionerId, slot.StartsAt, AppointmentChannel.Phone, AppointmentVisitType.New, IsTelehealth: true, "Cough", Notes: null));
            await frontOffice.PostAsync<JsonElement>($"/api/v1/patients/{patientId}/consents",
                new Modules.Patients.Features.FrontOffice.CaptureConsentRequest(ConsentType.Telehealth, null, true, null, null, null, null, "Agreed on the phone"));
            var day = DateOnly.FromDateTime(slot.StartsAt.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
            var session = (await doctor.GetAsync<List<TelehealthSessionListItem>>($"/api/v1/telehealth/sessions?date={day:O}&practitionerId={practitionerId}"))
                .Single(s => s.AppointmentId == appointment.Id);
            var started = await doctor.PostAsync<TelehealthSessionDetail>($"/api/v1/telehealth/sessions/{session.Id}/start");
            visitId = started.VisitId!.Value;
        }
        else
        {
            visitId = (await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
                patientId, VisitType.Telehealth, null, practitionerId, null, PayerType.Self, null, ReferralSource.Self, null, "Phone consultation", false, null,
                null, TokenPriority.Normal, IssueToken: false, NotifyPatient: false))).Id;
        }

        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patientId, visitId, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, ChiefComplaint: "Teleconsultation"));
        encounter.Type.ShouldBe(EncounterType.Telehealth);
        if (icdCode is not null)
        {
            await DiagnoseAsync(doctor, encounter.Id, icdCode);
        }

        return new Teleconsult(visitId, encounter.Id);
    }

    private static Task<DiagnosisDto> DiagnoseAsync(ApiClient doctor, Guid encounterId, string icdCode) =>
        doctor.PostAsync<DiagnosisDto>($"/api/v1/emr/encounters/{encounterId}/diagnoses",
            new AddDiagnosisRequest(icdCode, null, DiagnosisType.Provisional, true, null, DiagnosisStatus.Active, null));

    // ------------------------------------------------------------------ prescribing

    private static async Task<PrescriptionDto> WriteAsync(ApiClient doctor, Guid patientId, Teleconsult tele, Drug[] drugs) =>
        await doctor.PostAsync<PrescriptionDto>(Prescriptions, new CreatePrescriptionRequest(
            patientId, tele.EncounterId, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null, RefillsAllowed: 0,
            [
                .. drugs.Select(d => new PrescriptionItemRequest(
                    d.Id, d.Name, null, null, null, Route: "Oral", Dose: "1", DoseFrequency.OD, CustomFrequency: null, DosesPerDay: null, 3, Quantity: 3,
                    Instructions: null, IsControlledDrug: false, ControlledSchedule: null)),
            ]));

    private static SignPrescriptionRequest Sign(PrescriptionDto rx) =>
        new("Reviewed: clinically needed for this patient now.", AcknowledgeWarnings: true, ConfirmControlledDrugs: true, rx.Version);

    private static async Task<PrescriptionStatus> SignAsync(ApiClient doctor, Guid patientId, Teleconsult tele, params Drug[] drugs)
    {
        var rx = await WriteAsync(doctor, patientId, tele, drugs);
        return (await doctor.PostAsync<SignPrescriptionResult>($"{Prescriptions}/{rx.Id}/sign", Sign(rx))).Prescription.Status;
    }

    private static async Task<Refusal> RefusedAsync(ApiClient doctor, Guid patientId, Teleconsult tele, params Drug[] drugs)
    {
        var rx = await WriteAsync(doctor, patientId, tele, drugs);
        using var response = await doctor.SendRawAsync(HttpMethod.Post, $"{Prescriptions}/{rx.Id}/sign", Sign(rx));
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
        var problem = JsonDocument.Parse(body).RootElement;
        var data = problem.GetProperty("data");
        var consult = data.GetProperty("consult");
        return new Refusal(
            problem.GetProperty("code").GetString()!,
            problem.GetProperty("detail").GetString()!,
            [.. data.GetProperty("telemedicine").EnumerateArray().Select(r => (r.GetProperty("name").GetString()!, r.GetProperty("list").GetString()!, r.GetProperty("code").GetString()!))],
            consult.GetProperty("byVideo").GetBoolean(),
            consult.GetProperty("isFollowUp").GetBoolean());
    }

    // ------------------------------------------------------------------ helpers

    private static SaveItemRequest Save(ItemDetail i, TelemedicineList? list) => new(
        i.Code, i.Name, i.GenericName, i.BrandName, i.Type, i.CategoryId, i.Strength, i.DosageForm, i.PackSize, i.BaseUnit, i.PurchaseUnit,
        i.PurchaseUnitFactor, i.HsnCode, i.GstRate, i.Schedule, i.IsControlled, i.IsColdChain, i.VedClass, i.ReorderLevel,
        i.ReorderQuantity, i.MinimumStock, i.MaximumStock, i.Manufacturer, i.Barcode, i.IsActive, i.Version, i.Specification, i.CeilingPrice,
        i.IsHighAlert, CeilingNotification: i.CeilingNotification, TelemedicineList: list);

    private static async Task<(HttpStatusCode Status, string? Code)> StatusAsync(ApiClient api, HttpMethod method, string path, object body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        return (response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private static async Task<Guid> PractitionerIdAsync(ApiClient doctor) =>
        (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
