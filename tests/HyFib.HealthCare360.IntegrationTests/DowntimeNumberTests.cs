using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Downtime;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Downtime visit, bill and receipt numbers over the real API (NABH IMS continuity), and back-entry past the window: the
/// medical records officer issues a block to a counter; the counter's paper is entered afterwards under the number on it,
/// with the time on it, in order; a number is entered once, a voided or returned one never, a number of another kind or a
/// number HC360 issued normally not at all; and paper older than its window waits for a second person's approval.
/// </summary>
public sealed class DowntimeNumberTests(HospitalServer server)
{
    private const string Blocks = "/api/v1/downtime/number-blocks";
    private const string Late = "/api/v1/downtime/back-entry/late";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Visit_numbers_are_issued_to_a_counter_entered_in_order_once_and_the_rest_returned()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await RegisterAsync(frontOffice);

        // The desk enters the paper but does not issue blocks.
        using (var refused = await frontOffice.SendRawAsync(HttpMethod.Post, Blocks, new IssueDowntimeNumbersRequest(DowntimeNumberKind.Visit, "OPD registration", 3)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var block = await mrd.PostAsync<DowntimeNumberBlockDetail>(Blocks, new IssueDowntimeNumbersRequest(DowntimeNumberKind.Visit, "OPD registration", 3));
        block.Numbers.Select(n => n.Position).ShouldBe([1, 2, 3]);
        (block.Block.Counter, block.Block.KindName, block.Block.Open).ShouldBe(("OPD registration", "visit", 3));

        // A visit checked in now does not take a block's number.
        var live = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", Visit(patient.Id, null));
        block.Numbers.ShouldNotContain(n => n.Number == live.VisitNumber);
        await frontOffice.PostAsync($"/api/v1/visits/{live.Id}/cancel", new CancelVisitRequest("Checked in for the test only"));

        // The second slip is entered first, keeping its number and time.
        var second = block.Block.IssuedAt.AddSeconds(20);
        var two = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", Visit(patient.Id, new PaperNumber(block.Numbers[1].Number.ToLowerInvariant(), second)));
        two.VisitNumber.ShouldBe(block.Numbers[1].Number);
        two.VisitedAt.ShouldBe(second, TimeSpan.FromSeconds(1));

        // The first slip cannot be dated after the second: the counter used the numbers in order.
        var other = await RegisterAsync(frontOffice);
        await RefusedAsync(frontOffice, Visit(other.Id, new PaperNumber(block.Numbers[0].Number, second.AddSeconds(10))), HttpStatusCode.BadRequest, "Numbers are used in order");
        var one = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", Visit(other.Id, new PaperNumber(block.Numbers[0].Number, second.AddSeconds(-10))));
        one.VisitNumber.ShouldBe(block.Numbers[0].Number);

        // Entered once; a normal visit number is not a downtime number.
        var third = await RegisterAsync(frontOffice);
        await RefusedAsync(frontOffice, Visit(third.Id, new PaperNumber(block.Numbers[0].Number, DateTimeOffset.UtcNow)), HttpStatusCode.Conflict, "already been entered");
        await RefusedAsync(frontOffice, Visit(third.Id, new PaperNumber(live.VisitNumber, DateTimeOffset.UtcNow)), HttpStatusCode.BadRequest, "is not a downtime visit number");

        // The block comes back: the number not used is voided, audited, and can no longer be entered.
        var current = await mrd.GetAsync<DowntimeNumberBlockDetail>($"{Blocks}/{block.Block.Id}");
        var returned = await mrd.PostAsync<DowntimeNumberBlockDetail>($"{Blocks}/{block.Block.Id}/return", new ReturnDowntimeBlockRequest("Counter closed", current.Block.Version));
        (returned.Block.Entered, returned.Block.Voided, returned.Block.Open).ShouldBe((2, 1, 0));
        returned.Block.ReturnedAt.ShouldNotBeNull();
        (returned.Numbers[2].Status, returned.Numbers[2].VoidReason).ShouldBe((DowntimeUhidStatus.Voided, "Returned unused"));
        var detail = await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}");
        (returned.Numbers[1].EntityId, returned.Numbers[1].PatientName).ShouldBe((two.Id, detail.FullName));
        await RefusedAsync(frontOffice, Visit(third.Id, new PaperNumber(block.Numbers[2].Number, DateTimeOffset.UtcNow)), HttpStatusCode.BadRequest, "was voided (Returned unused)");
        using (var again = await mrd.SendRawAsync(HttpMethod.Post, $"{Blocks}/{block.Block.Id}/return", new ReturnDowntimeBlockRequest(null, returned.Block.Version)))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var actions = await db.Set<AuditEvent>().AsNoTracking()
            .Where(a => a.EntityId == block.Block.Id.ToString() || a.EntityId == two.Id.ToString())
            .Select(a => a.Action).ToListAsync(Ct);
        actions.ShouldContain("patients.downtime_numbers_issued");
        actions.ShouldContain("patients.downtime_block_returned");
        actions.ShouldContain("patients.entered_from_paper");
    }

    [Fact]
    public async Task A_paper_bill_and_its_manual_receipt_keep_their_numbers_and_times()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var bills = await mrd.PostAsync<DowntimeNumberBlockDetail>(Blocks, new IssueDowntimeNumbersRequest(DowntimeNumberKind.OpBill, "OPD billing counter 1", 2));
        var receipts = await mrd.PostAsync<DowntimeNumberBlockDetail>(Blocks, new IssueDowntimeNumbersRequest(DowntimeNumberKind.Receipt, "OPD billing counter 1", 2));
        var patient = await RegisterAsync(frontOffice);
        var draft = await DraftAsync(frontOffice, billing, patient.Id);

        // A receipt number is not a bill number.
        await RefusedAsync(billing, $"/api/v1/billing/bills/{draft.Id}/finalize",
            new FinalizeBillRequest(draft.Version, new PaperNumber(receipts.Numbers[0].Number, DateTimeOffset.UtcNow)), HttpStatusCode.BadRequest, "is not a downtime OP bill number");

        var billedAt = bills.Block.IssuedAt.AddSeconds(5);
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize",
            new FinalizeBillRequest(draft.Version, new PaperNumber(bills.Numbers[0].Number, billedAt)));
        bill.BillNumber.ShouldBe(bills.Numbers[0].Number);
        bill.FinalizedAt!.Value.ShouldBe(billedAt, TimeSpan.FromSeconds(1));

        var shift = await OpenShiftAsync(billing);
        try
        {
            var paidAt = receipts.Block.IssuedAt.AddSeconds(10);
            var receipt = await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts",
                new CollectPaymentRequest(bill.Id, [Upi(bill.DueAmount)], null, new PaperNumber(receipts.Numbers[0].Number, paidAt)));
            receipt.ReceiptNumber.ShouldBe(receipts.Numbers[0].Number);
            receipt.ReceivedAt.ShouldBe(paidAt, TimeSpan.FromSeconds(1));
        }
        finally
        {
            await CloseShiftAsync(billing, shift);
        }

        var entered = await mrd.GetAsync<DowntimeNumberBlockDetail>($"{Blocks}/{receipts.Block.Id}");
        (entered.Numbers[0].Status, entered.Numbers[0].EntityType).ShouldBe((DowntimeUhidStatus.Entered, nameof(Receipt)));
    }

    [Fact]
    public async Task Paper_of_another_period_of_the_series_is_refused()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var block = await mrd.PostAsync<DowntimeNumberBlockDetail>(Blocks, new IssueDowntimeNumbersRequest(DowntimeNumberKind.Visit, "Casualty", 1));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            // As if the block had been counted in a year (or month) that has since ended.
            (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<DowntimeNumberBlock>()
                .Where(b => b.Id == block.Block.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Period, "1999").SetProperty(b => b.ResetPolicy, Platform.Numbering.NumberResetPolicy.Yearly), Ct)).ShouldBe(1);
        }

        var patient = await RegisterAsync(frontOffice);
        await RefusedAsync(frontOffice, Visit(patient.Id, new PaperNumber(block.Numbers[0].Number, DateTimeOffset.UtcNow)), HttpStatusCode.BadRequest, "belongs to the series' year 1999");
    }

    [Fact]
    public async Task Paper_older_than_its_window_is_entered_only_after_a_second_person_approves()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var block = await mrd.PostAsync<DowntimeNumberBlockDetail>(Blocks, new IssueDowntimeNumbersRequest(DowntimeNumberKind.Visit, "OPD registration, long outage", 2));
        await BackdateAsync(block.Block.Id, TimeSpan.FromDays(6));
        var patient = await RegisterAsync(frontOffice);
        var writtenAt = DateTimeOffset.UtcNow.AddDays(-5);

        // Five days back is beyond the visit's 72-hour window.
        var refused = await RefusedAsync(frontOffice, Visit(patient.Id, new PaperNumber(block.Numbers[0].Number, writtenAt)), HttpStatusCode.UnprocessableEntity,
            "beyond its back-entry window");
        refused.GetProperty("code").GetString().ShouldBe("backentry.late");

        // Paper within the window needs no approval.
        using (var within = await frontOffice.SendRawAsync(HttpMethod.Post, Late,
                   new RequestLateBackEntry(BackEntryRecord.Visit, DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1), 1, "Outage")))
        {
            within.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        // The desk asks for one record; it waits (202), and the desk cannot approve its own request.
        using var asked = await frontOffice.SendRawAsync(HttpMethod.Post, Late,
            new RequestLateBackEntry(BackEntryRecord.Visit, writtenAt.AddHours(-1), writtenAt.AddHours(1), 1, "Six-day server outage; tally of 1 OPD slip"));
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted, await asked.Content.ReadAsStringAsync(Ct));
        var approval = (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
        approval.Kind.ShouldBe("patients.late_back_entry");
        approval.CanDecide.ShouldBeFalse();
        using (var self = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approval.Id}/approve", new ApproveRequest(null, approval.Version)))
        {
            self.StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.UnprocessableEntity);
        }

        // Still refused until it is approved.
        await RefusedAsync(frontOffice, Visit(patient.Id, new PaperNumber(block.Numbers[0].Number, writtenAt)), HttpStatusCode.UnprocessableEntity, "beyond its back-entry window");

        var seen = await mrd.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approval.Id}");
        seen.CanDecide.ShouldBeTrue();
        (await mrd.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approval.Id}/approve", new ApproveRequest("Tally checked", seen.Version)))
            .Status.ShouldBe(ApprovalStatus.Approved);

        // Another user is not covered by the desk's approval.
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        await RefusedAsync(nurse, Visit(patient.Id, new PaperNumber(block.Numbers[0].Number, writtenAt)), HttpStatusCode.UnprocessableEntity, "beyond its back-entry window");

        var entered = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", Visit(patient.Id, new PaperNumber(block.Numbers[0].Number, writtenAt)));
        entered.VisitNumber.ShouldBe(block.Numbers[0].Number);
        var detail = await mrd.GetAsync<DowntimeNumberBlockDetail>($"{Blocks}/{block.Block.Id}");
        detail.Numbers[0].LateBackEntry.ShouldBeTrue();

        // The approval covered one record: the next one is refused again.
        var next = await RegisterAsync(frontOffice);
        await RefusedAsync(frontOffice, Visit(next.Id, new PaperNumber(block.Numbers[1].Number, writtenAt.AddMinutes(10))), HttpStatusCode.UnprocessableEntity,
            "beyond its back-entry window");
        var grants = await frontOffice.GetAsync<List<LateBackEntryGrantDto>>(Late);
        grants.ShouldContain(g => g.MaxRecords == 1 && g.Used == 1 && !g.Usable && g.ApprovedByName != null);
    }

    [Fact]
    public async Task The_windows_in_force_are_listed_with_their_bounds()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var windows = await frontOffice.GetAsync<List<BackEntryWindowDto>>("/api/v1/downtime/back-entry/windows");
        windows.Select(w => w.Record).ShouldBe(Enum.GetValues<BackEntryRecord>());
        windows.Single(w => w.Record == BackEntryRecord.Visit).ShouldSatisfyAllConditions(
            w => w.DefaultHours.ShouldBe(72), w => w.MaxHours.ShouldBe(14 * 24), w => w.SettingKey.ShouldBe("downtime.backEntryHours.visit"));
        windows.Single(w => w.Record == BackEntryRecord.NursingChart).DefaultHours.ShouldBe(24);
        windows.Single(w => w.Record == BackEntryRecord.Registration).DefaultHours.ShouldBe(90 * 24);
    }

    private async Task BackdateAsync(Guid blockId, TimeSpan by)
    {
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<DowntimeNumberBlock>()
            .Where(b => b.Id == blockId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.CreatedAt, b => b.CreatedAt - by), Ct)).ShouldBe(1);
    }

    private static async Task<JsonElement> RefusedAsync(ApiClient client, CreateVisitRequest visit, HttpStatusCode status, string reason) =>
        await RefusedAsync(client, "/api/v1/visits", visit, status, reason);

    private static async Task<JsonElement> RefusedAsync(ApiClient client, string path, object body, HttpStatusCode status, string reason)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, text);
        text.ShouldContain(reason);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static CreateVisitRequest Visit(Guid patientId, PaperNumber? paper) => new(
        patientId, VisitType.HealthCheckup, null, null, null, PayerType.Self, null, ReferralSource.Self, null, null, false, null, null,
        TokenPriority.Normal, IssueToken: false, NotifyPatient: false, Paper: paper);

    [Fact]
    public async Task Bill_numbers_voided_unused_count_as_cancelled_in_gstr1_table_13()
    {
        await using var einvoicing = await server.EInvoicingAsync(EInvoicing.Today().AddDays(-1), Ct);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var month = $"{EInvoicing.Today():yyyy-MM}";
        var before = await OpBillsIssuedAsync(accountant, month);

        // Three OP bill numbers reserved for a downtime: one written on a paper bill, one spoilt, one returned unused.
        var block = await mrd.PostAsync<DowntimeNumberBlockDetail>(Blocks, new IssueDowntimeNumbersRequest(DowntimeNumberKind.OpBill, "OPD billing counter 2", 3));
        var draft = await DraftAsync(frontOffice, billing, (await RegisterAsync(frontOffice)).Id);
        await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize",
            new FinalizeBillRequest(draft.Version, new PaperNumber(block.Numbers[0].Number, block.Block.IssuedAt.AddSeconds(5))));
        var current = await mrd.GetAsync<DowntimeNumberBlockDetail>($"{Blocks}/{block.Block.Id}");
        await mrd.PostAsync($"{Blocks}/{block.Block.Id}/void", new VoidDowntimeNumberRequest(block.Numbers[2].Number, "Torn", current.Block.Version));
        current = await mrd.GetAsync<DowntimeNumberBlockDetail>($"{Blocks}/{block.Block.Id}");
        await mrd.PostAsync<DowntimeNumberBlockDetail>($"{Blocks}/{block.Block.Id}/return", new ReturnDowntimeBlockRequest("Counter closed", current.Block.Version));

        // Table 13 counts all three serials and the two voided ones as cancelled: one more bill was issued, net.
        var after = await OpBillsIssuedAsync(accountant, month);
        (after.Total - before.Total, after.Cancelled - before.Cancelled).ShouldBe((3, 2));
        block.Numbers.ShouldAllBe(n => string.CompareOrdinal(n.Number, after.From) >= 0 && string.CompareOrdinal(n.Number, after.To) <= 0);
    }

    [Fact]
    public async Task A_paper_bill_entered_after_its_months_gstr1_was_filed_is_flagged_and_listed_for_amendment()
    {
        await using var einvoicing = await server.EInvoicingAsync(EInvoicing.Today().AddDays(-1), Ct);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var today = EInvoicing.Today();
        var block = await mrd.PostAsync<DowntimeNumberBlockDetail>(Blocks, new IssueDowntimeNumbersRequest(DowntimeNumberKind.OpBill, "OPD billing counter 3", 2));

        // The month's filing not recorded: a paper bill for it is entered without a warning.
        var first = await DraftAsync(frontOffice, billing, (await RegisterAsync(frontOffice)).Id);
        var before = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{first.Id}/finalize",
            new FinalizeBillRequest(first.Version, new PaperNumber(block.Numbers[0].Number, block.Block.IssuedAt.AddSeconds(5))));
        before.GstFilingWarning.ShouldBeNull();

        // As if this month's GSTR-1 had been filed (the API records a month only once it has ended).
        var filingId = Guid.NewGuid();
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            await db.Set<GstReturnFiling>().Where(f => f.Gstin == EInvoicing.Hospital && f.Year == today.Year && f.Month == today.Month).ExecuteDeleteAsync(Ct);
            db.Add(new GstReturnFiling { Id = filingId, Gstin = EInvoicing.Hospital, Year = today.Year, Month = today.Month, Arn = "AA330000000001Z", FiledOn = today, RecordedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        try
        {
            // Entered after the filing: the bill says it is not in the filed return.
            var second = await DraftAsync(frontOffice, billing, (await RegisterAsync(frontOffice)).Id);
            var after = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{second.Id}/finalize",
                new FinalizeBillRequest(second.Version, new PaperNumber(block.Numbers[1].Number, block.Block.IssuedAt.AddSeconds(10))));
            after.GstFilingWarning.ShouldNotBeNull().ShouldContain("AA330000000001Z");
            after.GstFilingWarning.ShouldContain("amendment");
            (await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{first.Id}")).GstFilingWarning.ShouldBeNull("the first was entered before the filing");

            // GSTR-1 of the month it was entered in lists it for amendment; here, as if entered next month.
            var next = today.AddMonths(1);
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var enteredNextMonth = new DateTimeOffset(next.Year, next.Month, 1, 6, 0, 0, TimeSpan.Zero);
                (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Bill>().IgnoreQueryFilters().Where(b => b.Id == second.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(b => b.EnteredFromPaperAt, enteredNextMonth), Ct)).ShouldBe(1);
            }

            var gstr1 = await accountant.GetAsync<Gstr1Return>($"/api/v1/billing/gst-returns/gstr1?month={next:yyyy-MM}");
            gstr1.Filing.ShouldBeNull("next month's filing is not recorded");
            var late = gstr1.LateEntries.ShouldNotBeNull().Where(l => l.Number == after.BillNumber).ShouldHaveSingleItem();
            (late.FiledYear, late.FiledMonth, late.FiledArn).ShouldBe((today.Year, today.Month, "AA330000000001Z"));
            gstr1.LateEntries.ShouldNotContain(l => l.Number == before.BillNumber);
            gstr1.Problems.ShouldContain(p => p.Contains(after.BillNumber!, StringComparison.Ordinal) && p.Contains("amendment", StringComparison.Ordinal));
            (await accountant.GetAsync<Gstr1Return>($"/api/v1/billing/gst-returns/gstr1?month={today:yyyy-MM}")).Filing.ShouldNotBeNull().Arn.ShouldBe("AA330000000001Z");
        }
        finally
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<GstReturnFiling>().Where(f => f.Id == filingId).ExecuteDeleteAsync(Ct);
        }
    }

    private static async Task<Gstr1DocumentsIssued> OpBillsIssuedAsync(ApiClient accountant, string month) =>
        (await accountant.GetAsync<Gstr1Return>($"/api/v1/billing/gst-returns/gstr1?month={month}")).DocumentsIssued.SingleOrDefault(d => d.Series == "OP bills")
        ?? new Gstr1DocumentsIssued(1, "OP bills", string.Empty, string.Empty, 0, 0);

    private static async Task<BillDetail> DraftAsync(ApiClient frontOffice, ApiClient billing, Guid patientId)
    {
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", Visit(patientId, null));
        var dressing = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=PROC-DRESS")).Single(s => s.Code == "PROC-DRESS");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(patientId, visit.Id, null, dressing.Id, 1m, null, null, null, null, null));
        return await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));
    }

    private static async Task<PatientCreated> RegisterAsync(ApiClient frontOffice) =>
        await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: TestPatients.FirstName("Downtime"), MiddleName: null, LastName: "Numbers", Gender: Gender.Female,
            DateOfBirth: null, AgeYears: 20 + Random.Shared.Next(60), AgeMonths: null, Phone: null, AlternatePhone: null, Email: null,
            Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null,
            PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false,
            Notes: null, ConfirmedNotDuplicate: true));

    private static PaymentLineInput Upi(decimal amount) =>
        new(PaymentMode.Upi, amount, "UTR" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10)))), null, null, null, null, null);

    private static async Task<ShiftDto?> OpenShiftAsync(ApiClient billing)
    {
        using var current = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current");
        current.EnsureSuccessStatusCode();
        return current.StatusCode == HttpStatusCode.NoContent
            ? await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OPD billing", 0m))
            : null;
    }

    private static async Task CloseShiftAsync(ApiClient billing, ShiftDto? opened)
    {
        if (opened is null)
        {
            return;
        }

        var shift = await billing.GetAsync<ShiftDto>($"/api/v1/billing/shifts/{opened.Id}");
        await billing.PostAsync<ShiftDto>($"/api/v1/billing/shifts/{opened.Id}/close", new CloseShiftRequest(shift.ExpectedCash, null, shift.Version));
    }
}
