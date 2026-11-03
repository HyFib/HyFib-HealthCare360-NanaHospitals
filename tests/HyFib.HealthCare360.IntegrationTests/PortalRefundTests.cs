using System.Net;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Portal;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Gap #37 through the whole server: a patient sees in the portal where each of their refunds stands, with the reference
/// their bank or the counter can trace it by, and never the staff's names, the approver's remarks or the gateway's error.
/// </summary>
public sealed class PortalRefundTests(HospitalServer server)
{
    private const string Arn = "74332745123456789012345";
    private const string StaffNote = "Duplicate of an earlier request (staff note)";
    private const string GatewayError = "The beneficiary account is closed";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_patient_sees_where_each_refund_stands_and_the_reference_to_trace_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var (patientId, phone) = await RegisterAsync(frontOffice);
        var bill = await RegistrationBillAsync(billing, patientId);

        // At the counter: an online prepayment's refund asked for, approved by a second person and paid by bank transfer.
        var advance = await PrepaidAdvanceAsync(bill.FacilityId, patientId, 800m);
        var transferred = (await billing.PostAsync<CreatedId>("/api/v1/billing/refunds", new RequestRefundRequest(null, null, 300m, "Appointment cancelled", advance.Id))).Id;
        await accountant.PostAsync($"/api/v1/billing/refunds/{transferred}/approve", new ApproveRefundRequest(null));
        await billing.PostAsync($"/api/v1/billing/refunds/{transferred}/pay", new PayRefundRequest(PaymentMode.NetBanking, "NEFT0012345678"));
        var asked = (await billing.PostAsync<CreatedId>("/api/v1/billing/refunds", new RequestRefundRequest(null, null, 200m, "Appointment cancelled", advance.Id))).Id;

        // Each other stage, as the counter, the approver and the gateway leave a refund (the bill's ones first).
        var now = DateTimeOffset.UtcNow;
        var credited = await SaveAsync(bill, patientId, 10m, r =>
        {
            Approve(r, now);
            ThroughGateway(r, "rfnd_credited", now.AddDays(-3));
            r.ApplyGatewayStatus(new RefundStatusResult("rfnd_credited", PaymentRefundStatus.Processed, 10m, null, Arn, null, null), now).ShouldBeTrue();
        });
        var withBank = await SaveAsync(bill, patientId, 11m, r =>
        {
            Approve(r, now);
            ThroughGateway(r, "rfnd_pending", now.AddDays(-1));
        });
        var returned = await SaveAsync(bill, patientId, 12m, r =>
        {
            Approve(r, now);
            ThroughGateway(r, "rfnd_failed", now.AddDays(-2));
            r.ApplyGatewayStatus(new RefundStatusResult("rfnd_failed", PaymentRefundStatus.Failed, 12m, null, null, null, GatewayError), now).ShouldBeTrue();
        });
        var sendingAgain = await SaveAsync(bill, patientId, 13m, r =>
        {
            Approve(r, now);
            ThroughGateway(r, "rfnd_retry", now.AddDays(-2));
            r.ApplyGatewayStatus(new RefundStatusResult("rfnd_retry", PaymentRefundStatus.Failed, 13m, null, null, null, GatewayError), now).ShouldBeTrue();
            r.ClaimGatewayRetry(now).IsSuccess.ShouldBeTrue();
        });
        var stoppedPartWay = await SaveAsync(bill, patientId, 20m, r =>
        {
            // Sent again ten minutes ago, and the server stopped before the gateway's answer was recorded.
            Approve(r, now);
            ThroughGateway(r, "rfnd_stopped", now.AddDays(-2));
            r.ApplyGatewayStatus(new RefundStatusResult("rfnd_stopped", PaymentRefundStatus.Failed, 20m, null, null, null, GatewayError), now).ShouldBeTrue();
            r.ClaimGatewayRetry(now.AddMinutes(-10)).IsSuccess.ShouldBeTrue();
        });
        var notApproved = await SaveAsync(bill, patientId, 14m, r => r.Reject(Guid.CreateVersion7(), StaffNote, now).IsSuccess.ShouldBeTrue());
        var cash = await SaveAsync(null, patientId, 15m, r =>
        {
            Approve(r, now);
            // The counter's own voucher number for the cash it paid out: the hospital's, not one the patient can trace.
            r.MarkPaid(PaymentMode.Cash, "VCH-0042", null, null, null, now).IsSuccess.ShouldBeTrue();
        }, bill.FacilityId);
        var replaced = await SaveAsync(null, patientId, 16m, r =>
        {
            Approve(r, now);
            r.MarkPaid(PaymentMode.Cheque, "CHQ445566", null, null, null, now).IsSuccess.ShouldBeTrue();
            r.PayoutReversedAt = now;
        }, bill.FacilityId);
        var kept = await SaveAsync(bill, patientId, 17m, _ => { }, keptAsAdvance: true);
        var approved = await SaveAsync(null, patientId, 19m, r => Approve(r, now), bill.FacilityId);

        // Someone else's refund, which this patient must never see.
        var (otherPatient, _) = await RegisterAsync(frontOffice);
        var others = await SaveAsync(null, otherPatient, 18m, _ => { }, bill.FacilityId);

        var portal = await server.SignInToPortalAsync(phone);
        var refunds = await portal.GetAsync<List<PortalRefund>>("/api/v1/portal/refunds");

        refunds.Select(r => r.Id).ShouldNotContain(others);
        refunds.Count.ShouldBe(12);
        Stage(refunds, transferred).ShouldBe((PortalRefundStage.PaidOut, PaymentMode.NetBanking, "NEFT0012345678"));
        Stage(refunds, asked).ShouldBe((PortalRefundStage.Requested, null, null));
        Stage(refunds, approved).ShouldBe((PortalRefundStage.Approved, null, null));
        Stage(refunds, credited).ShouldBe((PortalRefundStage.Credited, PaymentMode.PaymentGateway, Arn));
        Stage(refunds, withBank).ShouldBe((PortalRefundStage.WithTheBank, PaymentMode.PaymentGateway, null));
        Stage(refunds, returned).ShouldBe((PortalRefundStage.BankReturned, PaymentMode.PaymentGateway, null));
        Stage(refunds, sendingAgain).ShouldBe((PortalRefundStage.WithTheBank, PaymentMode.PaymentGateway, null), "a refund being sent again is with the bank again");
        Stage(refunds, stoppedPartWay).ShouldBe(
            (PortalRefundStage.BankReturned, PaymentMode.PaymentGateway, null), "a retry that stopped part way left the money with the hospital, as the counter sees it");
        Stage(refunds, notApproved).ShouldBe((PortalRefundStage.NotApproved, null, null));
        Stage(refunds, cash).ShouldBe((PortalRefundStage.PaidOut, PaymentMode.Cash, null), "cash in hand has no reference to trace, and the counter's voucher is its own");
        Stage(refunds, replaced).ShouldBe((PortalRefundStage.Replaced, PaymentMode.Cheque, null), "a payout that never reached the patient is not one to trace");
        Stage(refunds, kept).ShouldBe((PortalRefundStage.KeptAsAdvance, PaymentMode.Advance, null));

        var creditedRefund = refunds.Single(r => r.Id == credited);
        (creditedRefund.BillId, creditedRefund.BillNumber, creditedRefund.Amount).ShouldBe((bill.Id, bill.BillNumber, 10m));
        creditedRefund.SentAt.ShouldNotBeNull().ShouldBe(now.AddDays(-3), TimeSpan.FromSeconds(1));
        creditedRefund.CreditedAt.ShouldNotBeNull().ShouldBe(now, TimeSpan.FromSeconds(1));
        refunds.Single(r => r.Id == asked).SentAt.ShouldBeNull();
        refunds.Single(r => r.Id == transferred).Purpose.ShouldBe(RefundPurpose.Advance);
        refunds.Select(r => r.RequestedAt).ShouldBeInOrder(SortDirection.Descending);

        // What the hospital keeps to itself never reaches the patient.
        var raw = await portal.Http.GetStringAsync("/api/v1/portal/refunds", Ct);
        raw.ShouldNotContain(StaffNote);
        raw.ShouldNotContain(GatewayError);
        raw.ShouldNotContain("Cashier");
        raw.ShouldNotContain("rfnd_", Case.Sensitive, "the gateway's own refund ids are the hospital's");

        // On the bill: its own refunds only.
        var detail = await portal.GetAsync<PortalBillDetail>($"/api/v1/portal/bills/{bill.Id}");
        detail.Refunds.Select(r => r.Id).ShouldBe([credited, withBank, returned, sendingAgain, stoppedPartWay, notApproved, kept], ignoreOrder: true);

        // One of the family's patients, and nobody else's.
        (await portal.GetAsync<List<PortalRefund>>($"/api/v1/portal/refunds?patientId={patientId}")).Count.ShouldBe(12);
        using var denied = await portal.SendRawAsync(HttpMethod.Get, $"/api/v1/portal/refunds?patientId={otherPatient}");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static (PortalRefundStage, PaymentMode?, string?) Stage(List<PortalRefund> refunds, Guid id) =>
        refunds.Single(r => r.Id == id) is var r ? (r.Stage, r.Mode, r.Reference) : default;

    private static void Approve(Refund refund, DateTimeOffset now) => refund.Approve(Guid.CreateVersion7(), "Checked against the bill", now).IsSuccess.ShouldBeTrue();

    private static void ThroughGateway(Refund refund, string gatewayRefundId, DateTimeOffset sent)
    {
        // Recorded as paid out a moment before the gateway took it: the patient is shown when the gateway took it.
        refund.MarkPaid(PaymentMode.PaymentGateway, gatewayRefundId, null, null, null, sent.AddMinutes(-5)).IsSuccess.ShouldBeTrue();
        refund.StartGatewayRefund(gatewayRefundId, new GatewayPaymentRef("simulator", "pay_portal" + gatewayRefundId, "order_portal" + gatewayRefundId), sent);
    }

    /// <summary>A refund of the patient recorded as the counter, the approver and the gateway leave one.</summary>
    private async Task<Guid> SaveAsync(BillDetail? bill, Guid patientId, decimal amount, Action<Refund> shape, Guid? facilityId = null, bool keptAsAdvance = false)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var facility = bill?.FacilityId ?? facilityId!.Value;
        var number = "ITPR" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var refund = keptAsAdvance
            ? Refund.ReturnToAdvance(facility, number, patientId, bill!.Id, null, amount, "Bill cancelled", Guid.CreateVersion7(), DateTimeOffset.UtcNow)
            : Refund.Request(facility, number, RefundPurpose.Bill, patientId, bill?.Id, null, amount, "Overpaid", Guid.CreateVersion7(), "Cashier", DateTimeOffset.UtcNow);
        shape(refund);
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Add(refund);
        await db.SaveChangesAsync(Ct);
        return refund.Id;
    }

    /// <summary>A patient advance of an online appointment prepayment, recorded as the gateway's confirmation records one.</summary>
    private async Task<PatientAdvance> PrepaidAdvanceAsync(Guid facilityId, Guid patientId, decimal amount)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var advances = scope.ServiceProvider.GetRequiredService<AdvanceService>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var advance = await advances.RecordOnlinePaymentAsync(
            facilityId, patientId, amount, "pay_portalrefund" + Digits(10), "order_portalrefund" + Digits(10), "razorpay", "Online prepayment for an appointment (test)",
            DateTimeOffset.UtcNow, null, null, Ct);
        await db.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
        return advance;
    }

    /// <summary>The patient's registration bill (₹100, REG-NEW), finalized.</summary>
    private static async Task<BillDetail> RegistrationBillAsync(ApiClient billing, Guid patientId)
    {
        var service = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=REG-NEW")).Single(s => s.Code == "REG-NEW");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(patientId, null, null, service.Id, 1m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, patientId, null));
        return await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
    }

    private static async Task<(Guid PatientId, string Phone)> RegisterAsync(ApiClient frontOffice)
    {
        var phone = "9" + Digits(9);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Refund", MiddleName: null, LastName: "Portal" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1965, 1, 1).AddDays(Random.Shared.Next(7300)), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return (patient.Id, phone);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record CreatedId(Guid Id);
}
