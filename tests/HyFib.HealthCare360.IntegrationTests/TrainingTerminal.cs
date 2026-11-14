using System.Net;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The training card/UPI terminal (the POS simulator) for tests of terminal sales, and the Billing counter that takes
/// a bill's balance on it. The terminal setting is the server's, which every test of the run shares: a test puts it back
/// when it is done.
/// </summary>
internal static class TrainingTerminal
{
    /// <summary>
    /// Card and UPI sales go to the training terminal for the rest of the test, which approves a sale
    /// <paramref name="approveAfterSeconds"/> after it was pushed (by its own clock); then the setting is put back.
    /// <paramref name="loseUploadAnswer"/> makes the terminal take each sale but lose the answer to the upload.
    /// </summary>
    public static async Task<IAsyncDisposable> UseAsync(HospitalServer server, int approveAfterSeconds, bool loseUploadAnswer = false)
    {
        const string key = "integrations.pos";
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}"))
            .FirstOrDefault(s => s.Key == key && s.FacilityId is null)?.Value;
        await server.SaveSettingAsync(admin, key, new SaveSettingRequest(
            new JsonObject { ["enabled"] = true, ["provider"] = "simulator", ["simulatorApproveAfterSeconds"] = approveAfterSeconds, ["simulatorLoseUploadAnswer"] = loseUploadAnswer }, null, false, null));
        return new Restore(() => server.SaveSettingAsync(admin, key,
            new SaveSettingRequest(before?.DeepClone() ?? new JsonObject { ["enabled"] = false }, null, false, null)));
    }

    /// <summary>The Billing cashier's shift at the counter, opened when none is.</summary>
    public static async Task OpenCounterShiftAsync(ApiClient billing)
    {
        using var current = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current");
        if (current.StatusCode == HttpStatusCode.NoContent)
        {
            await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 1", OpeningFloat: 0m));
        }
    }

    /// <summary>A new patient's registration fee, billed and finalized, with its whole balance due.</summary>
    public static async Task<BillDetail> FinalizedBillAsync(HospitalServer server, ApiClient billing)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var surname = "Pos" + new string(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        var registered = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: TestPatients.FirstName("Lata"), MiddleName: null, LastName: surname, Gender: Gender.Female,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(365 * 40)), AgeYears: null, AgeMonths: null,
            Phone: "7" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        var service = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=REG-NEW")).Single(s => s.Code == "REG-NEW");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(registered.Id, null, null, service.Id, 1m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, registered.Id, null));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        bill.DueAmount.ShouldBeGreaterThan(0m);
        return bill;
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
