using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The doctor's pickers find a service or a medicine by its words in any order. Both searches took the whole term as one
/// phrase, so "chest X-ray", the order picker's own example, found nothing: the service is "X-ray chest PA view". Each
/// word must still match, so a word the name lacks finds nothing.
/// </summary>
public sealed class WordSearchTests(HospitalServer server)
{
    [Fact]
    public async Task A_service_is_found_by_its_words_in_any_order()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var tag = Letters(8);
        var name = $"X-ray wrist PA view {tag}";
        await admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            $"XR-{tag.ToUpperInvariant()}", name, null, ServiceCategory.Radiology, null, 450m, 0m, "999312", true, false, null, true, null));

        (await ServicesAsync(doctor, $"wrist X-ray {tag}")).ShouldBe([name], "the words of the name, in another order");
        (await ServicesAsync(doctor, $"{tag} view")).ShouldBe([name]);
        (await ServicesAsync(doctor, $"XR-{tag}")).ShouldBe([name], "its code, as before");
        (await ServicesAsync(doctor, $"wrist MRI {tag}")).ShouldBeEmpty("every word must be in the name or the code");
    }

    [Fact]
    public async Task A_medicine_is_found_by_its_words_in_any_order_and_by_its_code_as_before()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var tag = Letters(8);
        var code = $"IT-{tag.ToUpperInvariant()}";
        var name = $"Testolol {tag} 25 mg Tablet";
        var barcode = $"8901234 {Digits(6)}";
        await admin.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: code, Name: name, GenericName: $"Testolol {tag}", BrandName: $"Calmheart {tag}", Type: ItemType.Drug,
            CategoryId: null, Strength: "25 mg", DosageForm: "Tablet", PackSize: "Strip of 10", BaseUnit: "TAB", PurchaseUnit: "TAB",
            PurchaseUnitFactor: 1, HsnCode: "300490", GstRate: 5m, Schedule: DrugSchedule.Otc, IsControlled: false, IsColdChain: false,
            VedClass: VedClass.Essential, ReorderLevel: 0, ReorderQuantity: 0, MinimumStock: 0, MaximumStock: 0, Manufacturer: "Test Pharma",
            Barcode: barcode, IsActive: true, Version: null));

        (await DrugsAsync(doctor, $"tablet {tag} 25")).ShouldBe([name], "the words of the name, in another order");
        (await DrugsAsync(doctor, $"calmheart {tag} tablet")).ShouldBe([name], "a word may be in the brand, another in the name");
        (await DrugsAsync(doctor, code)).ShouldBe([name], "its code alone, as before");
        (await DrugsAsync(doctor, barcode)).ShouldBe([name], "its barcode typed as printed, with its space, as before");
        (await DrugsAsync(doctor, $"{tag} 50 mg")).ShouldBeEmpty("every word must be in the name, the generic or the brand");
    }

    private static async Task<List<string>> ServicesAsync(ApiClient client, string term) =>
        [.. (await client.GetAsync<List<ServiceItemDto>>($"/api/v1/masters/lookups/services?search={Uri.EscapeDataString(term)}")).Select(s => s.Name)];

    private static async Task<List<string>> DrugsAsync(ApiClient client, string term) =>
        [.. (await client.GetAsync<List<PrescribableDrug>>($"/api/v1/emr/drugs?search={Uri.EscapeDataString(term)}&take=50")).Select(d => d.Name)];

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));
}
