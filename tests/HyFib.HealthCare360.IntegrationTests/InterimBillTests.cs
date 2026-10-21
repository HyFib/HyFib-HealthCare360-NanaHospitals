using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Billing.Features.Packages;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Interim bills (IPB-05) through the real API. A self-paying patient's stay is billed in parts while he is in: an
/// interim bill takes the services not billed yet up to a day, no later bill carries them again, and the final bill holds
/// only what no interim bill took. Insured, scheme and package stays are billed once, at discharge: the payer's share
/// and the package's cover are worked out over the whole stay on the final bill, so they get no interim bill.
/// </summary>
public sealed class InterimBillTests(HospitalServer server)
{
    private const decimal Advance = 10_000m;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_self_paying_stay_is_billed_in_parts_and_the_final_bill_carries_only_what_no_interim_bill_took()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);

        // Admitted at 10:00 (IST) the day before yesterday, recorded as a late entry, so the stay crosses two midnights.
        var today = await HospitalDayAsync();
        var dayBefore = today.AddDays(-2);
        var yesterday = today.AddDays(-1);
        var patientId = await RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patientId, PayerType.Self, null, IndiaClock.AtLocal(dayBefore, new TimeOnly(10, 0)));
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}");
        account.PayerType.ShouldBe(PayerType.Self);
        account.PayerId.ShouldBeNull();
        account.PackageId.ShouldBeNull();

        // Billing counter: takes an advance at admission.
        var shift = await OpenShiftAsync(billing);
        var deposit = await billing.PostAsync<ReceiptDto>($"/api/v1/billing/ip-accounts/{account.Id}/deposits",
            new CollectDepositRequest([Cash(Advance)], "Advance at admission"));
        deposit.Purpose.ShouldBe(ReceiptPurpose.Deposit);

        // The stay: every midnight spent in the bed is charged a bed day and a nursing day, and the consultant's visits
        // yesterday and today are charged. (A visit must be entered within 48 hours of it, and the evening of the day of
        // admission may be further back than that by now, so none is entered for that day.) The bed-day job runs hourly;
        // it is run here, so what the bills below carry does not depend on when it wakes.
        await RecordVisitAsync(doctor, admission.Id, IndiaClock.AtLocal(yesterday, new TimeOnly(11, 0)));
        await RecordVisitAsync(doctor, admission.Id, null);
        await PostDueBedDaysAsync(admission.Id);

        var charges = await ChargesAsync(billing, admission.Id);
        var upToYesterday = charges.Where(c => IndiaClock.LocalDate(c.ServiceDate) <= yesterday).ToList();
        upToYesterday.Select(c => (IndiaClock.LocalDate(c.ServiceDate), c.ServiceCode)).Order().ToArray().ShouldBe([
            (dayBefore, "BED-GEN"), (dayBefore, "NURS-DAY"),
            (yesterday, "BED-GEN"), (yesterday, "NURS-DAY"), (yesterday, "VISIT-DR"),
        ]);
        var todaysVisit = charges.Where(c => IndiaClock.LocalDate(c.ServiceDate) == today).ShouldHaveSingleItem();
        todaysVisit.ServiceCode.ShouldBe("VISIT-DR");

        // Billing desk: an interim bill of everything up to yesterday, that day included. It is an in-patient bill but not
        // the final bill, and with no payer the patient pays all of it.
        var interim = await billing.PostAsync<BillDetail>(Path(account, "interim-bills"), new CreateInterimBillRequest(yesterday));
        interim.Type.ShouldBe(BillType.InPatient);
        interim.IsFinalBill.ShouldBeFalse();
        interim.Status.ShouldBe(BillStatus.Draft);
        interim.AdmissionId.ShouldBe(admission.Id);
        interim.IpAccountId.ShouldBe(account.Id);
        interim.Lines.Select(l => l.ChargeId).Order().ToArray()
            .ShouldBe(upToYesterday.Select(c => c.Id).Order().ToArray(), "every service up to yesterday, and not today's visit");
        interim.NetAmount.ShouldBe(upToYesterday.Sum(c => c.NetAmount));
        interim.PayerId.ShouldBeNull();
        interim.PayerAmount.ShouldBe(0m);
        interim.PatientAmount.ShouldBe(interim.NetAmount);

        // The draft holds its services: a second interim bill of the same days finds nothing left to bill.
        await RefusedAsync(billing, account, yesterday, "billing.nothing_to_bill");

        // Finalized, it takes an in-patient bill number and its services are billed; today's visit is still to be billed.
        interim = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{interim.Id}/finalize", new BillVersionRequest(interim.Version));
        interim.Status.ShouldBe(BillStatus.Finalized);
        interim.BillNumber.ShouldNotBeNull().ShouldMatch(@"IB\d{9}$", "in-patient bills are numbered {FAC}IB{YY}{SEQ:7}");
        interim.DueAmount.ShouldBe(interim.PatientAmount);
        var running = await billing.GetAsync<RunningBill>(Path(account, "running-bill"));
        running.Lines.Where(l => l.BillId == interim.Id).Select(l => (l.ChargeId, l.Status)).Order().ToArray()
            .ShouldBe(upToYesterday.Select(c => (c.Id, ChargeStatus.Billed)).Order().ToArray());
        running.UnbilledNet.ShouldBe(todaysVisit.NetAmount);

        // Billing counter: the family pays the interim bill.
        await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(interim.Id, [Cash(interim.DueAmount)], "Interim bill"));
        (await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{interim.Id}")).Status.ShouldBe(BillStatus.Paid);

        // Without a day, an interim bill takes everything up to today: here today's visit alone.
        var second = await billing.PostAsync<BillDetail>(Path(account, "interim-bills"), new CreateInterimBillRequest(null));
        second.IsFinalBill.ShouldBeFalse();
        second.Lines.ShouldHaveSingleItem().ChargeId.ShouldBe(todaysVisit.Id);
        second = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{second.Id}/finalize", new BillVersionRequest(second.Version));
        second.DueAmount.ShouldBe(todaysVisit.NetAmount);

        // Doctor: orders a dressing before discharge (Billing charges a procedure when it is ordered).
        var dressing = await OrderDressingAsync(doctor, billing, patientId, admission);

        // Nurse: initiates the discharge. The final bill drafted then holds the dressing alone: every other service of the
        // stay is on an interim bill. No midnight has passed since the last bed day, so none is charged.
        await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/initiate",
            new InitiateDischargeRequest(today, "Discharge advised on the ward round"));
        var final = await billing.PostAsync<BillDetail>(Path(account, "final-bill"));
        final.IsFinalBill.ShouldBeTrue();
        final.Lines.ShouldHaveSingleItem().ChargeId.ShouldBe(dressing.Id, "no service is billed twice");
        final = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{final.Id}/finalize", new BillVersionRequest(final.Version));
        final.DueAmount.ShouldBe(dressing.NetAmount);

        // Billing desk: settles the account. The advance pays what is still due on the open interim bill and on the final
        // bill, and what is left of it is to be refunded.
        var settled = await billing.PostAsync<SettlementResult>(Path(account, "settle"));
        settled.FinalBillId.ShouldBe(final.Id);
        settled.AdvanceAdjusted.ShouldBe(second.DueAmount + final.DueAmount);
        settled.RemainingDue.ShouldBe(0m);
        settled.RefundId.ShouldNotBeNull();
        settled.RefundRequested.ShouldBe(Advance - settled.AdvanceAdjusted);
        foreach (var bill in new[] { second, final })
        {
            var paid = await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}");
            paid.Status.ShouldBe(BillStatus.Paid);
            paid.Receipts.ShouldHaveSingleItem().Modes.ShouldBe(nameof(PaymentMode.Advance));
        }

        // Nurse: billing has cleared him, and she completes the discharge.
        (await nurse.GetAsync<DischargeClearanceDto>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/clearance")).Cleared.ShouldBeTrue();
        await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/complete",
            new CompleteDischargeRequest(DischargeType.Normal, null, "Discharged home", NotifyPatient: false));
        (await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/{account.Id}")).Status.ShouldBe(IpAccountStatus.Discharged);

        // Interim bills are for the stay: once he has left there are none.
        await RefusedAsync(billing, account, null, "billing.ip_account_inactive");

        await CloseShiftAsync(billing, shift, Advance + interim.NetAmount);
    }

    [Fact]
    public async Task Insured_scheme_and_package_stays_are_billed_once_at_discharge_so_they_get_no_interim_bill()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var payers = await billing.GetAsync<List<PayerListItem>>("/api/v1/insurance/payers");

        // A cashless admission with an insurer, and a CGHS beneficiary's on credit. The payer's share (co-pay, what it
        // never pays, and at most the amount it approved) is worked out on the final bill, and claimed from it.
        foreach (var (payerType, code) in new[] { (PayerType.Insurance, "STAR"), (PayerType.GovernmentScheme, "CGHS") })
        {
            var payer = payers.Single(p => p.Code == code);
            var (insured, insuredAccount) = await AdmitWithAVisitAsync(frontOffice, doctor, billing, payerType, payer.Id);
            insuredAccount.PayerId.ShouldBe(payer.Id);

            await RefusedAsync(billing, insuredAccount, null, "billing.interim_insured");
            (await BillsAsync(billing, insured.Id)).ShouldBeEmpty();

            // Meanwhile the running bill shows the stay and the payer's estimated share.
            var running = await billing.GetAsync<RunningBill>(Path(insuredAccount, "running-bill"));
            running.UnbilledNet.ShouldBeGreaterThan(0m);
            running.Insurance.ShouldNotBeNull().PayerId.ShouldBe(payer.Id);
        }

        // A self-paying stay at a package: what the package absorbs is worked out over the final bill's services, so the
        // stay is not billed in parts either.
        var package = await PackageAsync(await server.SignInAsync(HospitalServer.Users.HospitalAdmin));
        var (packaged, account) = await AdmitWithAVisitAsync(frontOffice, doctor, billing, PayerType.Self, null);
        await billing.PutAsync(Path(account, "package"), new AssignPackageRequest(package.Id, account.Version));

        await RefusedAsync(billing, account, null, "billing.interim_package");
        (await BillsAsync(billing, packaged.Id)).ShouldBeEmpty();

        // Taken off the package, the stay is billed in parts like any self-paying stay.
        account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/{account.Id}");
        await billing.PutAsync(Path(account, "package"), new AssignPackageRequest(null, account.Version));
        var interim = await billing.PostAsync<BillDetail>(Path(account, "interim-bills"), new CreateInterimBillRequest(null));
        interim.Lines.ShouldHaveSingleItem().ServiceCode.ShouldBe("VISIT-DR");
        interim.PackageCoveredAmount.ShouldBe(0m);
    }

    // ---------------------------------------------------------------------- steps

    /// <summary>
    /// Front office: registers a new patient. The random surname, date of birth and mobile number keep the duplicate check
    /// quiet, as no other patient has them.
    /// </summary>
    private static async Task<Guid> RegisterPatientAsync(ApiClient frontOffice)
    {
        var surname = Letters(8);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Suresh"), MiddleName: null, LastName: surname[0] + surname[1..].ToLowerInvariant(), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1955, 1, 1).AddDays(Random.Shared.Next(365 * 10)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        return created.Id;
    }

    /// <summary>Front office: admits the patient under the doctor to a free bed of the male general ward.</summary>
    private static async Task<AdmissionCreated> AdmitAsync(
        ApiClient frontOffice, ApiClient doctor, Guid patientId, PayerType payerType, Guid? payerId, DateTimeOffset? admittedAt)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWM");
        var bed = (await frontOffice.GetAsync<List<AvailableBedDto>>($"/api/v1/beds/available?patientId={patientId}&wardId={ward.Id}"))
            .First(b => b.IsEligible && b.Status == BedStatus.Vacant);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Cellulitis of the right leg", ReferredBy: null, PayerType: payerType, PayerId: payerId, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: admittedAt, Notes: null));
    }

    /// <summary>A patient admitted now and seen once by the consultant, with his IP account.</summary>
    private static async Task<(AdmissionCreated Admission, IpAccountDetail Account)> AdmitWithAVisitAsync(
        ApiClient frontOffice, ApiClient doctor, ApiClient billing, PayerType payerType, Guid? payerId)
    {
        var admission = await AdmitAsync(frontOffice, doctor, await RegisterPatientAsync(frontOffice), payerType, payerId, null);
        await RecordVisitAsync(doctor, admission.Id, null);
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}");
        account.PayerType.ShouldBe(payerType);
        return (admission, account);
    }

    /// <summary>Doctor: records a ward-round visit at a time of the stay (now when none is given), which is charged.</summary>
    private static async Task RecordVisitAsync(ApiClient doctor, Guid admissionId, DateTimeOffset? visitedAt)
    {
        var visit = await doctor.PostAsync<ConsultantVisitDto>($"/api/v1/inpatient/admissions/{admissionId}/consultant-visits",
            new RecordConsultantVisitRequest(null, visitedAt, "Seen on the ward round"));
        visit.IsCharged.ShouldBeTrue(visit.ChargeError);
    }

    /// <summary>
    /// Charges the bed and nursing days due so far, as the hourly bed-day job (<c>BedDayChargeJob</c>) does: each day is
    /// charged once, whoever posts it first.
    /// </summary>
    private async Task PostDueBedDaysAsync(Guid admissionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<InpatientChargeService>().PostDueBedDaysAsync(admissionId, null, Ct);
    }

    /// <summary>Doctor: orders a dressing for the in-patient; returns the charge Billing posted for it.</summary>
    private static async Task<ChargeDto> OrderDressingAsync(ApiClient doctor, ApiClient billing, Guid patientId, AdmissionCreated admission)
    {
        var dressing = (await doctor.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=PROC-DRESS")).Single(s => s.Code == "PROC-DRESS");
        await doctor.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(
            patientId, null, admission.VisitId, admission.Id, OrderPriority.Routine, "Dressing of the leg before discharge",
            [new OrderLineRequest(dressing.Id, null)], null, false));
        return (await ChargesAsync(billing, admission.Id)).Where(c => c.ServiceCode == "PROC-DRESS").ShouldHaveSingleItem();
    }

    /// <summary>The admin: a package for admissions with the bed days and the consultant's visits of a short stay.</summary>
    private static async Task<PackageDetail> PackageAsync(ApiClient admin)
    {
        const string name = "Cellulitis care (3 days)";
        var item = await admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            $"SV-{Letters(8)}", name, null, ServiceCategory.Package, null, 9000m, 0m, null, true, false, null, true, null));
        return await admin.PostAsync<PackageDetail>("/api/v1/billing/packages", new SavePackageRequest(
            $"PKG-{Letters(8)}", name, item.Id, 9000m, 3, null, PackageUse.InPatient, null, true,
            [
                new PackageInclusionInput(null, ServiceCategory.BedCharge, 3m, null, "Three bed days"),
                new PackageInclusionInput(null, ServiceCategory.DoctorVisit, 3m, null, "A consultant visit a day"),
            ],
            null));
    }

    /// <summary>Billing desk: asks for an interim bill and is refused with the rule's code (422).</summary>
    private static async Task RefusedAsync(ApiClient billing, IpAccountDetail account, DateOnly? upTo, string code)
    {
        using var response = await billing.SendRawAsync(HttpMethod.Post, Path(account, "interim-bills"), new CreateInterimBillRequest(upTo));
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(code);
    }

    /// <summary>
    /// Billing counter: opens the cashier's shift for the money this test collects (counter money needs one); null when a
    /// shift is open already.
    /// </summary>
    private static async Task<ShiftDto?> OpenShiftAsync(ApiClient billing)
    {
        using var current = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current");
        current.EnsureSuccessStatusCode();
        return current.StatusCode == HttpStatusCode.NoContent
            ? await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("IP billing", 0m))
            : null;
    }

    /// <summary>Closes the shift the test opened, with exactly the cash it took, so no other test finds it open.</summary>
    private static async Task CloseShiftAsync(ApiClient billing, ShiftDto? opened, decimal cash)
    {
        if (opened is null)
        {
            return;
        }

        var shift = await billing.GetAsync<ShiftDto>($"/api/v1/billing/shifts/{opened.Id}");
        shift.ExpectedCash.ShouldBe(cash);
        var closed = await billing.PostAsync<ShiftDto>($"/api/v1/billing/shifts/{opened.Id}/close", new CloseShiftRequest(cash, null, shift.Version));
        closed.Variance.ShouldBe(0m);
    }

    // ---------------------------------------------------------------------- look-ups

    /// <summary>
    /// Today in the hospital's calendar (IST). Bed days are charged at midnight, so the test must not straddle one:
    /// started in the last two minutes of a day, it waits for the next.
    /// </summary>
    private async Task<DateOnly> HospitalDayAsync()
    {
        var clock = server.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var untilMidnight = IndiaClock.StartOfDayUtc(IndiaClock.LocalDate(now).AddDays(1)) - now;
        if (untilMidnight < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(untilMidnight + TimeSpan.FromSeconds(1), clock, Ct);
        }

        return IndiaClock.LocalDate(clock.GetUtcNow());
    }

    private static async Task<List<ChargeDto>> ChargesAsync(ApiClient billing, Guid admissionId) =>
        (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?admissionId={admissionId}&pageSize=200"))
        .Items.Where(c => c.Status != ChargeStatus.Cancelled).ToList();

    private static async Task<IReadOnlyList<BillListItem>> BillsAsync(ApiClient billing, Guid admissionId) =>
        (await billing.GetAsync<PagedResult<BillListItem>>($"/api/v1/billing/bills?admissionId={admissionId}")).Items;

    private static string Path(IpAccountDetail account, string action) => $"/api/v1/billing/ip-accounts/{account.Id}/{action}";

    private static PaymentLineInput Cash(decimal amount) => new(PaymentMode.Cash, amount, null, null, null, null, null, null);

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
