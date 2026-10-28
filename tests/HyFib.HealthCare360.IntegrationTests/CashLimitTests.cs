using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CollectPaymentRequest = HyFib.HealthCare360.Modules.Billing.Features.Payments.CollectPaymentRequest;
using PharmacyCollectPaymentRequest = HyFib.HealthCare360.Modules.Pharmacy.Features.Sales.CollectPaymentRequest;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;
using SalePayment = HyFib.HealthCare360.Modules.Pharmacy.Domain.SalePayment;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The cash limit of the Income-tax Act, over the real API. No one may take ₹2,00,000 or more in cash from a person in a
/// day, for a single transaction, or for one event or occasion (s.269ST); the penalty is the whole sum taken (s.271DA).
/// Only an account-payee cheque or draft and the bank modes of rule 6ABBA (cards, net banking, IMPS, UPI, RTGS, NEFT, BHIM
/// Aadhaar Pay) fall outside it, so a wallet counts as cash. HC360 counts the cash a patient pays at the billing counter
/// and at the pharmacy, at every hospital under the same PAN, by the hospital's (IST) day, by admission, by bill and
/// by payment, and refuses what would reach the limit, saying how much more may be taken in cash.
/// </summary>
public sealed class CashLimitTests(HospitalServer server)
{
    private const string Pan = "AABCN1234F";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cash_that_would_make_two_lakh_is_refused_in_one_payment_for_one_admission_and_at_the_pharmacy_the_same_day()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        await HospitalDayAsync();
        var patientId = await RegisterPatientAsync(frontOffice);
        await using var stay = await AdmitAsync(frontOffice, doctor, billing, patientId);
        var account = stay.Account;
        var shift = await OpenShiftAsync(billing);

        // One payment of ₹2,00,000 in cash is refused, split into two lines or not. The counter is told what it may take.
        (await RefusedAsync(billing, Deposits(account), new CollectDepositRequest([Cash(1_00_000m), Cash(1_00_000m)], null)))
            .ShouldBe("Income-tax Act s.269ST: ₹2,00,000.00 or more cannot be taken in cash (wallets included) in one payment. "
                      + "Take less than ₹2,00,000.00 in cash and the rest by card, UPI, net banking or an account-payee cheque.");

        // ₹1,50,000 is taken at admission. ₹50,000 more in cash would make ₹2,00,000 for the admission: refused.
        await billing.PostAsync<ReceiptDto>(Deposits(account), new CollectDepositRequest([Cash(1_50_000m)], "Advance at admission"));
        (await RefusedAsync(billing, Deposits(account), new CollectDepositRequest([Cash(50_000m)], null)))
            .ShouldBe("Income-tax Act s.269ST: ₹2,00,000.00 or more cannot be taken in cash (wallets included) for one admission. "
                      + "₹1,50,000.00 has been taken in cash for this admission, so take less than ₹50,000.00 in cash and the rest by card, UPI, "
                      + "net banking or an account-payee cheque.");

        // Less than that is taken, and the rest by card: the limit is on cash alone.
        await billing.PostAsync<ReceiptDto>(Deposits(account), new CollectDepositRequest([Cash(49_999m), Card(50_001m)], null));

        // A wallet is not a bank payment: even ₹1 by wallet is refused now.
        (await RefusedAsync(billing, Deposits(account), new CollectDepositRequest([Wallet(1m)], null)))
            .ShouldStartWith("Income-tax Act s.269ST: ₹2,00,000.00 or more cannot be taken in cash (wallets included) for one admission.");

        // The pharmacy sells the patient's medicines. In cash, they would make ₹2,00,000 from the patient today: refused,
        // although the sale is a transaction of its own. By UPI they are sold.
        var sale = await MedicineSaleAsync(pharmacist, patientId);
        (await RefusedAsync(pharmacist, "/api/v1/pharmacy/sales/counter", sale(PharmacyPaymentMode.Cash)))
            .ShouldBe("Income-tax Act s.269ST: ₹2,00,000.00 or more cannot be taken in cash (wallets included) from one patient in a day. "
                      + "₹1,99,999.00 has been taken in cash from this patient today, so take less than ₹1.00 in cash and the rest by card, UPI, "
                      + "net banking or an account-payee cheque.");
        (await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", sale(PharmacyPaymentMode.Upi))).DueAmount.ShouldBe(0m);

        // Medicines sold on credit: the balance, collected later, is refused in cash too, and taken by UPI.
        var credit = await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", sale(PharmacyPaymentMode.Credit));
        credit.DueAmount.ShouldBeGreaterThan(0m);
        using (var finer = await pharmacist.SendRawAsync(HttpMethod.Post, $"/api/v1/pharmacy/sales/{credit.Id}/payments",
                   new PharmacyCollectPaymentRequest(PharmacyPaymentMode.Upi, 0.005m, "UTR" + Digits(9))))
        {
            finer.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "money is taken in rupees and paise");
        }

        (await RefusedAsync(pharmacist, $"/api/v1/pharmacy/sales/{credit.Id}/payments", new PharmacyCollectPaymentRequest(PharmacyPaymentMode.Cash, credit.DueAmount, null)))
            .ShouldStartWith("Income-tax Act s.269ST: ₹2,00,000.00 or more cannot be taken in cash (wallets included) from one patient in a day.");
        using (var collected = await pharmacist.SendRawAsync(HttpMethod.Post, $"/api/v1/pharmacy/sales/{credit.Id}/payments",
                   new PharmacyCollectPaymentRequest(PharmacyPaymentMode.Upi, credit.DueAmount, "UTR" + Digits(9))))
        {
            collected.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await CloseShiftAsync(billing, shift, 1_99_999m);
    }

    [Fact]
    public async Task An_admission_and_a_bill_count_the_cash_of_every_day_and_a_day_counts_its_own()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var today = await HospitalDayAsync();
        var patientId = await RegisterPatientAsync(frontOffice);
        await using var stay = await AdmitAsync(frontOffice, doctor, billing, patientId);
        var account = stay.Account;
        var shift = await OpenShiftAsync(billing);

        // ₹1,50,000 was taken in cash for the stay yesterday.
        var advance = await billing.PostAsync<ReceiptDto>(Deposits(account), new CollectDepositRequest([Cash(1_50_000m)], "Advance at admission"));
        await MoveBackADayAsync(advance.Id);

        // Today, ₹50,000 more in cash for the stay is refused: the stay is one occasion, whatever the day.
        (await RefusedAsync(billing, Deposits(account), new CollectDepositRequest([Cash(50_000m)], null)))
            .ShouldStartWith("Income-tax Act s.269ST: ₹2,00,000.00 or more cannot be taken in cash (wallets included) for one admission. "
                             + "₹1,50,000.00 has been taken in cash for this admission, so take less than ₹50,000.00 in cash");
        await billing.PostAsync<ReceiptDto>(Deposits(account), new CollectDepositRequest([Cash(49_500m)], null));

        // A bill of the stay is paid for the stay: with ₹1,99,500 taken for it, the consultant's visit (₹700) is refused in
        // cash, and paid by UPI.
        var visit = await doctor.PostAsync<ConsultantVisitDto>($"/api/v1/inpatient/admissions/{account.AdmissionId}/consultant-visits",
            new RecordConsultantVisitRequest(null, null, "Seen on the ward round"));
        visit.IsCharged.ShouldBeTrue(visit.ChargeError);
        var interim = await billing.PostAsync<BillDetail>($"/api/v1/billing/ip-accounts/{account.Id}/interim-bills", new CreateInterimBillRequest(null));
        interim = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{interim.Id}/finalize", new BillVersionRequest(interim.Version));
        interim.DueAmount.ShouldBeGreaterThanOrEqualTo(500m);
        (await RefusedAsync(billing, "/api/v1/billing/receipts", new CollectPaymentRequest(interim.Id, [Cash(interim.DueAmount)], null)))
            .ShouldStartWith("Income-tax Act s.269ST: ₹2,00,000.00 or more cannot be taken in cash (wallets included) for one admission. "
                             + "₹1,99,500.00 has been taken in cash for this admission, so take less than ₹500.00 in cash");
        await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(interim.Id, [Upi(interim.DueAmount)], null));

        // Yesterday's cash is not today's: the patient's medicines, a transaction of their own, are sold for cash.
        var sale = await MedicineSaleAsync(pharmacist, patientId);
        (await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", sale(PharmacyPaymentMode.Cash))).DueAmount.ShouldBe(0m);

        // An out-patient bill is one transaction, whatever the day: ₹1,00,000 of a ₹2,50,000 bill was paid in cash
        // yesterday, so ₹1,00,000 more in cash is refused today, and the rest is paid by card.
        var bill = await OutPatientBillAsync(frontOffice, billing, patientId, 2_50_000m);
        var first = await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(bill.Id, [Cash(1_00_000m)], null));
        await MoveBackADayAsync(first.Id);
        (await RefusedAsync(billing, "/api/v1/billing/receipts", new CollectPaymentRequest(bill.Id, [Cash(1_00_000m)], null)))
            .ShouldBe("Income-tax Act s.269ST: ₹2,00,000.00 or more cannot be taken in cash (wallets included) for one bill. "
                      + "₹1,00,000.00 has been taken in cash for this bill, so take less than ₹1,00,000.00 in cash and the rest by card, UPI, "
                      + "net banking or an account-payee cheque.");
        await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(bill.Id, [Card(1_50_000m)], null));

        // He goes home: his day in the ward is charged on the final bill, which his advance pays, and his bed goes back.
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{account.AdmissionId}/discharge/initiate",
            new InitiateDischargeRequest(today, "Discharge advised on the ward round"));
        var final = await billing.PostAsync<BillDetail>($"/api/v1/billing/ip-accounts/{account.Id}/final-bill");
        final = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{final.Id}/finalize", new BillVersionRequest(final.Version));
        var settled = await billing.PostAsync<SettlementResult>($"/api/v1/billing/ip-accounts/{account.Id}/settle");
        (settled.AdvanceAdjusted, settled.RemainingDue).ShouldBe((final.DueAmount, 0m));
        await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{account.AdmissionId}/discharge/complete",
            new CompleteDischargeRequest(DischargeType.Normal, null, "Discharged home", NotifyPatient: false));

        await CloseShiftAsync(billing, shift, 1_50_000m + 49_500m + 1_00_000m);
    }

    [Fact]
    public async Task Cash_taken_at_a_hospital_under_the_same_pan_counts_and_at_one_under_another_pan_does_not()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        await HospitalDayAsync();
        var patientId = await RegisterPatientAsync(frontOffice);
        await using var stay = await AdmitAsync(frontOffice, doctor, billing, patientId);
        var account = stay.Account;
        var shift = await OpenShiftAsync(billing);

        // A sister hospital of the same company took ₹1,50,000 in cash from the patient this morning.
        await using var sister = await SisterHospitalAsync(Pan);
        await sister.TookCashAsync(patientId, 1_50_000m);

        // The company is the one person who receives it all: ₹50,000 more in cash here is refused.
        (await RefusedAsync(billing, Deposits(account), new CollectDepositRequest([Cash(50_000m)], null)))
            .ShouldBe("Income-tax Act s.269ST: ₹2,00,000.00 or more cannot be taken in cash (wallets included) from one patient in a day. "
                      + "₹1,50,000.00 has been taken in cash from this patient today, so take less than ₹50,000.00 in cash and the rest by card, UPI, "
                      + "net banking or an account-payee cheque.");

        // A hospital of another company is another person: its cash is not counted here.
        await sister.SetPanAsync("AAACH7409R");
        await billing.PostAsync<ReceiptDto>(Deposits(account), new CollectDepositRequest([Cash(50_000m)], null));

        await CloseShiftAsync(billing, shift, 50_000m);
    }

    [Fact]
    public async Task Cash_paid_at_the_pharmacy_counts_for_the_day_at_the_billing_counter()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        await HospitalDayAsync();
        var patientId = await RegisterPatientAsync(frontOffice);
        await using var stay = await AdmitAsync(frontOffice, doctor, billing, patientId);
        var account = stay.Account;
        var shift = await OpenShiftAsync(billing);

        // The patient's medicines are paid for in cash at the pharmacy, and the billing counter then takes as much more in
        // cash as makes ₹1,99,999 from the patient today: under the limit, so both are taken.
        var sale = (await MedicineSaleAsync(pharmacist, patientId))(PharmacyPaymentMode.Cash);
        var medicines = sale.Payments.ShouldHaveSingleItem().Amount;

        // Money is taken in rupees and paise: cash a fraction of a paisa short of the price, which would be kept rounded up
        // to it, is refused.
        var finer = sale with
        {
            Payments = [new SalePayment(PharmacyPaymentMode.Cash, medicines - 0.005m, null), new SalePayment(PharmacyPaymentMode.Upi, 0.005m, "UTR" + Digits(9))],
        };
        using (var refused = await pharmacist.SendRawAsync(HttpMethod.Post, "/api/v1/pharmacy/sales/counter", finer))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", sale);
        await billing.PostAsync<ReceiptDto>(Deposits(account), new CollectDepositRequest([Cash(1_99_999m - medicines)], null));

        // ₹1 more at the billing counter would make ₹2,00,000 from the patient today, counting the pharmacy's cash: refused,
        // although the admission has had less than that.
        (await RefusedAsync(billing, Deposits(account), new CollectDepositRequest([Cash(1m)], null)))
            .ShouldBe("Income-tax Act s.269ST: ₹2,00,000.00 or more cannot be taken in cash (wallets included) from one patient in a day. "
                      + "₹1,99,999.00 has been taken in cash from this patient today, so take less than ₹1.00 in cash and the rest by card, UPI, "
                      + "net banking or an account-payee cheque.");

        await CloseShiftAsync(billing, shift, 1_99_999m - medicines);
    }

    [Fact]
    public async Task A_wallet_payment_taken_online_counts_as_cash_and_one_that_would_reach_the_limit_is_not_recorded()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var today = await HospitalDayAsync();
        var patientId = await RegisterPatientAsync(frontOffice);
        await using var stay = await AdmitAsync(frontOffice, doctor, billing, patientId);
        var account = stay.Account;
        var shift = await OpenShiftAsync(billing);

        // ₹1,50,000 was taken in cash for the stay yesterday.
        var advance = await billing.PostAsync<ReceiptDto>(Deposits(account), new CollectDepositRequest([Cash(1_50_000m)], "Advance at admission"));
        await MoveBackADayAsync(advance.Id);

        // The family pays ₹50,000 more for the stay online, from a wallet. With the cash, it would make ₹2,00,000 for the
        // admission, so the gateway's capture is not recorded: the accountants are to refund it. By UPI, a bank payment, the
        // same ₹50,000 is recorded.
        (await TakenOnlineAsync(patientId, null, account.Id, 50_000m, "wallet"))
            .ShouldBe("Income-tax Act s.269ST: ₹2,00,000.00 or more cannot be taken in cash (wallets included) for one admission. "
                      + "₹1,50,000.00 has been taken in cash for this admission, so take less than ₹50,000.00 in cash and the rest by card, UPI, "
                      + "net banking or an account-payee cheque.");
        (await TakenOnlineAsync(patientId, null, account.Id, 50_000m, "upi")).ShouldBeNull();

        // ₹49,000 from a wallet is under the limit and recorded. It counts as cash: the counter may now take less than ₹1,000
        // in cash for the stay.
        (await TakenOnlineAsync(patientId, null, account.Id, 49_000m, "wallet")).ShouldBeNull();
        (await RefusedAsync(billing, Deposits(account), new CollectDepositRequest([Cash(1_000m)], null)))
            .ShouldStartWith("Income-tax Act s.269ST: ₹2,00,000.00 or more cannot be taken in cash (wallets included) for one admission. "
                             + "₹1,99,000.00 has been taken in cash for this admission, so take less than ₹1,000.00 in cash");

        // A wallet payment the gateway took yesterday for the patient's out-patient bill, recorded only today, counts on
        // the day it was taken, with yesterday's cash.
        var bill = await OutPatientBillAsync(frontOffice, billing, patientId, 60_000m);
        (await TakenOnlineAsync(patientId, bill.Id, null, 60_000m, "wallet", DateTimeOffset.UtcNow.AddDays(-1)))
            .ShouldBe("Income-tax Act s.269ST: ₹2,00,000.00 or more cannot be taken in cash (wallets included) from one patient in a day. "
                      + $"₹1,50,000.00 has been taken in cash from this patient on {today.AddDays(-1):dd-MM-yyyy}, so take less than ₹50,000.00 in cash "
                      + "and the rest by card, UPI, net banking or an account-payee cheque.");

        await CloseShiftAsync(billing, shift, 1_50_000m);
    }

    [Fact]
    public async Task Cash_taken_for_the_same_patient_at_two_counters_at_once_is_counted_one_after_the_other()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        await HospitalDayAsync();
        var patientId = await RegisterPatientAsync(frontOffice);
        await using var stay = await AdmitAsync(frontOffice, doctor, billing, patientId);
        var account = stay.Account;
        var bill = await OutPatientBillAsync(frontOffice, billing, patientId, 2_50_000m);
        var shift = await OpenShiftAsync(billing);

        // At the same moment, one counter takes ₹1,00,000 in cash as an advance for the stay, and another ₹1,00,000 for the
        // patient's out-patient bill. Each is under the limit for what it is for, but together they make ₹2,00,000 from the
        // patient today: one is taken, and the other, counted after it, is refused.
        var attempts = await Task.WhenAll(
            AttemptAsync(billing, Deposits(account), new CollectDepositRequest([Cash(1_00_000m)], null)),
            AttemptAsync(billing, "/api/v1/billing/receipts", new CollectPaymentRequest(bill.Id, [Cash(1_00_000m)], null)));
        attempts.Order().ToArray().ShouldBe([HttpStatusCode.Created, HttpStatusCode.UnprocessableEntity]);

        await CloseShiftAsync(billing, shift, 1_00_000m);
    }

    // ---------------------------------------------------------------------- the journey

    private static async Task<Guid> RegisterPatientAsync(ApiClient frontOffice)
    {
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Ramesh"), MiddleName: null, LastName: "Cash" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        return created.Id;
    }

    /// <summary>Front office: admits the patient, paying for himself, to a free bed of the male general ward.</summary>
    private async Task<Stay> AdmitAsync(ApiClient frontOffice, ApiClient doctor, ApiClient billing, Guid patientId)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWM");
        var bed = (await frontOffice.GetAsync<List<AvailableBedDto>>($"/api/v1/beds/available?patientId={patientId}&wardId={ward.Id}"))
            .First(b => b.IsEligible && b.Status == BedStatus.Vacant);
        var admission = await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Cellulitis of the right leg", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
        return new Stay(server, frontOffice, await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}"), ward.Id);
    }

    /// <summary>
    /// The patient's stay, with his IP account. Every test of the run shares the ward's beds, so when the test ends a stay
    /// still in progress is cancelled (one that has ended is refused, and stays as it is), and the nurse cleans the bed it
    /// left: a bed is taken again only once housekeeping has made it vacant.
    /// </summary>
    private sealed class Stay(HospitalServer server, ApiClient frontOffice, IpAccountDetail account, Guid wardId) : IAsyncDisposable
    {
        public IpAccountDetail Account => account;

        public async ValueTask DisposeAsync()
        {
            (await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/inpatient/admissions/{account.AdmissionId}/cancel",
                new CancelAdmissionRequest("Admitted for a test of the cash limit"))).Dispose();
            var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
            foreach (var task in (await nurse.GetAsync<List<HousekeepingTaskDto>>($"/api/v1/beds/housekeeping?wardId={wardId}"))
                     .Where(t => t.AdmissionId == account.AdmissionId && t.Status == HousekeepingStatus.Pending))
            {
                await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/start");
                await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/complete", new BedNoteRequest("Cleaned after the stay"));
            }
        }
    }

    /// <summary>
    /// A health check-up visit charged <paramref name="amount"/> in dressings (₹250 each, no GST), billed and finalized by
    /// the billing desk.
    /// </summary>
    private static async Task<BillDetail> OutPatientBillAsync(ApiClient frontOffice, ApiClient billing, Guid patientId, decimal amount)
    {
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.HealthCheckup, null, null, null, PayerType.Self, null, ReferralSource.Self, null, null, false, null, null,
            TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
        var dressing = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=PROC-DRESS")).Single(s => s.Code == "PROC-DRESS");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(
            patientId, visit.Id, null, dressing.Id, amount / 250m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        bill.DueAmount.ShouldBe(amount);
        return bill;
    }

    /// <summary>Pharmacist: a counter sale of one strip of paracetamol to the patient, paid in full in the mode given.</summary>
    private static async Task<Func<PharmacyPaymentMode, CounterSaleRequest>> MedicineSaleAsync(ApiClient pharmacist, Guid patientId)
    {
        var store = (await pharmacist.GetAsync<List<StoreDto>>("/api/v1/inventory/stores?type=Pharmacy")).Single(s => s.Code == "OPPH");
        var medicine = (await pharmacist.GetAsync<List<PharmacyStockRow>>($"/api/v1/pharmacy/stock?storeId={store.Id}&search=paracetamol&take=5"))
            .First(r => r.Available > 0);
        var batch = (await pharmacist.GetAsync<List<BatchStock>>($"/api/v1/pharmacy/stock/batches?storeId={store.Id}&itemId={medicine.ItemId}"))
            .First(b => b.Available > 0 && !b.IsExpired && !b.IsBlocked);
        var price = Math.Round(batch.Mrp, MidpointRounding.AwayFromZero);
        return mode => new CounterSaleRequest(
            store.Id, patientId, null, null, null, [new CounterSaleLine(medicine.ItemId, 1m, batch.BatchId)], 0m, [new SalePayment(mode, price, mode == PharmacyPaymentMode.Upi ? "UTR" + Digits(9) : null)], false);
    }

    /// <summary>Posts to <paramref name="path"/>, is refused under the cash limit (422), and returns what the counter is told.</summary>
    private static async Task<string> RefusedAsync(ApiClient client, string path, object body)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        problem.GetProperty("code").GetString().ShouldBe("payments.cash_limit");
        return problem.GetProperty("detail").GetString().ShouldNotBeNull();
    }

    private static async Task<HttpStatusCode> AttemptAsync(ApiClient client, string path, object body)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path, body);
        return response.StatusCode;
    }

    /// <summary>Billing counter: opens the cashier's shift for the cash this test takes; null when a shift is open already.</summary>
    private static async Task<ShiftDto?> OpenShiftAsync(ApiClient billing)
    {
        using var current = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current");
        current.EnsureSuccessStatusCode();
        return current.StatusCode == HttpStatusCode.NoContent
            ? await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("IP billing", 0m))
            : null;
    }

    /// <summary>Closes the shift the test opened, with exactly the cash it took (refused cash is not in the drawer).</summary>
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

    /// <summary>The receipt as if the money had been taken a day earlier.</summary>
    private async Task MoveBackADayAsync(Guid receiptId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.Set<Receipt>().IgnoreQueryFilters().Where(r => r.Id == receiptId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReceivedAt, r => r.ReceivedAt.AddDays(-1)), Ct)).ShouldBe(1);
    }

    /// <summary>
    /// A payment the gateway took for the bill or the stay, made by <paramref name="method"/> (Razorpay's name for it),
    /// recorded as <c>PaymentGatewayService</c> records a capture: in a transaction of its own, dated when the gateway took
    /// it. Null when it is recorded; otherwise why it is not.
    /// </summary>
    private async Task<string?> TakenOnlineAsync(Guid patientId, Guid? billId, Guid? accountId, decimal amount, string method, DateTimeOffset? takenAt = null)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var hospital = await db.Set<Facility>().OrderBy(f => f.CreatedAt).FirstAsync(Ct);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var receipt = await scope.ServiceProvider.GetRequiredService<ReceiptService>().RecordExternalAsync(new ExternalPayment(
            hospital.Id, billId is null ? ReceiptPurpose.Deposit : ReceiptPurpose.BillPayment, patientId, billId, accountId, PaymentMode.PaymentGateway,
            amount, "pay_" + Letters(14), method, null, ReceiptSource.PaymentGateway, null, takenAt), Ct);
        if (receipt.Error is { } error)
        {
            return error.Message;
        }

        await db.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
        return null;
    }

    /// <summary>
    /// A second hospital, registered under <paramref name="pan"/>, with this hospital under <see cref="Pan"/>, for the rest
    /// of the test; then this hospital's PAN is put back and the second hospital closed, because the server is shared by
    /// every test of the run.
    /// </summary>
    private async Task<SisterHospital> SisterHospitalAsync(string pan)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var hospital = await db.Set<Facility>().OrderBy(f => f.CreatedAt).FirstAsync(Ct);
        var before = hospital.Pan;
        hospital.Pan = Pan;
        var sister = new Facility
        {
            Code = "S" + Letters(5).ToUpperInvariant(),
            Name = "Nana Hospitals, Vellore",
            Pan = pan,
            Address = new Address { City = "Vellore", State = "Tamil Nadu", Country = "India" },
        };
        db.Add(sister);
        await db.SaveChangesAsync(Ct);
        return new SisterHospital(server, hospital.Id, before, sister.Id);
    }

    private sealed class SisterHospital(HospitalServer server, Guid hospitalId, string? hospitalPan, Guid sisterId) : IAsyncDisposable
    {
        /// <summary>A cash receipt of <paramref name="amount"/> from the patient at the sister hospital, a minute ago.</summary>
        public async Task TookCashAsync(Guid patientId, decimal amount)
        {
            await using var scope = server.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var receipt = new Receipt
            {
                FacilityId = sisterId,
                ReceiptNumber = "SIS" + Digits(12),
                PatientId = patientId,
                Purpose = ReceiptPurpose.BillPayment,
                Source = ReceiptSource.Counter,
                Amount = amount,
                ReceivedAt = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().AddMinutes(-1),
            };
            receipt.AddPayment(new PaymentLineData(PaymentMode.Cash, amount));
            db.Add(receipt);
            await db.SaveChangesAsync(Ct);
        }

        public async Task SetPanAsync(string pan)
        {
            await using var scope = server.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<Facility>().Where(f => f.Id == sisterId).ExecuteUpdateAsync(s => s.SetProperty(f => f.Pan, pan), Ct)).ShouldBe(1);
        }

        public async ValueTask DisposeAsync()
        {
            await using var scope = server.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var hospital = await db.Set<Facility>().FirstAsync(f => f.Id == hospitalId, CancellationToken.None);
            hospital.Pan = hospitalPan;
            var sister = await db.Set<Facility>().FirstAsync(f => f.Id == sisterId, CancellationToken.None);
            sister.IsDeleted = true;
            sister.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    // ---------------------------------------------------------------------- look-ups

    /// <summary>
    /// Today in the hospital's calendar (IST). The limit counts a day's cash, so the test must not straddle midnight:
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

    private static string Deposits(IpAccountDetail account) => $"/api/v1/billing/ip-accounts/{account.Id}/deposits";

    private static PaymentLineInput Cash(decimal amount) => new(PaymentMode.Cash, amount, null, null, null, null, null, null);

    private static PaymentLineInput Card(decimal amount) => new(PaymentMode.Card, amount, null, "4242", "Visa", null, null, null);

    private static PaymentLineInput Upi(decimal amount) => new(PaymentMode.Upi, amount, "UTR" + Digits(9), null, null, null, null, null);

    private static PaymentLineInput Wallet(decimal amount) => new(PaymentMode.Wallet, amount, "WAL" + Digits(9), null, null, null, null, null);

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('a' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
