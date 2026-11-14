using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Queue;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// What the hospital keys to its day turns at midnight in India, not at midnight UTC, which is 05:30 there: a night's bed
/// charge, the numbers that start again every day or every year, the date printed on a document, and today's queue. Each
/// test crosses a midnight on a server of its own whose clock it sets (<see cref="MidnightInIndiaServer"/>), half a minute
/// before it and half a minute after: 23:59:30 to 00:00:30 in India, 18:29:30 to 18:30:30 UTC. Half a minute after, it is
/// still the day before in UTC, so a day taken from the UTC date would not have turned, and each test would fail.
/// </summary>
[Collection(MidnightInIndiaCollection.Name)]
[OnTestClock]
public sealed class MidnightInIndiaTests(MidnightInIndiaServer midnight)
{
    private static readonly TimeOnly BeforeMidnight = new(23, 59, 30);
    private static readonly TimeOnly AfterMidnight = new(0, 0, 30);

    private HospitalServer Server => midnight.Server;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_night_in_a_bed_is_charged_once_midnight_has_passed_in_India()
    {
        var day = midnight.NextEvening(IsOrdinaryDay);
        var frontOffice = await Server.SignInAsync(Users.FrontOffice);
        var doctor = await Server.SignInAsync(Users.Doctor);
        var billing = await Server.SignInAsync(Users.Billing);
        var admission = await AdmitAsync(frontOffice, doctor);

        // Half a minute to midnight the night is not over: nothing is charged for it yet.
        midnight.At(day, BeforeMidnight);
        await RunBedDayJobAsync();
        (await DayChargesAsync(billing, admission.Id)).ShouldBeEmpty("the night has not ended in India");

        // Half a minute past, the night has ended in India (it is 18:30 the same day in UTC), and the job charges its bed day
        // and its nursing day, for the day the night began.
        midnight.At(day.AddDays(1), AfterMidnight);
        await RunBedDayJobAsync();
        var charged = await DayChargesAsync(billing, admission.Id);
        charged.Select(c => c.SourceModule).ShouldBe([DailyChargeKeys.BedSource, DailyChargeKeys.NursingSource], ignoreOrder: true);
        charged.ShouldAllBe(c => c.SourceReference == DailyChargeKeys.For(admission.Id, day));
    }

    [Fact]
    public async Task Todays_queue_and_its_token_numbers_start_afresh_at_midnight_in_India()
    {
        var day = midnight.NextEvening(IsOrdinaryDay);
        var admin = await Server.SignInAsync(Users.HospitalAdmin);
        var frontOffice = await Server.SignInAsync(Users.FrontOffice);
        var clinic = await ClinicAsync(admin);
        var (eveningPatient, latePatient, morningPatient) = (await RegisterAsync(frontOffice), await RegisterAsync(frontOffice), await RegisterAsync(frontOffice));

        // The evening's patients get tokens 1 and 2, the second half a minute before midnight; both wait in today's queue.
        var evening = await TokenAsync(frontOffice, eveningPatient, clinic);
        midnight.At(day, BeforeMidnight);
        var late = await TokenAsync(frontOffice, latePatient, clinic);
        (evening.TokenDate, evening.Sequence, late.TokenDate, late.Sequence).ShouldBe((day, 1, day, 2));
        (await QueueAsync(frontOffice, clinic)).Select(e => e.TokenId).ShouldBe([evening.Id, late.Id]);

        // Half a minute past midnight a new day has begun in India, though not in UTC: its first token is 1 again, and today's
        // queue holds it alone. Last night's tokens, still waiting, are yesterday's.
        midnight.At(day.AddDays(1), AfterMidnight);
        var morning = await TokenAsync(frontOffice, morningPatient, clinic);
        (morning.TokenDate, morning.Sequence, morning.Label).ShouldBe((day.AddDays(1), 1, $"{clinic.Code}-001"));
        (await QueueAsync(frontOffice, clinic)).Select(e => e.TokenId).ShouldBe([morning.Id]);
    }

    [Fact]
    public async Task The_years_first_bill_in_India_starts_the_numbers_again_and_is_printed_with_the_new_date()
    {
        var day = midnight.NextEvening(IsNewYearsEve);
        var admin = await Server.SignInAsync(Users.HospitalAdmin);
        var frontOffice = await Server.SignInAsync(Users.FrontOffice);
        var billing = await Server.SignInAsync(Users.Billing);
        var dressing = await DressingAsync(admin);
        var (lastPatient, firstPatient) = (await RegisterAsync(frontOffice), await RegisterAsync(frontOffice));
        var lastDraft = await DraftAsync(billing, lastPatient, dressing);

        // Half a minute to midnight on New Year's Eve: the year's last bill, numbered in this year's series.
        midnight.At(day, BeforeMidnight);
        var last = await FinalizeAsync(billing, lastDraft);

        // Half a minute past, the new year has begun in India (in UTC it is still 31 December): the series starts again from
        // 1, under the new year's digits.
        midnight.At(day.AddDays(1), AfterMidnight);
        var first = await FinalizeAsync(billing, await DraftAsync(billing, firstPatient, dressing));

        var lastNumber = last.BillNumber.ShouldNotBeNull();
        lastNumber.ShouldMatch($@"OB{TwoDigits(day.Year)}\d{{6}}$");
        first.BillNumber.ShouldBe(lastNumber[..^10] + $"OB{TwoDigits(day.Year + 1)}000001", "the facility's OP bills start again from 1 in the new year");

        // Each is printed with the date and time it was finalized, in India; printed after midnight, the old year's last bill
        // stays in the old year.
        var lastPrinted = await DocumentAsync(billing, last.Id);
        lastPrinted.ShouldContain(lastNumber);
        lastPrinted.ShouldContain($"31 Dec {day.Year}, 11:59 pm");
        (await DocumentAsync(billing, first.Id)).ShouldContain($"01 Jan {day.Year + 1}, 12:00 am");
    }

    private static bool IsNewYearsEve(DateOnly day) => day is { Month: 12, Day: 31 };

    private static bool IsOrdinaryDay(DateOnly day) => !IsNewYearsEve(day);

    /// <summary>Charges the bed and nursing days due so far, as the hourly bed-day job does.</summary>
    private Task RunBedDayJobAsync() => Server.Services.GetServices<IHostedService>().OfType<BedDayChargeJob>().Single().RunOnceAsync(Ct);

    /// <summary>The bed and nursing day charges billing holds for the stay.</summary>
    private static async Task<List<ChargeDto>> DayChargesAsync(ApiClient billing, Guid admissionId) =>
        (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?admissionId={admissionId}&pageSize=200")).Items
        .Where(c => c.Status != ChargeStatus.Cancelled && c.SourceModule is DailyChargeKeys.BedSource or DailyChargeKeys.NursingSource)
        .ToList();

    /// <summary>Front office: admits a new patient under the doctor, now, to a new bed of the general male ward.</summary>
    private async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var patientId = await RegisterAsync(frontOffice, Gender.Male);
        var bed = await TestBeds.AddAsync(Server, "GWM");
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Community-acquired pneumonia", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    /// <summary>A clinic of the test's own, whose tokens no other test takes, so they count from 1.</summary>
    private static async Task<Clinic> ClinicAsync(ApiClient admin)
    {
        var code = "MID" + RandomLetters(5);
        var created = await admin.PostAsync<JsonElement>("/api/v1/masters/departments", new SaveDepartmentRequest(
            code, $"Night clinic {code}", DepartmentKind.Clinical, null, null, null, null, null, 900, true, null));
        return new Clinic(created.GetProperty("id").GetGuid(), code);
    }

    /// <summary>Front office: checks the patient in to the clinic with a queue token, as a walk-in.</summary>
    private static async Task<QueueTokenDto> TokenAsync(ApiClient frontOffice, Guid patientId, Clinic clinic)
    {
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.OutPatient, clinic.Id, null, null, PayerType.Self, null, ReferralSource.Self, null, "Fever since the evening",
            false, null, null, TokenPriority.Normal, IssueToken: true, NotifyPatient: false));
        return visit.Token.ShouldNotBeNull();
    }

    /// <summary>The clinic's queue today, as the front desk and the doctor see it.</summary>
    private static Task<List<QueueEntry>> QueueAsync(ApiClient frontOffice, Clinic clinic) =>
        frontOffice.GetAsync<List<QueueEntry>>($"/api/v1/queue?departmentId={clinic.Id}");

    /// <summary>A dressing at ₹450 for the bills, in use at that price once the other administrator approves it.</summary>
    private Task<ServiceItemDto> DressingAsync(ApiClient admin) => Server.CreateServiceAsync(admin, new SaveServiceItemRequest(
        "SV-MID" + RandomLetters(6), "Dressing (night)", null, ServiceCategory.Procedure, null, 450m, 0m, null, true, false, null, true, null));

    /// <summary>Billing: an OP bill for the patient with one dressing on it, not finalized yet.</summary>
    private static async Task<BillDetail> DraftAsync(ApiClient billing, Guid patientId, ServiceItemDto service)
    {
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, patientId, null));
        return await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/lines", new AddBillLineRequest(service.Id, 1m, null, null, null, null, draft.Version));
    }

    /// <summary>Billing finalizes the bill: it takes its number and its date then.</summary>
    private static Task<BillDetail> FinalizeAsync(ApiClient billing, BillDetail draft) =>
        billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));

    /// <summary>The bill as the counter prints it.</summary>
    private static async Task<string> DocumentAsync(ApiClient billing, Guid billId) =>
        (await billing.GetAsync<PrintableDocumentDto>($"/api/v1/billing/bills/{billId}/document")).Html;

    /// <summary>Front office: registers a new patient, the duplicate check on (the first name is the run's own).</summary>
    private static async Task<Guid> RegisterAsync(ApiClient frontOffice, Gender gender = Gender.Female) =>
        (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: TestPatients.FirstName(gender == Gender.Male ? "Ravi" : "Lakshmi"), MiddleName: null, LastName: "Night" + RandomLetters(6),
            Gender: gender, DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(365 * 30)), AgeYears: null, AgeMonths: null,
            Phone: "9" + RandomDigits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null,
            GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null,
            Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null,
            PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false))).Id;

    /// <summary>The year's last two digits, as {YY} writes them in a number.</summary>
    private static string TwoDigits(int year) => (year % 100).ToString("00", CultureInfo.InvariantCulture);

    private static string RandomLetters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));

    private static string RandomDigits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    /// <summary>A clinic of the test's own: its id, and its code, which its tokens carry (it has no token prefix).</summary>
    private sealed record Clinic(Guid Id, string Code);
}
