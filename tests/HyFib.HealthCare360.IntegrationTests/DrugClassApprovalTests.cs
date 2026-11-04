using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A drug handled less strictly (no longer controlled or high-alert, or on a lower schedule) is changed only when two
/// people agree: whoever keeps the item master asks, and a pharmacist, someone else, approves. Until then the drug keeps
/// its class, so its register and the witness at the bedside stay; the rest of the item is saved at once. Made stricter,
/// a class applies at once.
/// </summary>
public sealed class DrugClassApprovalTests(HospitalServer server)
{
    private const string Items = "/api/v1/inventory/items";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_drug_handled_less_strictly_waits_for_a_pharmacist_and_the_rest_is_saved_at_once()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var id = await CreateAsync(admin, "Morphine", DrugSchedule.X, controlled: true, highAlert: true);
        var item = await admin.GetAsync<ItemDetail>($"{Items}/{id}");

        // Schedule H, no longer controlled nor high-alert, and a new reorder level: the reorder level is saved, the class waits.
        const string reason = "Kept in the pain clinic's own register now.";
        using var asked = await admin.SendRawAsync(HttpMethod.Put, $"{Items}/{id}",
            Save(item, schedule: DrugSchedule.H, controlled: false, highAlert: false) with { ReorderLevel = 50, ClassChangeReason = reason });
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted, await asked.Content.ReadAsStringAsync(Ct));
        var request = (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
        (request.Kind, request.Status, request.SubjectId, request.Reason)
            .ShouldBe(("inventory.drug_class", ApprovalStatus.Pending, id, reason));
        request.Details.ShouldContain(d => d.Label == "What it changes"
            && d.Value.Contains("no longer entered in the statutory register", StringComparison.Ordinal)
            && d.Value.Contains("no longer witnessed by a second nurse", StringComparison.Ordinal));

        var waiting = await admin.GetAsync<ItemDetail>($"{Items}/{id}");
        (waiting.ReorderLevel, waiting.IsControlled, waiting.IsHighAlert, waiting.Schedule).ShouldBe((50m, true, true, DrugSchedule.X));
        waiting.PendingClass.ShouldNotBeNull().Asked.ShouldBe(new DrugSafetyClass(DrugSchedule.H, IsControlled: false, IsHighAlert: false));

        // Never by the one who asked, however many permissions they hold; a pharmacist decides.
        (await RefusalAsync(admin, $"/api/v1/approvals/{request.Id}/approve", new ApproveRequest("Agreed.", request.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        var approved = await pharmacist.PostAsync<ApprovalRequestDto>(
            $"/api/v1/approvals/{request.Id}/approve", new ApproveRequest("Agreed with the pain clinic.", request.Version));
        approved.Status.ShouldBe(ApprovalStatus.Approved);

        var changed = await admin.GetAsync<ItemDetail>($"{Items}/{id}");
        (changed.IsControlled, changed.IsHighAlert, changed.Schedule, changed.PendingClass).ShouldBe((false, false, DrugSchedule.H, (PendingDrugClass?)null));
        (await AuditAsync(id)).ShouldContain(e => e.Action == "inventory.drug_class_changed" && e.Severity == AuditSeverity.Warning);
    }

    [Fact]
    public async Task A_drug_handled_more_strictly_or_a_new_one_is_saved_at_once()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var id = await CreateAsync(admin, "Insulin", DrugSchedule.H, controlled: false, highAlert: false);
        var item = await admin.GetAsync<ItemDetail>($"{Items}/{id}");

        using var saved = await admin.SendRawAsync(HttpMethod.Put, $"{Items}/{id}", Save(item, highAlert: true));
        saved.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var stricter = await admin.GetAsync<ItemDetail>($"{Items}/{id}");
        (stricter.IsHighAlert, stricter.PendingClass).ShouldBe((true, (PendingDrugClass?)null));
    }

    [Fact]
    public async Task One_change_waits_at_a_time_and_it_goes_stale_when_the_class_changes_meanwhile()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var id = await CreateAsync(admin, "Potassium chloride concentrate", DrugSchedule.H1, controlled: false, highAlert: true);
        var item = await admin.GetAsync<ItemDetail>($"{Items}/{id}");

        using var asked = await admin.SendRawAsync(HttpMethod.Put, $"{Items}/{id}", Save(item, highAlert: false));
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var request = (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;

        // Saved again asking the same class, nothing new is asked; asking another, it is refused while the first waits.
        item = await admin.GetAsync<ItemDetail>($"{Items}/{id}");
        using (var same = await admin.SendRawAsync(HttpMethod.Put, $"{Items}/{id}", Save(item, highAlert: false) with { ReorderLevel = 20 }))
        {
            same.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        item = await admin.GetAsync<ItemDetail>($"{Items}/{id}");
        (item.ReorderLevel, item.IsHighAlert).ShouldBe((20m, true));
        (await RefusalAsync(admin, $"{Items}/{id}", Save(item, schedule: DrugSchedule.H, highAlert: false), HttpMethod.Put))
            .ShouldBe((HttpStatusCode.Conflict, "inventory.drug_class_waiting"));

        // Made stricter meanwhile (now controlled too): the waiting change is no longer the one asked for.
        using (var stricter = await admin.SendRawAsync(HttpMethod.Put, $"{Items}/{id}", Save(item, controlled: true)))
        {
            stricter.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await RefusalAsync(pharmacist, $"/api/v1/approvals/{request.Id}/approve", new ApproveRequest("Agreed.", request.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "approval.stale"));
        var kept = await admin.GetAsync<ItemDetail>($"{Items}/{id}");
        (kept.IsControlled, kept.IsHighAlert, kept.Schedule).ShouldBe((true, true, DrugSchedule.H1));
    }

    [Fact]
    public async Task A_change_nobody_decided_in_time_no_longer_waits_and_is_asked_for_anew()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var id = await CreateAsync(admin, "Heparin", DrugSchedule.H, controlled: false, highAlert: true);
        var item = await admin.GetAsync<ItemDetail>($"{Items}/{id}");
        using var asked = await admin.SendRawAsync(HttpMethod.Put, $"{Items}/{id}", Save(item, highAlert: false));
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var first = (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;

        // It ran out: the drug keeps its class, nothing is shown waiting, and saving it so again asks anew.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<ApprovalRequest>().IgnoreQueryFilters().SingleAsync(r => r.Id == first.Id, Ct)).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync(Ct);
        }

        item = await admin.GetAsync<ItemDetail>($"{Items}/{id}");
        (item.IsHighAlert, item.PendingClass).ShouldBe((true, (PendingDrugClass?)null));
        using var again = await admin.SendRawAsync(HttpMethod.Put, $"{Items}/{id}", Save(item, highAlert: false));
        again.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await again.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!.Id.ShouldNotBe(first.Id);
    }

    /// <summary>A new drug of the master, from the hospital administrator (who keeps the item master).</summary>
    internal static async Task<Guid> CreateAsync(ApiClient admin, string name, DrugSchedule schedule, bool controlled, bool highAlert)
    {
        using var response = await admin.SendRawAsync(HttpMethod.Post, Items, new SaveItemRequest(
            Code: $"IT-{Letters(10).ToUpperInvariant()}", Name: $"{name} {Letters(6)}", GenericName: name, BrandName: null, Type: ItemType.Drug,
            CategoryId: null, Strength: null, DosageForm: "Injection", PackSize: "1 vial", BaseUnit: "VIAL", PurchaseUnit: "VIAL", PurchaseUnitFactor: 1,
            HsnCode: "300490", GstRate: 5m, Schedule: schedule, IsControlled: controlled, IsColdChain: false, VedClass: VedClass.Vital,
            ReorderLevel: 0, ReorderQuantity: 0, MinimumStock: 0, MaximumStock: 0, Manufacturer: "Test Pharma", Barcode: null, IsActive: true, Version: null,
            IsHighAlert: highAlert));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, "a new drug takes the class it is given at once");
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("id").GetGuid();
    }

    /// <summary>The item as read, saved again with these changes to its class.</summary>
    private static SaveItemRequest Save(ItemDetail i, DrugSchedule? schedule = null, bool? controlled = null, bool? highAlert = null) => new(
        i.Code, i.Name, i.GenericName, i.BrandName, i.Type, i.CategoryId, i.Strength, i.DosageForm, i.PackSize, i.BaseUnit, i.PurchaseUnit,
        i.PurchaseUnitFactor, i.HsnCode, i.GstRate, schedule ?? i.Schedule, controlled ?? i.IsControlled, i.IsColdChain, i.VedClass, i.ReorderLevel,
        i.ReorderQuantity, i.MinimumStock, i.MaximumStock, i.Manufacturer, i.Barcode, i.IsActive, i.Version, i.Specification, i.CeilingPrice,
        highAlert ?? i.IsHighAlert);

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body, HttpMethod? method = null)
    {
        using var response = await api.SendRawAsync(method ?? HttpMethod.Post, path, body);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private async Task<List<AuditEvent>> AuditAsync(Guid itemId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var key = itemId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.EntityType == nameof(Item) && e.EntityId == key).ToListAsync(Ct);
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
