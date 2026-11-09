using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The planned procedures of a pre-authorisation are coded (INS-04): each is a procedure of PM-JAY's imported HBP master or
/// an ICD-10-PCS code of the hospital's catalogue, with a quantity and the amount asked. A code neither master lists, or one
/// it has retired, is refused; the name saved is the master's, not the desk's. What was typed before procedures were coded
/// is kept, read only, beside the coded lines. The HBP procedures are this test's own, written to the master directly.
/// </summary>
public sealed class PreAuthProcedureCodingTests(HospitalServer server)
{
    private const string PreAuths = "/api/v1/insurance/preauths";
    private const string Cholecystectomy = "0FT44ZZ";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Planned_procedures_are_coded_from_the_masters_and_checked_against_them()
    {
        var desk = await server.SignInAsync(Users.InsuranceDesk);
        var (hbp, retired) = await HbpProceduresAsync();
        var policyId = await PolicyAsync();

        // Both masters are offered to the desk, the HBP master with the scheme's rate.
        var hbpOptions = await desk.GetAsync<List<PreAuthProcedureOption>>($"{PreAuths}/procedure-options?system=Hbp&search={hbp}");
        hbpOptions.ShouldHaveSingleItem().ShouldBe(new PreAuthProcedureOption(PreAuthProcedureSystem.Hbp, hbp, "Laparoscopic cholecystectomy (test)", 22_800m));
        (await desk.GetAsync<List<PreAuthProcedureOption>>($"{PreAuths}/procedure-options?system=Hbp&search={retired}")).ShouldBeEmpty();
        (await desk.GetAsync<List<PreAuthProcedureOption>>($"{PreAuths}/procedure-options?system=Icd10Pcs&search={Cholecystectomy}"))
            .First().Code.ShouldBe(Cholecystectomy);

        // Coded lines are saved in the desk's order, with the masters' names (the code as typed, in any case).
        var id = (await desk.PostAsync<JsonElement>(PreAuths, Request(policyId, null,
            [Line(PreAuthProcedureSystem.Hbp, hbp.ToLowerInvariant(), 1, 22_800m), Line(PreAuthProcedureSystem.Icd10Pcs, Cholecystectomy, 1, 0m)])))
            .GetProperty("id").GetGuid();
        var saved = await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{id}");
        saved.Procedures.Select(p => (p.System, p.Code, p.Quantity, p.Amount)).ShouldBe(
            [(PreAuthProcedureSystem.Hbp, hbp, 1, 22_800m), (PreAuthProcedureSystem.Icd10Pcs, Cholecystectomy, 1, 0m)]);
        saved.Procedures[0].Name.ShouldBe("Laparoscopic cholecystectomy (test)");
        saved.Procedures[1].Name.ShouldStartWith("Laparoscopic cholecystectomy");
        saved.LegacyProcedureText.ShouldBeNull();
        (await AuditAsync("insurance.preauth.procedures", id)).Summary.ShouldNotBeNull().ShouldContain($"{hbp} Laparoscopic cholecystectomy (test)");

        // Unknown and retired codes are refused, naming the line; nothing is saved.
        await RefusedAsync(desk, id, saved.Version, Line(PreAuthProcedureSystem.Hbp, $"{hbp[..5]}Z", 1, 100m), "procedures[1].code", "not in the imported PM-JAY HBP master");
        await RefusedAsync(desk, id, saved.Version, Line(PreAuthProcedureSystem.Hbp, retired, 1, 100m), "procedures[1].code", "was retired from the HBP master");
        await RefusedAsync(desk, id, saved.Version, Line(PreAuthProcedureSystem.Icd10Pcs, "0ZZZZZZ", 1, 100m), "procedures[1].code", "not an ICD-10-PCS code");

        // A quantity out of range, or the same code twice, does not pass validation.
        using (var badQuantity = await desk.SendRawAsync(HttpMethod.Put, $"{PreAuths}/{id}", Request(policyId, saved.Version, [Line(PreAuthProcedureSystem.Hbp, hbp, 0, 1m)])))
        {
            badQuantity.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var twice = await desk.SendRawAsync(HttpMethod.Put, $"{PreAuths}/{id}", Request(policyId, saved.Version,
            [Line(PreAuthProcedureSystem.Hbp, hbp, 1, 1m), Line(PreAuthProcedureSystem.Hbp, hbp, 2, 1m)])))
        {
            twice.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await twice.Content.ReadAsStringAsync(Ct)).ShouldContain("listed once");
        }

        (await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{id}")).Procedures.Count.ShouldBe(2);

        // A later save replaces the lines.
        await desk.PutAsync($"{PreAuths}/{id}", Request(policyId, saved.Version, [Line(PreAuthProcedureSystem.Icd10Pcs, Cholecystectomy, 2, 40_000m)]));
        (await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{id}")).Procedures.ShouldHaveSingleItem()
            .ShouldBe(saved.Procedures[1] with { Quantity = 2, Amount = 40_000m });

        // Only the insurance desk looks procedures up for a pre-authorisation.
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        using var forbidden = await frontOffice.SendRawAsync(HttpMethod.Get, $"{PreAuths}/procedure-options?system=Hbp&search={hbp}", null);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task What_was_typed_before_procedures_were_coded_is_kept_beside_the_coded_lines()
    {
        var desk = await server.SignInAsync(Users.InsuranceDesk);
        var policyId = await PolicyAsync();
        var id = (await desk.PostAsync<JsonElement>(PreAuths, Request(policyId, null, null))).GetProperty("id").GetGuid();

        // As the migration leaves a pre-authorisation drafted before: its typed procedures in the legacy text.
        const string typed = "Lap chole + on-table cholangiogram if CBD dilated";
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PreAuthorization>().Where(p => p.Id == id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.LegacyProcedureText, typed), Ct);
        }

        var before = await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{id}");
        (before.LegacyProcedureText, before.Procedures.Count).ShouldBe((typed, 0));

        // Coding the procedure keeps the text: it is evidence of what was asked, not replaced.
        await desk.PutAsync($"{PreAuths}/{id}", Request(policyId, before.Version, [Line(PreAuthProcedureSystem.Icd10Pcs, Cholecystectomy, 1, 60_000m)]));
        var after = await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{id}");
        after.LegacyProcedureText.ShouldBe(typed);
        after.Procedures.ShouldHaveSingleItem().Code.ShouldBe(Cholecystectomy);
    }

    private async Task RefusedAsync(ApiClient desk, Guid id, uint version, PreAuthProcedureLine bad, string field, string message)
    {
        var policyId = (await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{id}")).PolicyId;
        using var response = await desk.SendRawAsync(HttpMethod.Put, $"{PreAuths}/{id}",
            Request(policyId, version, [Line(PreAuthProcedureSystem.Icd10Pcs, Cholecystectomy, 1, 0m), bad]));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldContain(field);
        body.ShouldContain(message);
    }

    private static SavePreAuthRequest Request(Guid policyId, uint? version, IReadOnlyList<PreAuthProcedureLine>? procedures) => new(
        policyId, null, null, 60_000m, "K80.20 Calculus of gallbladder", procedures, "Symptomatic cholelithiasis; planned laparoscopic cholecystectomy.", 2, null, version);

    private static PreAuthProcedureLine Line(PreAuthProcedureSystem system, string code, int quantity, decimal amount) => new(system, code, quantity, amount);

    /// <summary>An active and a retired procedure of the HBP master, under a specialty no other test uses.</summary>
    private async Task<(string Active, string Retired)> HbpProceduresAsync()
    {
        var specialty = new string([.. Enumerable.Range(0, 2).Select(_ => (char)('A' + Random.Shared.Next(8)))]);
        var number = Random.Shared.Next(100, 999);
        var (active, retired) = ($"{specialty}{number}A", $"{specialty}{number}B");
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.AddRange(Procedure(active, "Laparoscopic cholecystectomy (test)", true), Procedure(retired, "Open cholecystectomy (test)", false));
        await db.SaveChangesAsync(Ct);
        return (active, retired);

        static HbpProcedure Procedure(string code, string name, bool active) => new()
        {
            Code = code,
            PackageCode = code[..5],
            SpecialtyCode = code[..2],
            ProcedureName = name,
            Rate = 22_800m,
            MasterVersion = "HBP test",
            IsActive = active,
        };
    }

    private async Task<Guid> PolicyAsync()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var billing = await server.SignInAsync(Users.Billing);
        var insurer = (await billing.GetAsync<List<PayerListItem>>("/api/v1/insurance/payers")).Single(p => p.Code == "STAR");
        var patientId = (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: TestPatients.FirstName("Lakshmi"), MiddleName: null, LastName: "Coded" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1975, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false))).Id;
        return (await server.VerifiedPolicyAsync(patientId, insurer.Id)).Id;
    }

    private async Task<AuditEvent> AuditAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.Action == action && a.EntityId == entityId.ToString()).OrderByDescending(a => a.Id).FirstAsync(Ct);
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('a' + Random.Shared.Next(26)))]);
}
