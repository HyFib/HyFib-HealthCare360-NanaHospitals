using System.Net;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentMode = HyFib.HealthCare360.Modules.Billing.Domain.PaymentMode;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A record in a protected category (HIV, mental health, MTP, sexual violence) is hidden from a mobile number another patient
/// also has, and from a family member's account: whoever holds the phone signs in to the portal and reads what is sent to
/// it. On the patient's own number, theirs alone, nothing changes.
/// </summary>
public sealed class SharedNumberTests(HospitalServer server)
{
    private const string Profiles = "/api/v1/portal/profiles";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_record_in_a_protected_category_is_hidden_from_a_number_another_patient_shares()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var phone = NewMobile();
        var mother = await RegisterAsync(frontOffice, "Kamala", phone);
        var son = await RegisterAsync(frontOffice, "Ravi", phone);
        var family = await server.SignInToPortalAsync(phone);
        (await ProfileIdsAsync(family)).ShouldBe([mother, son], ignoreOrder: true);

        await ProtectAsync(mother);

        (await ProfileIdsAsync(family)).ShouldBe([son], "the mother's record is in a protected category and her number is shared");
        (await StatusAsync(family, $"/api/v1/portal/health-records?patientId={mother}")).ShouldBe(HttpStatusCode.Forbidden);
        (await StatusAsync(family, $"/api/v1/portal/health-records?patientId={son}")).ShouldBe(HttpStatusCode.OK);

        // On a number that is the patient's alone, the patient still sees their own record.
        var ownPhone = NewMobile();
        var alone = await RegisterAsync(frontOffice, "Latha", ownPhone);
        await ProtectAsync(alone);
        var own = await server.SignInToPortalAsync(ownPhone);
        (await ProfileIdsAsync(own)).ShouldBe([alone]);
        (await StatusAsync(own, $"/api/v1/portal/health-records?patientId={alone}")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_record_in_a_protected_category_is_hidden_from_a_family_members_account()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var motherPhone = NewMobile();
        var mother = await RegisterAsync(frontOffice, "Saroja", motherPhone);
        var sonPhone = NewMobile();
        await server.LinkFamilyAtDeskAsync(frontOffice, mother, new LinkFamilyMemberRequest(sonPhone, FamilyRelation.Parent, "Patient", true));
        var son = await server.SignInToPortalAsync(sonPhone);
        (await ProfileIdsAsync(son)).ShouldBe([mother]);

        await ProtectAsync(mother);

        (await ProfileIdsAsync(son)).ShouldBeEmpty("the son's number is not the mother's");
        (await StatusAsync(son, $"/api/v1/portal/health-records?patientId={mother}")).ShouldBe(HttpStatusCode.Forbidden);
        var own = await server.SignInToPortalAsync(motherPhone);
        (await ProfileIdsAsync(own)).ShouldBe([mother], "her own number is hers alone");
    }

    [Fact]
    public async Task A_report_a_prescription_or_a_bill_of_a_protected_record_is_not_sent_to_a_number_another_patient_shares()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var phone = NewMobile();
        var mother = await RegisterAsync(frontOffice, "Parvathi", phone);
        var son = await RegisterAsync(frontOffice, "Kumar", phone);
        var alonePhone = NewMobile();
        var alone = await RegisterAsync(frontOffice, "Vasanthi", alonePhone);
        await ProtectAsync(mother);
        await ProtectAsync(alone);

        // Verified numbers, as the desk verifies them with a code: an unverified one gets no documents at all.
        await server.VerifyPatientPhonesAsync(phone);
        await server.VerifyPatientPhonesAsync(alonePhone);

        // A report ready.
        foreach (var patient in new[] { mother, son, alone })
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ReportDelivery>().QueueReportReadyAsync(patient, "LAB-" + Digits(6), null, null, Ct);
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().SaveChangesAsync(Ct);
        }

        (await MessagesAsync(mother, ReportDelivery.ReportReadyTemplate)).ShouldBeEmpty("her number is shared and she has no e-mail address");
        (await MessagesAsync(son, ReportDelivery.ReportReadyTemplate)).ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp], ignoreOrder: true);
        (await MessagesAsync(alone, ReportDelivery.ReportReadyTemplate)).ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp], ignoreOrder: true);

        // A prescription signed in the clinic.
        await PrescribeAsync(mother);
        await PrescribeAsync(alone);
        (await MessagesAsync(mother, "prescription.shared")).ShouldBeEmpty();
        (await MessagesAsync(alone, "prescription.shared")).ShouldBe([NotificationChannel.WhatsApp]);

        // A bill: not sent when paid, and the desk is not told why when it tries.
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var motherBill = await PaidBillAsync(billing, mother);
        (await MessagesAsync(mother, BillSharing.Template)).ShouldBeEmpty();
        using (var refused = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/bills/{motherBill}/send-whatsapp"))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var body = await refused.Content.ReadAsStringAsync(Ct);
            body.ShouldContain("billing.bill_not_sent_to_number");
            body.ShouldNotContain("protected", Case.Insensitive);
        }

        var aloneBill = await PaidBillAsync(billing, alone);
        (await MessagesAsync(alone, BillSharing.Template)).ShouldBe([NotificationChannel.WhatsApp], "her number is hers alone");
        await billing.PostAsync($"/api/v1/billing/bills/{aloneBill}/send-whatsapp");
        (await MessagesAsync(alone, BillSharing.Template)).Count.ShouldBe(2);
    }

    private async Task<Guid> RegisterAsync(ApiClient frontOffice, string firstName, string phone) =>
        (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: firstName, MiddleName: null, LastName: "Shared" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(18000)), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true))).Id;

    private async Task ProtectAsync(Guid patientId)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var categories = await doctor.GetAsync<SensitiveCategoriesDto>($"/api/v1/patients/{patientId}/sensitive-categories");
        await doctor.PutAsync<SensitiveCategoriesDto>($"/api/v1/patients/{patientId}/sensitive-categories",
            new SetSensitiveCategoriesRequest([SensitiveCategory.MentalHealth], "Diagnosis recorded in the clinic", categories.Version));
    }

    private static async Task<List<Guid>> ProfileIdsAsync(ApiClient portal) =>
        [.. (await portal.GetAsync<List<PatientSummary>>(Profiles)).Select(p => p.Id)];

    private static async Task<HttpStatusCode> StatusAsync(ApiClient client, string path)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, path);
        return response.StatusCode;
    }

    private async Task PrescribeAsync(Guid patientId)
    {
        const string prescriptions = "/api/v1/clinical/prescriptions";
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var draft = await doctor.PostAsync<PrescriptionDto>(prescriptions, new CreatePrescriptionRequest(
            patientId, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [new PrescriptionItemRequest(null, "Pan 40", "Pantoprazole", "40 mg", "Tablet", Route: "Oral", Dose: "1 tablet", DoseFrequency.OD,
                CustomFrequency: null, DosesPerDay: null, DurationDays: 5, Quantity: null, Instructions: "Before food", IsControlledDrug: false,
                ControlledSchedule: null)]));
        await doctor.PostAsync<SignPrescriptionResult>($"{prescriptions}/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version));
    }

    /// <summary>A bill for one service, finalised and paid in cash at the counter (the payment sends it on WhatsApp).</summary>
    private async Task<Guid> PaidBillAsync(ApiClient billing, Guid patientId)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var service = await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            $"SV-{Digits(8)}", "Counselling session", null, ServiceCategory.Procedure, null, 350m, 0m, null, true, false, null, true, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, patientId, null));
        draft = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/lines", new AddBillLineRequest(service.Id, 1m, null, null, null, null, draft.Version));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        using (var shift = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current"))
        {
            if (shift.StatusCode == HttpStatusCode.NoContent)
            {
                await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 1", OpeningFloat: 0m));
            }
        }

        await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            bill.Id, [new PaymentLineInput(PaymentMode.Cash, bill.DueAmount, null, null, null, null, null, null)], Notes: null));
        return bill.Id;
    }

    /// <summary>The channel of each message of a template queued for the patient.</summary>
    private async Task<List<NotificationChannel>> MessagesAsync(Guid patientId, string template)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<NotificationLog>().AsNoTracking()
            .Where(l => l.PatientId == patientId && l.TemplateKey == template)
            .Select(l => l.Channel)
            .ToListAsync(Ct);
    }

    private static string NewMobile() => "9" + Digits(9);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('a' + Random.Shared.Next(26)))]);
}
