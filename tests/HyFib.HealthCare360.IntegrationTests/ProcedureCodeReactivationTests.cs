using System.Net;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A procedure code switched off by mistake (or one the hospital needs again) is brought back by MRD: the catalogue
/// screen finds switched-off codes when asked, and switching one on offers it in searches again. The pickers in coding
/// and the doctor's notes never see switched-off codes, and the change is in the audit trail.
/// </summary>
public sealed class ProcedureCodeReactivationTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Mrd_switches_a_switched_off_code_back_on()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var code = "HX" + Random.Shared.Next(10_000, 99_999);
        var added = await mrd.PostAsync<ProcedureCodeDto>("/api/v1/mrd/procedure-codes",
            new SaveProcedureCodeRequest(code, "Wound wash-out, hospital code", null, "Minor procedures", null, IsActive: true));
        var off = await mrd.PutAsync<ProcedureCodeDto>($"/api/v1/mrd/procedure-codes/{added.Id}",
            new SaveProcedureCodeRequest(code, added.Title, added.System, added.Category, null, IsActive: false, added.Version));

        (await SearchAsync(mrd, code, includeInactive: false)).ShouldBeEmpty();
        (await SearchAsync(mrd, code, includeInactive: true)).ShouldHaveSingleItem().IsActive.ShouldBeFalse();

        var on = await mrd.PutAsync<ProcedureCodeDto>($"/api/v1/mrd/procedure-codes/{added.Id}",
            new SaveProcedureCodeRequest(code, added.Title, added.System, added.Category, null, IsActive: true, off.Version));

        on.IsActive.ShouldBeTrue();
        (await SearchAsync(mrd, code, includeInactive: false)).ShouldHaveSingleItem().Id.ShouldBe(added.Id);
        (await ActiveChangesAsync(added.Id)).ShouldBe(["true → false", "false → true"]);
    }

    [Fact]
    public async Task Only_a_coder_switches_a_code_on()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var code = "HY" + Random.Shared.Next(10_000, 99_999);
        var added = await mrd.PostAsync<ProcedureCodeDto>("/api/v1/mrd/procedure-codes",
            new SaveProcedureCodeRequest(code, "Dressing, hospital code", null, null, null, IsActive: false));

        using var refused = await doctor.SendRawAsync(HttpMethod.Put, $"/api/v1/mrd/procedure-codes/{added.Id}",
            new SaveProcedureCodeRequest(code, added.Title, added.System, null, null, IsActive: true, added.Version));

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SearchAsync(mrd, code, includeInactive: true)).ShouldHaveSingleItem().IsActive.ShouldBeFalse();
    }

    private static Task<List<ProcedureCodeDto>> SearchAsync(ApiClient client, string code, bool includeInactive) =>
        client.GetAsync<List<ProcedureCodeDto>>($"/api/v1/mrd/procedure-codes?search={code}&includeInactive={(includeInactive ? "true" : "false")}");

    /// <summary>The audited from/to of the code's active flag, oldest first.</summary>
    private async Task<List<string>> ActiveChangesAsync(Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Category == AuditCategories.Data && e.Action == "ProcedureCode.update" && e.EntityId == id.ToString())
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(Ct);
        return [.. events.Select(e => e.Details).Where(d => d is not null && d.Contains("isActive", StringComparison.Ordinal)).Select(ActiveChange)];
    }

    private static string ActiveChange(string? details)
    {
        using var json = System.Text.Json.JsonDocument.Parse(details!);
        var change = json.RootElement.GetProperty("isActive");
        return $"{change.GetProperty("from").GetRawText()} → {change.GetProperty("to").GetRawText()}";
    }
}
