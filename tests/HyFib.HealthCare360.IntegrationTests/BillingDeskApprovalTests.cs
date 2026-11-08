using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The billing desk's money decisions wait for a second person (maker-checker, slice 6), over the real API and PostgreSQL:
/// an approver's own discount above their own limit waits for another approver, who is recorded as its approver; raising
/// anyone's own-authority limit waits for a second approver, never the one who asked and never the user whose limit it
/// is, while lowering it is made at once; cancelling an OT, procedure, implant or bed-day charge, or one above the
/// hospital's amount, waits for someone who approves discounts, while a module's own cancellation (an order withdrawn)
/// is made at once; a cash shortage at a shift's close waits for a supervisor who is not the cashier, while an excess
/// does not; and a doctor's consultation fee changes only as a service's price does, the old fee charged until then.
/// </summary>
public sealed class BillingDeskApprovalTests(HospitalServer server)
{
    private const string Approvals = "/api/v1/approvals";
    private const string Bills = "/api/v1/billing/bills";
    private const string Limits = "/api/v1/billing/discount-limits";
    private const string Charges = "/api/v1/billing/charges";
    private const string Shifts = "/api/v1/billing/shifts";
    private const string Practitioners = "/api/v1/masters/practitioners";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ discounts

    [Fact]
    public async Task An_approvers_own_discount_above_their_limit_waits_and_another_approver_is_recorded_as_its_approver()
    {
        // The hospital administrator approves discounts and bills at the counter, and has no limit of their own.
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var accountant = await server.SignInAsync(Users.Accountant);
        var draft = await DraftBillAsync(admin, 1_000m);
        (await admin.GetAsync<DiscountAuthority>($"{Limits}/me")).MaxPercent.ShouldBeLessThan(50m);

        // Half off waits: holding the approval permission gives no more than their own limit.
        var outcome = await admin.PostAsync<DiscountOutcome>($"{Bills}/{draft.Id}/discount", new ApplyDiscountRequest(50m, null, null, "Staff relative", draft.Version));
        outcome.Applied.ShouldBeFalse();
        var approvalId = outcome.ApprovalId.ShouldNotBeNull();
        var waiting = await admin.GetAsync<BillDetail>($"{Bills}/{draft.Id}");
        (waiting.BillDiscountPercent, waiting.ApprovedDiscountAmount, waiting.PendingDiscount?.Id).ShouldBe((null, 0m, approvalId));

        // They may not approve it themselves, and the bill is not finalized while it waits.
        (await RefusalAsync(admin, $"/api/v1/billing/discount-approvals/{approvalId}/approve", new DecideDiscountRequest(null)))
            .ShouldBe((HttpStatusCode.Forbidden, "billing.discount_self_approval"));
        (await RefusalAsync(admin, $"{Bills}/{draft.Id}/finalize", new BillVersionRequest(waiting.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "billing.discount_pending"));

        // The accountant approves: the discount is on the bill, sanctioned in the accountant's name.
        using (var approved = await accountant.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/discount-approvals/{approvalId}/approve", new DecideDiscountRequest("Agreed")))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.NoContent, await approved.Content.ReadAsStringAsync(Ct));
        }

        var discounted = await admin.GetAsync<BillDetail>($"{Bills}/{draft.Id}");
        (discounted.BillDiscountPercent, discounted.ApprovedDiscountAmount, discounted.PendingDiscount).ShouldBe((50m, 500m, null));
        (await DiscountApproverAsync(draft.Id)).ShouldBe(await UserIdAsync(accountant));
    }

    [Fact]
    public async Task An_approvers_discount_within_their_own_limit_is_made_at_once_with_nobody_recorded_as_approving_it()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var accountant = await server.SignInAsync(Users.Accountant);
        var superAdmin = await server.SignInAsync(Users.Admin);
        var adminId = await UserIdAsync(admin);

        // The accountant gives the hospital administrator a 10% limit; raised, it waits, and the super administrator (not
        // the hospital administrator, whose limit it is) approves it.
        await server.SaveDiscountLimitAsync(accountant, new SaveDiscountLimitRequest(adminId, 10m, null, "Counter cover"));
        try
        {
            var draft = await DraftBillAsync(admin, 1_000m);
            var outcome = await admin.PostAsync<DiscountOutcome>($"{Bills}/{draft.Id}/discount", new ApplyDiscountRequest(10m, null, null, "Senior citizen", draft.Version));
            (outcome.Applied, outcome.ApprovalId).ShouldBe((true, null));
            var bill = await admin.GetAsync<BillDetail>($"{Bills}/{draft.Id}");
            (bill.BillDiscountPercent, bill.ApprovedDiscountAmount).ShouldBe((10m, 0m));
            (await DiscountApproverAsync(draft.Id)).ShouldBeNull("a discount given within one's own limit has no approver, least of all its maker");
        }
        finally
        {
            // Removing the limit lowers it to the default: made at once.
            var limit = (await superAdmin.GetAsync<List<DiscountLimitDto>>(Limits)).Single(l => l.UserId == adminId);
            using var removed = await superAdmin.SendRawAsync(HttpMethod.Delete, $"{Limits}/{limit.Id}", null);
            removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await removed.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task A_discount_limit_raised_waits_for_a_second_approver_never_the_user_whose_limit_it_is_and_lowering_is_at_once()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var accountant = await server.SignInAsync(Users.Accountant);
        var superAdmin = await server.SignInAsync(Users.Admin);
        var accountantId = await UserIdAsync(accountant);
        var limitsBefore = await admin.GetAsync<List<DiscountLimitDto>>(Limits);
        limitsBefore.ShouldNotContain(l => l.UserId == accountantId);

        // Nobody asks for a higher limit of their own.
        (await RefusalAsync(accountant, HttpMethod.Put, Limits, new SaveDiscountLimitRequest(accountantId, 20m, null, "Mine")))
            .ShouldBe((HttpStatusCode.Forbidden, DiscountService.OwnLimitCode));

        // The hospital administrator asks to give the accountant 20%: it waits, in words, and nothing changes meanwhile.
        using var response = await admin.SendRawAsync(HttpMethod.Put, Limits, new SaveDiscountLimitRequest(accountantId, 20m, null, "Covers the cash counter"));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Put, Limits);
        asked.Kind.ShouldBe(DiscountService.LimitKind);
        asked.Details.ShouldContain(d => d.Label == "Own-authority discount limit" && d.Value.EndsWith("→ 20%", StringComparison.Ordinal));
        (await admin.GetAsync<List<DiscountLimitDto>>(Limits)).ShouldNotContain(l => l.UserId == accountantId);
        (await admin.GetAsync<List<PendingDiscountLimitDto>>($"{Limits}/pending")).ShouldContain(p => p.ApprovalId == asked.Id && p.UserId == accountantId && p.AskedByYou);

        // Not by the one who asked, nor by the accountant whose limit it is (the request goes on waiting); the super
        // administrator approves.
        (await RefusalAsync(admin, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        (await RefusalAsync(accountant, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, DiscountService.OwnLimitCode));
        var current = await superAdmin.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}");
        current.Status.ShouldBe(ApprovalStatus.Pending);
        (await superAdmin.PostAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, current.Version))).Status.ShouldBe(ApprovalStatus.Approved);
        var limit = (await admin.GetAsync<List<DiscountLimitDto>>(Limits)).Single(l => l.UserId == accountantId);
        (limit.MaxPercent, limit.MaxAmount, limit.Notes).ShouldBe((20m, null, "Covers the cash counter"));
        var audited = await AuditAsync("billing.discount_limit.raised", accountantId);
        audited.UserId.ShouldBe(await UserIdAsync(superAdmin));
        audited.Summary.ShouldNotBeNull().ShouldContain("asked for by");

        try
        {
            // Lowering is at once: to 5%, and a rupee cap put on.
            await SaveAtOnceAsync(admin, new SaveDiscountLimitRequest(accountantId, 5m, null, "Lowered", limit.Version));
            limit = (await admin.GetAsync<List<DiscountLimitDto>>(Limits)).Single(l => l.UserId == accountantId);
            await SaveAtOnceAsync(admin, new SaveDiscountLimitRequest(accountantId, 5m, 500m, "Capped", limit.Version));
            limit = (await admin.GetAsync<List<DiscountLimitDto>>(Limits)).Single(l => l.UserId == accountantId);
            (limit.MaxPercent, limit.MaxAmount).ShouldBe((5m, 500m));

            // Taking the cap away lets them give more: it waits. Lowered meanwhile, the request no longer fits and fails.
            using var uncapped = await admin.SendRawAsync(HttpMethod.Put, Limits, new SaveDiscountLimitRequest(accountantId, 5m, null, "No cap", limit.Version));
            uncapped.StatusCode.ShouldBe(HttpStatusCode.Accepted, await uncapped.Content.ReadAsStringAsync(Ct));
            var stale = await ApiClient.ReadAsync<ApprovalRequestDto>(uncapped, HttpMethod.Put, Limits);
            await SaveAtOnceAsync(admin, new SaveDiscountLimitRequest(accountantId, 2m, 500m, "Lowered again", limit.Version));
            (await RefusalAsync(superAdmin, $"{Approvals}/{stale.Id}/approve", new ApproveRequest(null, stale.Version)))
                .ShouldBe((HttpStatusCode.UnprocessableEntity, "approval.stale"));
            limit = (await admin.GetAsync<List<DiscountLimitDto>>(Limits)).Single(l => l.UserId == accountantId);
            (limit.MaxPercent, limit.MaxAmount).ShouldBe((2m, 500m));
        }
        finally
        {
            limit = (await admin.GetAsync<List<DiscountLimitDto>>(Limits)).Single(l => l.UserId == accountantId);
            using var removed = await admin.SendRawAsync(HttpMethod.Delete, $"{Limits}/{limit.Id}", null);
            removed.IsSuccessStatusCode.ShouldBeTrue(await removed.Content.ReadAsStringAsync(Ct));
        }
    }

    // ------------------------------------------------------------------ charges

    [Fact]
    public async Task A_procedure_charge_cancelled_at_the_desk_waits_for_someone_who_approves_discounts_and_a_small_one_is_at_once()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var billing = await server.SignInAsync(Users.Billing);
        var accountant = await server.SignInAsync(Users.Accountant);
        var patientId = await PatientAsync();
        var procedure = await ServiceAsync(admin, "Suturing of a laceration", ServiceCategory.Procedure, 400m);
        var kit = await ServiceAsync(admin, "Dressing kit", ServiceCategory.Consumable, 300m);
        var oxygen = await ServiceAsync(admin, "Oxygen, per hour", ServiceCategory.Miscellaneous, 1_500m);
        var sutured = await ChargeAsync(billing, patientId, procedure);
        var dressed = await ChargeAsync(billing, patientId, kit);
        var given = await ChargeAsync(billing, patientId, oxygen);

        // A small kit is cancelled at once.
        (await ChargeAsync(billing, dressed.Id)).CancelNeedsApproval.ShouldBeFalse();
        using (var cancelled = await billing.SendRawAsync(HttpMethod.Post, $"{Charges}/{dressed.Id}/cancel", new CancelChargeRequest("Posted twice")))
        {
            cancelled.StatusCode.ShouldBe(HttpStatusCode.NoContent, await cancelled.Content.ReadAsStringAsync(Ct));
        }

        (await ChargeAsync(billing, dressed.Id)).Status.ShouldBe(ChargeStatus.Cancelled);

        // A procedure always waits, whatever it costs; the desk cannot approve it, and it stays charged meanwhile.
        (await ChargeAsync(billing, sutured.Id)).CancelNeedsApproval.ShouldBeTrue();
        var asked = await AskCancelAsync(billing, sutured.Id, "Not done, the doctor glued it");
        asked.Kind.ShouldBe(ChargeService.CancelKind);
        asked.Details.ShouldContain(d => d.Label == "Amount" && d.Value.StartsWith("₹400.00 → nothing", StringComparison.Ordinal));
        var waiting = await ChargeAsync(billing, sutured.Id);
        (waiting.Status, waiting.CancellationAskedAt is not null).ShouldBe((ChargeStatus.Unbilled, true));
        (await RefusalAsync(billing, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));

        // Someone who approves discounts and asks may not approve their own; the accountant approves.
        (await ChargeAsync(billing, given.Id)).CancelNeedsApproval.ShouldBeTrue("₹1,500 is above the ₹1,000 the desk cancels alone");
        var own = await AskCancelAsync(admin, given.Id, "Oxygen not given");
        (await RefusalAsync(admin, $"{Approvals}/{own.Id}/approve", new ApproveRequest(null, own.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        await server.ApproveRefundAsync(asked.Id);
        await server.ApproveRefundAsync(own.Id);
        (await ChargeAsync(billing, sutured.Id)).ShouldSatisfyAllConditions(
            c => c.Status.ShouldBe(ChargeStatus.Cancelled),
            c => c.CancelReason.ShouldBe("Not done, the doctor glued it"),
            c => c.CancellationAskedAt.ShouldBeNull());
        (await ChargeAsync(billing, given.Id)).Status.ShouldBe(ChargeStatus.Cancelled);
        var audited = await AuditAsync("billing.charge.cancelled", sutured.Id);
        audited.UserId.ShouldBe(await UserIdAsync(accountant));
        audited.Summary.ShouldNotBeNull().ShouldContain($"asked for by {asked.RequestedByName}");
    }

    [Fact]
    public async Task A_charge_billed_while_its_cancellation_waits_is_not_cancelled_and_the_hospital_may_make_every_cancellation_wait()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var billing = await server.SignInAsync(Users.Billing);
        var patientId = await PatientAsync();
        var procedure = await ServiceAsync(admin, "Plaster cast, forearm", ServiceCategory.Procedure, 900m);
        var cast = await ChargeAsync(billing, patientId, procedure);
        var asked = await AskCancelAsync(billing, cast.Id, "Wrong patient");

        // The bill is finalized with the cast on it before anyone decides: approving it then cancels nothing.
        var draft = await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(null, patientId, null));
        draft.Lines.ShouldContain(l => l.ChargeId == cast.Id);
        await billing.PostAsync<BillDetail>($"{Bills}/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        var accountant = await server.SignInAsync(Users.Accountant);
        var current = await accountant.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}");
        (await RefusalAsync(accountant, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, current.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "approval.stale"));
        (await ChargeAsync(billing, cast.Id)).Status.ShouldBe(ChargeStatus.Billed);

        // Set to ₹0 (stricter, so at once), even a small kit's cancellation waits.
        var superAdmin = await server.SignInAsync(Users.Admin);
        await server.SaveSettingAsync(superAdmin, ChargeService.CancelApprovalAboveSetting, new SaveSettingRequest(JsonValue.Create(0), null, false, null));
        try
        {
            var kit = await ServiceAsync(admin, "Sling", ServiceCategory.Consumable, 150m);
            var sling = await ChargeAsync(billing, patientId, kit);
            (await ChargeAsync(billing, sling.Id)).CancelNeedsApproval.ShouldBeTrue();
            (await AskCancelAsync(billing, sling.Id, "Not needed")).Kind.ShouldBe(ChargeService.CancelKind);
        }
        finally
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(ChargeService.CancelApprovalAboveSetting, null, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_procedure_order_withdrawn_by_the_doctor_cancels_its_charge_at_once()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var billing = await server.SignInAsync(Users.Billing);
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        var dressing = (await doctor.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=PROC-DRESS")).Single(s => s.Code == "PROC-DRESS");
        var order = await doctor.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(
            patientId, null, visit.Id, null, OrderPriority.Routine, "Dressing of the forearm", [new OrderLineRequest(dressing.Id, null)], null, false));
        var charge = (await billing.GetAsync<PagedResult<ChargeDto>>($"{Charges}?patientId={patientId}&pageSize=50")).Items.Single(c => c.ServiceItemId == dressing.Id);
        (charge.Category, charge.CancelNeedsApproval).ShouldBe((ServiceCategory.Procedure, true), "the desk would have to ask to cancel it");

        // The doctor withdraws the order: the module cancels its charge itself, at once, and nobody is asked.
        using (var withdrawn = await doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/clinical/orders/{order.Id}/cancel", new CancelOrderRequest("Dressed in the ambulance")))
        {
            withdrawn.IsSuccessStatusCode.ShouldBeTrue(await withdrawn.Content.ReadAsStringAsync(Ct));
        }

        var cancelled = await ChargeAsync(billing, charge.Id);
        (cancelled.Status, cancelled.CancellationAskedAt).ShouldBe((ChargeStatus.Cancelled, null));
        (await RequestsAboutAsync(ChargeService.CancelKind, charge.Id)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ cash shortage

    [Fact]
    public async Task A_cash_shortage_at_close_waits_for_a_supervisor_who_is_not_the_cashier_and_an_excess_does_not()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var accountant = await server.SignInAsync(Users.Accountant);
        var superAdmin = await server.SignInAsync(Users.Admin);

        // The hospital administrator works a counter with ₹1,000 float and takes nothing: ₹900 is counted at the close.
        var shift = await OpenShiftAsync(admin, 1_000m);
        using (var noReason = await admin.SendRawAsync(HttpMethod.Post, $"{Shifts}/{shift.Id}/close", new CloseShiftRequest(900m, null, shift.Version)))
        {
            noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a shortage is closed only with the reason a supervisor reads");
        }

        // A supervisor closes it, so the accountant is the one who asks.
        using var response = await accountant.SendRawAsync(HttpMethod.Post, $"{Shifts}/{shift.Id}/close", new CloseShiftRequest(900m, "₹100 note missing from the drawer", shift.Version));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var closed = await ApiClient.ReadAsync<ShiftDto>(response, HttpMethod.Post, $"{Shifts}/{shift.Id}/close");
        (closed.Status, closed.Variance, closed.ShortageAwaiting).ShouldBe((ShiftStatus.Closed, -100m, true));
        var approvalId = closed.ShortageApprovalId.ShouldNotBeNull();
        var asked = await superAdmin.GetAsync<ApprovalRequestDto>($"{Approvals}/{approvalId}");
        (asked.Kind, asked.Reason).ShouldBe((ShiftService.ShortageKind, "₹100 note missing from the drawer"));
        asked.Details.ShouldContain(new ApprovalDetail("Cash declared", "₹900.00"));

        // The day's report shows the shift closed with its shortage awaiting a supervisor.
        var dayEnd = await admin.GetAsync<DayEndReport>($"/api/v1/billing/reports/day-end?date={server.Today:yyyy-MM-dd}");
        dayEnd.Shifts.Single(s => s.ShiftId == shift.Id).ShortageAwaiting.ShouldBeTrue();
        dayEnd.ShortagesAwaiting.ShouldBeGreaterThanOrEqualTo(1);

        // The cashier, a supervisor too, may not accept their own shift's shortage; the one who asked may not either.
        (await RefusalAsync(admin, $"{Approvals}/{approvalId}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, ShiftCalculator.OwnShortageCode));
        (await RefusalAsync(accountant, $"{Approvals}/{approvalId}/approve", new ApproveRequest(null, asked.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        asked = await superAdmin.GetAsync<ApprovalRequestDto>($"{Approvals}/{approvalId}");
        (await superAdmin.PostAsync<ApprovalRequestDto>($"{Approvals}/{approvalId}/approve", new ApproveRequest(null, asked.Version))).Status.ShouldBe(ApprovalStatus.Approved);
        var accepted = await admin.GetAsync<ShiftDto>($"{Shifts}/{shift.Id}");
        (accepted.ShortageAwaiting, accepted.ShortageApprovalId, accepted.ShortageAcceptedBy is not null).ShouldBe((false, null, true));
        (await AuditAsync("billing.shift.shortage_accepted", shift.Id)).UserId.ShouldBe(await UserIdAsync(superAdmin));

        // An excess is recorded and flagged, and needs no one.
        var next = await OpenShiftAsync(admin, 500m);
        var over = await admin.PostAsync<ShiftDto>($"{Shifts}/{next.Id}/close", new CloseShiftRequest(520m, null, next.Version));
        (over.Status, over.Variance, over.ShortageAwaiting, over.ShortageApprovalId).ShouldBe((ShiftStatus.Closed, 20m, false, null));
    }

    [Fact]
    public async Task A_shortage_within_the_hospitals_tolerance_closes_at_once_and_a_rejected_one_is_asked_again()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var superAdmin = await server.SignInAsync(Users.Admin);

        // Raising the tolerance loosens it: a second administrator approves (the helper).
        await server.SaveSettingAsync(superAdmin, ShiftService.ShortageToleranceSetting, new SaveSettingRequest(JsonValue.Create(50), null, false, null));
        try
        {
            var shift = await OpenShiftAsync(admin, 1_000m);
            var closed = await admin.PostAsync<ShiftDto>($"{Shifts}/{shift.Id}/close", new CloseShiftRequest(970m, null, shift.Version));
            (closed.Variance, closed.ShortageAwaiting).ShouldBe((-30m, false));

            // ₹60 short is beyond it: it waits. Rejected, the shortage still awaits, and is asked for again.
            var short60 = await OpenShiftAsync(admin, 1_000m);
            using var response = await admin.SendRawAsync(HttpMethod.Post, $"{Shifts}/{short60.Id}/close", new CloseShiftRequest(940m, "Change given wrongly", short60.Version));
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
            var first = (await ApiClient.ReadAsync<ShiftDto>(response, HttpMethod.Post, "close")).ShortageApprovalId.ShouldNotBeNull();
            var request = await superAdmin.GetAsync<ApprovalRequestDto>($"{Approvals}/{first}");
            await superAdmin.PostAsync<ApprovalRequestDto>($"{Approvals}/{first}/reject", new RejectApprovalRequest("Recount with the cashier", request.Version));
            var still = await admin.GetAsync<ShiftDto>($"{Shifts}/{short60.Id}");
            (still.ShortageAwaiting, still.ShortageApprovalId).ShouldBe((true, null));
            using var again = await admin.SendRawAsync(HttpMethod.Post, $"{Shifts}/{short60.Id}/shortage", new AskShortageRequest("Recounted with the accountant: ₹60 short"));
            again.StatusCode.ShouldBe(HttpStatusCode.Accepted, await again.Content.ReadAsStringAsync(Ct));
            (await admin.GetAsync<ShiftDto>($"{Shifts}/{short60.Id}")).ShortageApprovalId.ShouldNotBeNull();
        }
        finally
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(ShiftService.ShortageToleranceSetting, null, CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------ doctor's consultation fee

    [Fact]
    public async Task A_doctors_consultation_fee_waits_for_someone_who_approves_prices_and_the_old_fee_is_charged_until_then()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var superAdmin = await server.SignInAsync(Users.Admin);
        var surgery = (await admin.GetAsync<List<LookupItem>>("/api/v1/masters/lookups/departments")).Single(d => d.Code == "GS");

        // A new doctor with a fee of ₹777: the record is made, the fee waits, and none of their own applies meanwhile.
        var created = await admin.PostAsync<JsonElement>(Practitioners, Doctor($"FE{Letters(8)}", surgery.Id, 777m, 400m, null));
        var doctorId = created.GetProperty("id").GetGuid();
        var feeApproval = created.GetProperty("feeApprovalId").GetGuid();
        var doctor = await admin.GetAsync<PractitionerDto>($"{Practitioners}/{doctorId}");
        (doctor.ConsultationFeeNew, doctor.ConsultationFeeFollowUp).ShouldBe((0m, 0m));
        doctor.PendingFee.ShouldNotBeNull().Asked.ShouldBe(new PractitionerFees(777m, 400m));
        var asked = await superAdmin.GetAsync<ApprovalRequestDto>($"{Approvals}/{feeApproval}");
        asked.Kind.ShouldBe(PractitionerFeeApprovals.Kind);
        asked.Details.ShouldContain(new ApprovalDetail("Consultation fee, new visit", "none (the service price) → ₹777.00"));

        // A visit meanwhile is charged the consultation service's approved price, not the fee asked for.
        var before = await ConsultationAsync(surgery.Id, doctorId);
        before.ShouldNotBe(777m);

        // The one who asked may not approve; someone else who approves prices does, and the fee is charged from then on.
        (await RefusalAsync(admin, $"{Approvals}/{feeApproval}/approve", new ApproveRequest(null, asked.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        (await superAdmin.PostAsync<ApprovalRequestDto>($"{Approvals}/{feeApproval}/approve", new ApproveRequest(null, asked.Version))).Status.ShouldBe(ApprovalStatus.Approved);
        (await ConsultationAsync(surgery.Id, doctorId)).ShouldBe(777m);
        (await AuditAsync("practitioner.fee_changed", doctorId)).UserId.ShouldBe(await UserIdAsync(superAdmin));

        // Lowered to ₹300: it waits too (a lower fee undercharges), ₹777 is charged meanwhile, and saving the rest of
        // the record with the fee in force is made at once; another fee while one waits is refused.
        doctor = await admin.GetAsync<PractitionerDto>($"{Practitioners}/{doctorId}");
        using (var lowered = await admin.SendRawAsync(HttpMethod.Put, $"{Practitioners}/{doctorId}", Doctor(doctor.Code, surgery.Id, 300m, 400m, doctor.Version)))
        {
            lowered.StatusCode.ShouldBe(HttpStatusCode.Accepted, await lowered.Content.ReadAsStringAsync(Ct));
            (await ApiClient.ReadAsync<ApprovalRequestDto>(lowered, HttpMethod.Put, Practitioners)).Summary.ShouldContain("new visit ₹777.00 → ₹300.00");
        }

        (await ConsultationAsync(surgery.Id, doctorId)).ShouldBe(777m);
        doctor = await admin.GetAsync<PractitionerDto>($"{Practitioners}/{doctorId}");
        using (var phone = await admin.SendRawAsync(HttpMethod.Put, $"{Practitioners}/{doctorId}", Doctor(doctor.Code, surgery.Id, 777m, 400m, doctor.Version) with { Phone = "9" + Digits(9) }))
        {
            phone.StatusCode.ShouldBe(HttpStatusCode.NoContent, await phone.Content.ReadAsStringAsync(Ct));
        }

        doctor = await admin.GetAsync<PractitionerDto>($"{Practitioners}/{doctorId}");
        (doctor.ConsultationFeeNew, doctor.PendingFee?.Asked.NewVisit).ShouldBe((777m, 300m));
        (await RefusalAsync(admin, HttpMethod.Put, $"{Practitioners}/{doctorId}", Doctor(doctor.Code, surgery.Id, 250m, 400m, doctor.Version)))
            .ShouldBe((HttpStatusCode.Conflict, PractitionerFeeApprovals.WaitingCode));
    }

    // ------------------------------------------------------------------ helpers

    private static SavePractitionerRequest Doctor(string code, Guid departmentId, decimal fee, decimal followUp, uint? version) => new(
        Code: code, Title: "Dr.", FirstName: "Farida", LastName: "Fees" + code[2..], Gender: Gender.Female, Qualification: "MS (General Surgery)",
        RegistrationCouncil: "Tamil Nadu Medical Council", RegistrationNumber: $"TNMC {code[2..]}", HprId: null, SpecialtyId: null, DepartmentId: departmentId,
        Kind: PractitionerKind.Consultant, Phone: null, Email: null, ConsultationFeeNew: fee, ConsultationFeeFollowUp: followUp, FollowUpValidityDays: 0,
        Languages: null, Bio: null, PhotoFileId: null, SignatureFileId: null, AvailableForOnlineBooking: false, AvailableForTelehealth: false,
        FacilityIds: null, IsActive: true, Version: version);

    /// <summary>What a new self-pay patient's visit with the doctor is charged for the consultation.</summary>
    private async Task<decimal> ConsultationAsync(Guid departmentId, Guid doctorId)
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var billing = await server.SignInAsync(Users.Billing);
        var patientId = await PatientAsync();
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.OutPatient, departmentId, doctorId, null, PayerType.Self, null, ReferralSource.Self, null, null, false, null, null,
            TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
        var charges = (await billing.GetAsync<PagedResult<ChargeDto>>($"{Charges}?visitId={visit.Id}&pageSize=50")).Items;
        return charges.Single(c => c.ServiceCode == "CONS-NEW").UnitPrice;
    }

    /// <summary>A draft bill of a new patient for one service the test adds at <paramref name="price"/>, discounts allowed.</summary>
    private async Task<BillDetail> DraftBillAsync(ApiClient biller, decimal price)
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var service = await ServiceAsync(admin, "Counselling session", ServiceCategory.Miscellaneous, price);
        var patientId = await PatientAsync();
        var draft = await biller.PostAsync<BillDetail>(Bills, new CreateBillRequest(null, patientId, null));
        return await biller.PostAsync<BillDetail>($"{Bills}/{draft.Id}/lines", new AddBillLineRequest(service.Id, 1m, null, null, null, null, draft.Version));
    }

    private async Task<ServiceItemDto> ServiceAsync(ApiClient admin, string name, ServiceCategory category, decimal price) =>
        await server.CreateServiceAsync(admin, new SaveServiceItemRequest($"SV-{Digits(8)}", name, null, category, null, price, 0m, null, true, false, null, true, null));

    private static Task<ChargeDto> ChargeAsync(ApiClient billing, Guid patientId, ServiceItemDto service) =>
        billing.PostAsync<ChargeDto>(Charges, new PostChargeRequest(patientId, null, null, service.Id, 1m, null, null, null, null, null));

    private static Task<ChargeDto> ChargeAsync(ApiClient billing, Guid chargeId) => billing.GetAsync<ChargeDto>($"{Charges}/{chargeId}");

    private static async Task<ApprovalRequestDto> AskCancelAsync(ApiClient maker, Guid chargeId, string reason)
    {
        var path = $"{Charges}/{chargeId}/cancel";
        using var response = await maker.SendRawAsync(HttpMethod.Post, path, new CancelChargeRequest(reason));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, path);
    }

    /// <summary>A shift opened by the user with the float, after closing (balanced) any shift they left open.</summary>
    private static async Task<ShiftDto> OpenShiftAsync(ApiClient cashier, decimal openingFloat)
    {
        using (var current = await cashier.SendRawAsync(HttpMethod.Get, $"{Shifts}/current", null))
        {
            if (current.StatusCode == HttpStatusCode.OK)
            {
                var open = await ApiClient.ReadAsync<ShiftDto>(current, HttpMethod.Get, $"{Shifts}/current");
                await cashier.PostAsync<ShiftDto>($"{Shifts}/{open.Id}/close", new CloseShiftRequest(open.ExpectedCash, null, open.Version));
            }
        }

        return await cashier.PostAsync<ShiftDto>($"{Shifts}/open", new OpenShiftRequest("Slice 6 counter", openingFloat));
    }

    private static async Task SaveAtOnceAsync(ApiClient admin, SaveDiscountLimitRequest request)
    {
        using var response = await admin.SendRawAsync(HttpMethod.Put, Limits, request);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
    }

    private static Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body) => RefusalAsync(api, HttpMethod.Post, path, body);

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, HttpMethod method, string path, object body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return (response.StatusCode, problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private static async Task<Guid> UserIdAsync(ApiClient user) => (await user.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid();

    private async Task<Guid?> DiscountApproverAsync(Guid billId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Bill>().IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.Id == billId).Select(b => b.DiscountApprovedBy).SingleAsync(Ct);
    }

    private async Task<int> RequestsAboutAsync(string kind, Guid subjectId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ApprovalRequest>().AsNoTracking()
            .CountAsync(r => r.Kind == kind && r.SubjectId == subjectId, Ct);
    }

    private async Task<AuditEvent> AuditAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .SingleAsync(a => a.Action == action && a.EntityId == entityId.ToString(), Ct);
    }

    private async Task<Guid> PatientAsync()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        return (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Ravi"), MiddleName: null, LastName: "Desk" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false))).Id;
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));
}
