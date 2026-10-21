using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.DoctorFees;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A doctor's fee statement for a period (DFA-01..05) from bills that accrued fees, through the real API: what each bill
/// line earned under the doctor's sharing rules, what a cancelled bill took back, and the payouts with their deductions,
/// TDS and net. The doctor is the test's own, so no other test's bills reach the statement.
/// </summary>
public sealed class DoctorFeeStatementTests(HospitalServer server)
{
    private const string Fees = "/api/v1/doctor-fees";

    [Fact]
    public async Task A_statement_lists_the_fees_bills_earned_in_the_period_and_the_payouts_with_deductions_tds_and_net()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var today = await HospitalDayAsync();

        // The doctor and his sharing rules: 40 % of what procedures are billed at, except suturing, which earns him a fixed
        // ₹150 for each one. These are his own rules, so they win over the hospital's default for procedures.
        var doctor = await DoctorAsync(admin);
        var debridement = await ServiceAsync(admin, "Wound debridement", 2000m);
        var suturing = await ServiceAsync(admin, "Suturing of a laceration", 1000m);
        var dressing = (await admin.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=PROC-DRESS")).Single(s => s.Code == "PROC-DRESS");
        await RuleAsync(accountant, doctor.Id, null, ServiceCategory.Procedure, ShareMethod.Percentage, 40m, today);
        await RuleAsync(accountant, doctor.Id, suturing.Id, null, ShareMethod.FixedPerUnit, 150m, today);

        // Two bills finalized today with his services, and a dressing that names no doctor.
        var first = await BillAsync(billing, (debridement, 1m, doctor.Id), (suturing, 2m, doctor.Id), (dressing, 1m, null));
        var second = await BillAsync(billing, (debridement, 1m, doctor.Id));

        // ------------------------------------------------------------------ the period's fees, paid out

        // The accountant closes the period: his unpaid fees, less an advance already paid to him and TDS at the hospital's
        // 10 % (section 194J) on the gross. The hospital administrator approves the payout and it is paid by bank transfer.
        var created = await accountant.PostAsync<JsonElement>($"{Fees}/payouts", new CreatePayoutRequest(
            doctor.Id, today, today, [new PayoutDeductionInput("Advance paid on the 1st", 200m)], null, "Fees for today"));
        var payoutId = created.GetProperty("id").GetGuid();
        var payout = await admin.GetAsync<PayoutDto>($"{Fees}/payouts/{payoutId}");
        await admin.PostAsync($"{Fees}/payouts/{payoutId}/approve", new PayoutVersionRequest(payout.Version));
        payout = await admin.GetAsync<PayoutDto>($"{Fees}/payouts/{payoutId}");
        await accountant.PostAsync($"{Fees}/payouts/{payoutId}/pay", new PayPayoutRequest(PaymentMode.NetBanking, "UTR" + Digits(12), payout.Version));

        var statement = await accountant.GetAsync<DoctorStatement>(StatementPath(doctor.Id, today, today));
        statement.PractitionerName.ShouldBe(doctor.Name);
        (statement.From, statement.To).ShouldBe((today, today));
        Fee(statement).ShouldBe([
            (first.BillNumber!, "Suturing of a laceration", 2000m, ShareMethod.FixedPerUnit, 150m, 300m, AccrualStatus.Paid, false),
            (first.BillNumber!, "Wound debridement", 2000m, ShareMethod.Percentage, 40m, 800m, AccrualStatus.Paid, false),
            (second.BillNumber!, "Wound debridement", 2000m, ShareMethod.Percentage, 40m, 800m, AccrualStatus.Paid, false),
        ]);
        statement.Accruals.ShouldAllBe(a => a.AccruedOn == today && a.PayoutId == payoutId);
        Totals(statement).ShouldBe((1900m, 0m, 0m, 1900m, 0m));

        var paid = statement.Payouts.ShouldHaveSingleItem();
        (paid.Id, paid.Status, paid.PeriodFrom, paid.PeriodTo, paid.AccrualCount).ShouldBe((payoutId, PayoutStatus.Paid, today, today, 3));
        Amounts(paid).ShouldBe((1900m, 200m, 10m, 190m, 1510m), "net = gross − TDS on the gross − deductions");
        paid.Deductions.Select(d => (d.Description, d.Amount)).ShouldBe([("Advance paid on the 1st", 200m)]);

        // ------------------------------------------------------------------ a cancelled bill, and the next payout

        // The second bill turns out to be wrong and is cancelled after its fee was paid out: a reversal takes the fee back,
        // to be recovered from his next payout. A third bill earns him more fees the same day.
        await admin.PostAsync<BillDetail>($"/api/v1/billing/bills/{second.Id}/cancel", new CancelBillRequest("Billed to the wrong patient", second.Version));
        var third = await BillAsync(billing, (debridement, 1m, doctor.Id), (suturing, 1m, doctor.Id));

        // The next payout nets the recovery against the new fees.
        created = await accountant.PostAsync<JsonElement>($"{Fees}/payouts", new CreatePayoutRequest(doctor.Id, today, today, null, null, null));
        var nextId = created.GetProperty("id").GetGuid();

        statement = await accountant.GetAsync<DoctorStatement>(StatementPath(doctor.Id, today, today));
        Fee(statement).ShouldBe([
            (first.BillNumber!, "Suturing of a laceration", 2000m, ShareMethod.FixedPerUnit, 150m, 300m, AccrualStatus.Paid, false),
            (first.BillNumber!, "Wound debridement", 2000m, ShareMethod.Percentage, 40m, 800m, AccrualStatus.Paid, false),
            (second.BillNumber!, "Reversal: Wound debridement", -2000m, ShareMethod.Percentage, 40m, -800m, AccrualStatus.InPayout, true),
            (second.BillNumber!, "Wound debridement", 2000m, ShareMethod.Percentage, 40m, 800m, AccrualStatus.Paid, false),
            (third.BillNumber!, "Suturing of a laceration", 1000m, ShareMethod.FixedPerUnit, 150m, 150m, AccrualStatus.InPayout, false),
            (third.BillNumber!, "Wound debridement", 2000m, ShareMethod.Percentage, 40m, 800m, AccrualStatus.InPayout, false),
        ]);
        Totals(statement).ShouldBe((2850m, 800m, 150m, 1900m, 0m), "earned less reversed is what is paid out, in a payout or unpaid");

        statement.Payouts.Select(p => p.Id).ShouldBe([payoutId, nextId], ignoreOrder: true);
        var next = statement.Payouts.Single(p => p.Id == nextId);
        (next.Status, next.AccrualCount).ShouldBe((PayoutStatus.Draft, 3));
        Amounts(next).ShouldBe((150m, 0m, 10m, 15m, 135m));
        next.Deductions.ShouldBeEmpty();

        // ------------------------------------------------------------------ the period

        // A period before today has none of it: fees are dated the day the bill was finalized, and the payouts were for today.
        var before = await accountant.GetAsync<DoctorStatement>(StatementPath(doctor.Id, today.AddDays(-30), today.AddDays(-1)));
        before.Accruals.ShouldBeEmpty();
        before.Payouts.ShouldBeEmpty();
        Totals(before).ShouldBe((0m, 0m, 0m, 0m, 0m));

        // A period that ends before it starts is refused.
        using var backwards = await accountant.SendRawAsync(HttpMethod.Get, StatementPath(doctor.Id, today, today.AddDays(-1)));
        backwards.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------------------- steps

    /// <summary>The admin: adds a consultant to the practitioner master, with a code no other test uses.</summary>
    private static async Task<(Guid Id, string Name)> DoctorAsync(ApiClient admin)
    {
        var surname = Letters(8);
        var lastName = surname[0] + surname[1..].ToLowerInvariant();
        var created = await admin.PostAsync<JsonElement>("/api/v1/masters/practitioners", new SavePractitionerRequest(
            Code: $"DF{Letters(8)}", Title: "Dr.", FirstName: "Arjun", LastName: lastName, Gender: Gender.Male, Qualification: "MS (General Surgery)",
            RegistrationCouncil: "Tamil Nadu Medical Council", RegistrationNumber: Digits(6), SpecialtyId: null, DepartmentId: null,
            Kind: PractitionerKind.Visiting, Phone: null, Email: null, ConsultationFeeNew: 0m, ConsultationFeeFollowUp: 0m, FollowUpValidityDays: 0,
            Languages: null, Bio: null, PhotoFileId: null, SignatureFileId: null, AvailableForOnlineBooking: false, AvailableForTelehealth: false,
            FacilityIds: null, IsActive: true, Version: null));
        return (created.GetProperty("id").GetGuid(), $"Dr. Arjun {lastName}");
    }

    /// <summary>The admin: adds a procedure to the service catalogue at a price, without GST.</summary>
    private static Task<ServiceItemDto> ServiceAsync(ApiClient admin, string name, decimal price) =>
        admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            $"SV-{Letters(8)}", name, null, ServiceCategory.Procedure, null, price, 0m, null, true, false, null, true, null));

    /// <summary>The accountant: a sharing rule for the doctor, in force from today.</summary>
    private static Task RuleAsync(
        ApiClient accountant, Guid doctorId, Guid? serviceItemId, ServiceCategory? category, ShareMethod method, decimal value, DateOnly from) =>
        accountant.PostAsync($"{Fees}/rules", new SaveShareRuleRequest(doctorId, serviceItemId, category, null, method, value, from, null, true, null, null));

    /// <summary>
    /// Billing desk: bills a new patient's services, each with its doctor (or none), and finalizes the bill, which
    /// accrues the doctors' fees.
    /// </summary>
    private async Task<BillDetail> BillAsync(ApiClient billing, params (ServiceItemDto Service, decimal Quantity, Guid? DoctorId)[] lines)
    {
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, await PatientAsync(), null));
        foreach (var (service, quantity, doctorId) in lines)
        {
            draft = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/lines",
                new AddBillLineRequest(service.Id, quantity, null, null, doctorId, null, draft.Version));
        }

        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        bill.Status.ShouldBe(BillStatus.Finalized);
        return bill;
    }

    /// <summary>Front office: registers a patient that no other test uses (random surname, date of birth and mobile number).</summary>
    private async Task<Guid> PatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var surname = Letters(8);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: TestPatients.FirstName("Meena"), MiddleName: null, LastName: surname[0] + surname[1..].ToLowerInvariant(), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1965, 1, 1).AddDays(Random.Shared.Next(365 * 10)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
        return created.Id;
    }

    // ---------------------------------------------------------------------- look-ups

    /// <summary>
    /// Today in the hospital's calendar (IST). Fees are dated the day they are earned, so the test must not straddle
    /// midnight: started in the last two minutes of a day, it waits for the next.
    /// </summary>
    private async Task<DateOnly> HospitalDayAsync()
    {
        var clock = server.Services.GetRequiredService<TimeProvider>();
        var untilMidnight = BillingTime.StartOfDay(BillingTime.Today(clock).AddDays(1)) - clock.GetUtcNow();
        if (untilMidnight < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(untilMidnight + TimeSpan.FromSeconds(1), clock, TestContext.Current.CancellationToken);
        }

        return BillingTime.Today(clock);
    }

    private static string StatementPath(Guid doctorId, DateOnly from, DateOnly to) =>
        $"{Fees}/statements?practitionerId={doctorId}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";

    /// <summary>Each fee line of a statement (bill, service, billed value, rule, share, status, reversal), in a fixed order.</summary>
    private static (string, string, decimal, ShareMethod, decimal, decimal, AccrualStatus, bool)[] Fee(DoctorStatement statement) =>
        statement.Accruals
            .Select(a => (a.BillNumber, a.Description, a.BasisAmount, a.Method, a.Rate, a.ShareAmount, a.Status, a.IsReversal))
            .OrderBy(a => a.BillNumber, StringComparer.Ordinal)
            .ThenBy(a => a.Description, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Earned, reversed, in a payout, paid out and unpaid.</summary>
    private static (decimal, decimal, decimal, decimal, decimal) Totals(DoctorStatement statement) =>
        (statement.Accrued, statement.Reversed, statement.InPayout, statement.PaidOut, statement.Unpaid);

    /// <summary>Gross, deductions, TDS percent, TDS and net of a payout.</summary>
    private static (decimal, decimal, decimal, decimal, decimal) Amounts(PayoutDto payout) =>
        (payout.GrossAmount, payout.DeductionAmount, payout.TdsPercent, payout.TdsAmount, payout.NetAmount);

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
