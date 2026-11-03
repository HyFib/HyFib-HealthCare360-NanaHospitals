using System.Net;
using System.Net.Http.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
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
/// The non-payable list changes only when two people agree (maker-checker, IRDAI non-payables), through the real API:
/// the hospital administrator, who keeps the payers, asks; the accountant, who approves prices, decides. An insured
/// patient's bill is the witness: a line the list makes non-payable is the patient's to pay, not the payer's. The payer
/// and the service are the test's own.
/// </summary>
public sealed class NonPayableApprovalTests(HospitalServer server)
{
    private const string Items = "/api/v1/insurance/non-payable-items";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_item_added_waits_and_the_payer_is_charged_for_it_until_a_second_person_approves()
    {
        var (admin, billing, accountant) = await PeopleAsync();
        var payer = await server.CreatePayerAsync(admin, Payer());
        var kit = await ServiceAsync(admin, "Admission kit", 400m);

        // An unbilled charge of an insured patient that the item would reach: the approver is told of it.
        var waiting = await VisitAsync(payer.Id);
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(waiting.PatientId, waiting.VisitId, null, kit, 1m, null, null, null, null, null));
        using var response = await admin.SendRawAsync(HttpMethod.Post, Items, new SaveNonPayableItemRequest(payer.Id, kit, null, "Kits are the patient's", "IRDAI list"));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var asked = (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
        (asked.Kind, asked.Reason).ShouldBe((NonPayableApprovals.Kind, "IRDAI list"));
        asked.Details.ShouldContain(d => d.Label == "Payer" && d.Value == payer.Name);
        asked.Details.ShouldContain(d => d.Label == "Change" && d.Value.StartsWith("Added to the non-payable list", StringComparison.Ordinal));
        asked.Details.ShouldContain(d => d.Label == "Not billed yet"
            && d.Value == "1 charge(s) of 1 insured patient(s), ₹400.00, not billed yet: their bills would charge them to the patients instead of the payer");

        // Listed as waiting to be added; meanwhile the payer is charged for the kit.
        var listed = (await admin.GetAsync<List<NonPayableItemDto>>($"{Items}?payerId={payer.Id}")).Single(i => i.ServiceItemId == kit);
        (listed.Waiting?.Change, listed.Waiting?.ApprovalId, listed.Notes).ShouldBe((NonPayableChange.Add, asked.Id, "Kits are the patient's"));
        (await PayerPaysAsync(billing, waiting, kit)).ShouldBeTrue("the item is not on the list until it is approved");

        // The administrator cannot approve their own request; the accountant does, and the next bill charges the patient.
        using (var own = await ApproveAsync(admin, asked.Id))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        await ApproveAsync(accountant, asked.Id, HttpStatusCode.OK);
        var item = (await admin.GetAsync<List<NonPayableItemDto>>($"{Items}?payerId={payer.Id}")).Single(i => i.ServiceItemId == kit);
        (item.Id, item.Waiting).ShouldBe((listed.Id, (PendingNonPayable?)null));
        var after = await VisitAsync(payer.Id);
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(after.PatientId, after.VisitId, null, kit, 1m, null, null, null, null, null));
        (await PayerPaysAsync(billing, after, kit)).ShouldBeFalse("on the list: the patient pays for it");
        (await AuditCountAsync("insurance.non_payable_changed", item.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task An_item_taken_off_waits_and_the_patient_pays_for_it_until_a_second_person_approves()
    {
        var (admin, billing, accountant) = await PeopleAsync();
        var payer = await server.CreatePayerAsync(admin, Payer());
        var kit = await ServiceAsync(admin, "Admission kit", 400m);
        var item = await AddAsync(admin, accountant, payer.Id, kit);

        // Twice is refused; taking it off is asked for, with a reason, and it stays in force meanwhile.
        using (var twice = await admin.SendRawAsync(HttpMethod.Post, Items, new SaveNonPayableItemRequest(payer.Id, kit, null, null)))
        {
            twice.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await twice.Content.ReadAsStringAsync(Ct)).ShouldContain(NonPayableApprovals.ExistsCode);
        }

        var asked = await admin.PostAsync<ApprovalRequestDto>($"{Items}/{item}/remove", new RemoveNonPayableItemRequest("Covered under the new agreement"));
        asked.Details.ShouldContain(d => d.Label == "Change" && d.Value.StartsWith("Taken off the non-payable list", StringComparison.Ordinal));
        using (var again = await admin.SendRawAsync(HttpMethod.Post, $"{Items}/{item}/remove", new RemoveNonPayableItemRequest(null)))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict, "one change of an item waits at a time");
        }

        (await admin.GetAsync<List<NonPayableItemDto>>($"{Items}?payerId={payer.Id}")).Single(i => i.Id == item).Waiting.ShouldNotBeNull().Change.ShouldBe(NonPayableChange.Remove);
        var waiting = await VisitAsync(payer.Id);
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(waiting.PatientId, waiting.VisitId, null, kit, 1m, null, null, null, null, null));
        (await PayerPaysAsync(billing, waiting, kit)).ShouldBeFalse("still on the list until it is approved");

        // Approved, it is off the list: the next bill charges the payer.
        await ApproveAsync(accountant, asked.Id, HttpStatusCode.OK);
        (await admin.GetAsync<List<NonPayableItemDto>>($"{Items}?payerId={payer.Id}")).ShouldNotContain(i => i.Id == item);
        var after = await VisitAsync(payer.Id);
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(after.PatientId, after.VisitId, null, kit, 1m, null, null, null, null, null));
        (await PayerPaysAsync(billing, after, kit)).ShouldBeTrue();
    }

    [Fact]
    public async Task An_item_waits_once_and_is_not_added_over_the_same_item_already_on_the_list()
    {
        var (admin, _, accountant) = await PeopleAsync();
        var payer = await server.CreatePayerAsync(admin, Payer());
        var kit = await ServiceAsync(admin, "Admission kit", 400m);

        // Asked again while it waits, with other notes: refused, one request per item waits at a time.
        var asked = await admin.PostAsync<ApprovalRequestDto>(Items, new SaveNonPayableItemRequest(payer.Id, kit, null, null));
        using (var again = await admin.SendRawAsync(HttpMethod.Post, Items, new SaveNonPayableItemRequest(payer.Id, kit, null, "Kits")))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await again.Content.ReadAsStringAsync(Ct)).ShouldContain("approval.already_requested");
        }

        // The same item put on the list meanwhile outside this request (restored or carried-over data, say): the
        // approval adds nothing, and the list keeps the one it had.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            db.Add(new NonPayableItem { PayerId = payer.Id, ServiceItemId = kit, Notes = "Carried over" });
            await db.SaveChangesAsync(Ct);
        }

        using var refused = await ApproveAsync(accountant, asked.Id);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain(NonPayableApprovals.ExistsCode);
        (await admin.GetAsync<List<NonPayableItemDto>>($"{Items}?payerId={payer.Id}")).Where(i => i.ServiceItemId == kit)
            .ShouldHaveSingleItem().Notes.ShouldBe("Carried over");
    }

    [Fact]
    public async Task The_approver_is_told_only_of_the_charges_a_change_would_move()
    {
        var (admin, billing, accountant) = await PeopleAsync();
        var payer = await server.CreatePayerAsync(admin, Payer());
        var kit = await ServiceAsync(admin, "Admission kit", 400m);
        var item = await AddAsync(admin, accountant, payer.Id, kit);
        var visit = await VisitAsync(payer.Id);
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(visit.PatientId, visit.VisitId, null, kit, 1m, null, null, null, null, null));

        // Every consumable for the payer: the kit not yet billed is non-payable already, so nothing moves.
        var consumables = await admin.PostAsync<ApprovalRequestDto>(Items, new SaveNonPayableItemRequest(payer.Id, null, ServiceCategory.Consumable, null));
        consumables.Details.ShouldContain(d => d.Label == "Item" && d.Value == "every consumable service");
        consumables.Details.ShouldContain(d => d.Label == "Not billed yet" && d.Value == "No charge of an insured patient not yet billed would be charged otherwise");

        // The kit taken off while that waits (not in force): the payer would be charged for it.
        var off = await admin.PostAsync<ApprovalRequestDto>($"{Items}/{item}/remove", new RemoveNonPayableItemRequest(null));
        off.Details.ShouldContain(d => d.Label == "Not billed yet"
            && d.Value == "1 charge(s) of 1 insured patient(s), ₹400.00, not billed yet: their bills would charge them to the payer instead of the patients");
    }

    private async Task<(ApiClient Admin, ApiClient Billing, ApiClient Accountant)> PeopleAsync() => (
        await server.SignInAsync(HospitalServer.Users.HospitalAdmin),
        await server.SignInAsync(HospitalServer.Users.Billing),
        await server.SignInAsync(HospitalServer.Users.Accountant));

    /// <summary>The item put on the list for the payer, approved by the accountant: its id.</summary>
    private static async Task<Guid> AddAsync(ApiClient admin, ApiClient accountant, Guid payerId, Guid serviceId)
    {
        var asked = await admin.PostAsync<ApprovalRequestDto>(Items, new SaveNonPayableItemRequest(payerId, serviceId, null, null));
        await ApproveAsync(accountant, asked.Id, HttpStatusCode.OK);
        return (await admin.GetAsync<List<NonPayableItemDto>>($"{Items}?payerId={payerId}")).Single(i => i.ServiceItemId == serviceId).Id;
    }

    private static async Task<HttpResponseMessage> ApproveAsync(ApiClient checker, Guid approvalId)
    {
        var request = await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        return await checker.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approvalId}/approve", new ApproveRequest(null, request.Version));
    }

    private static async Task ApproveAsync(ApiClient checker, Guid approvalId, HttpStatusCode expected)
    {
        using var response = await ApproveAsync(checker, approvalId);
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Whether the visit's bill charges the payer for the service: the billing desk makes the bill from its charges.</summary>
    private static async Task<bool> PayerPaysAsync(ApiClient billing, (Guid PatientId, Guid VisitId) visit, Guid serviceId)
    {
        var bill = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.VisitId, null, null));
        return bill.Lines.Single(l => l.ServiceItemId == serviceId).IsPayerPayable;
    }

    private async Task<Guid> ServiceAsync(ApiClient admin, string name, decimal price) =>
        (await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            "SV-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), name, null, ServiceCategory.Consumable, null, price, 0m, null,
            IsDiscountAllowed: true, IsPriceEditable: false, Description: null, IsActive: true, Version: null))).Id;

    /// <summary>An out-patient visit of a new patient, paid for by the payer.</summary>
    private async Task<(Guid PatientId, Guid VisitId)> VisitAsync(Guid payerId)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Ravi"), MiddleName: null, LastName: "Kit" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false))).Id;
        await server.VerifiedPolicyAsync(patient, payerId);
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patient, VisitType.HealthCheckup, null, null, null, PayerType.Corporate, payerId, ReferralSource.Corporate, null, null, false, null, null,
            TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
        return (patient, visit.Id);
    }

    private async Task<int> AuditCountAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = entityId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().CountAsync(e => e.Action == action && e.EntityId == id, Ct);
    }

    private static SavePayerRequest Payer() => new(
        "PY" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), $"Cauvery Mills {Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}",
        PayerCategory.Corporate, "Meena Iyer", "9876543210", null, null, null, null, CreditDays: 30, DefaultCoPayPercent: 0m, PortalUrl: null,
        IsActive: true, Notes: null, Version: null);

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
