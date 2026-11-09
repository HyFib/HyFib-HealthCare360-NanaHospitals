using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Lowering a transfusion reaction's severity or imputability, which can take it out of haemovigilance reporting, waits
/// for the blood bank medical officer (maker-checker, slice 15), over the real API: raising either, or a first assessment,
/// is made at once; lowering is asked for with a reason (202), the classification in force stands, the reaction cannot be
/// closed the lowered way meanwhile, and only someone who holds <c>bloodbank.reactions.approve</c>, never the officer who
/// asked, approves it. With <c>bloodbank.reactionDowngradeApproval</c> switched off by two administrators, the officer
/// lowers it alone, audited.
/// </summary>
public sealed class TransfusionReactionLowerTests(HospitalServer server)
{
    private const string Bank = "/api/v1/blood-bank";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lowering_the_severity_waits_for_the_medical_officer_while_raising_it_is_made_at_once()
    {
        var (officer, second) = (await OfficerAsync(), await OfficerAsync());
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var reaction = await ReportedReactionAsync(officer, ReactionSeverity.Moderate);

        // Raised, with a first assessment of imputability: made at once.
        await ClassifyAsync(officer, reaction, ReactionType.FebrileNonHaemolytic, ReactionSeverity.Severe, ReactionImputability.Probable, null, HttpStatusCode.NoContent);
        reaction = await ReactionAsync(officer, reaction.Id);
        (reaction.Severity, reaction.Imputability, reaction.Status).ShouldBe((ReactionSeverity.Severe, ReactionImputability.Probable, ReactionStatus.UnderInvestigation));

        // Lowered: a reason first, then it waits; the classification in force stands.
        (await RefusalAsync(officer, HttpMethod.Put, Classify(reaction.Id),
            new ClassifyReactionRequest(ReactionType.FebrileNonHaemolytic, ReactionSeverity.Mild, ReactionImputability.Probable, null, reaction.Version)))
            .ShouldBe((HttpStatusCode.BadRequest, "validation"));
        var asked = await ClassifyAsync(officer, reaction, ReactionType.FebrileNonHaemolytic, ReactionSeverity.Mild, ReactionImputability.Unlikely,
            "Bag culture sterile, fever settled with paracetamol, no haemolysis", HttpStatusCode.Accepted);
        asked.ShouldNotBeNull().ShouldSatisfyAllConditions(
            a => a.Kind.ShouldBe("bloodbank.reaction_lower"),
            a => a.Details.Single(d => d.Label == "Lowered").Value.ShouldBe("severity Severe → Mild; imputability Probable → Unlikely"),
            a => a.Details.Single(d => d.Label == "Haemovigilance").Value.ShouldStartWith("No longer reported"));
        reaction = await ReactionAsync(officer, reaction.Id);
        (reaction.Severity, reaction.Imputability, reaction.LowerPendingApprovalId).ShouldBe((ReactionSeverity.Severe, ReactionImputability.Probable, asked.Id));

        // Nor is it closed the lowered way meanwhile.
        (await RefusalAsync(officer, HttpMethod.Post, $"{Bank}/reactions/{reaction.Id}/close",
            new CloseReactionRequest(ReactionType.FebrileNonHaemolytic, ReactionSeverity.Mild, "Febrile reaction, settled", true, ReactionImputability.Unlikely)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.reaction_lowered"));

        // Another blood bank officer is not the medical officer; the officer who asked never approves it.
        (await RefusalAsync(second, HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));
        var approved = await MakerChecker.ApproveAsync(admin, asked);
        approved.Outcome.ShouldNotBeNull().ShouldContain("lowered");

        reaction = await ReactionAsync(officer, reaction.Id);
        reaction.ShouldSatisfyAllConditions(
            r => (r.Severity, r.Imputability).ShouldBe((ReactionSeverity.Mild, ReactionImputability.Unlikely)),
            r => r.LoweredByName.ShouldBe(approved.DecidedByName),
            r => r.LowerRequestedByName.ShouldBe(asked.RequestedByName),
            r => r.LowerPendingApprovalId.ShouldBeNull());

        // Closed as approved.
        using var closed = await officer.SendRawAsync(HttpMethod.Post, $"{Bank}/reactions/{reaction.Id}/close",
            new CloseReactionRequest(ReactionType.FebrileNonHaemolytic, ReactionSeverity.Mild, "Febrile reaction, settled", false, ReactionImputability.Unlikely));
        closed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await closed.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task With_the_medical_officer_switched_off_by_two_administrators_the_officer_lowers_it_alone()
    {
        var officer = await OfficerAsync();
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId ?? throw new InvalidOperationException("No facility.");
        var reaction = await ReportedReactionAsync(officer, ReactionSeverity.Severe);
        await server.SaveSettingAsync(admin, ReactionClassificationService.DowngradeApprovalSetting, new SaveSettingRequest(JsonValue.Create(false), facilityId, false, null));
        try
        {
            await ClassifyAsync(officer, reaction, ReactionType.AllergicMinor, ReactionSeverity.Mild, ReactionImputability.Possible, "Urticaria only, settled", HttpStatusCode.NoContent);
            reaction = await ReactionAsync(officer, reaction.Id);
            (reaction.Severity, reaction.LowerReason, reaction.LowerRequestedByName).ShouldBe((ReactionSeverity.Mild, "Urticaria only, settled", null));
            reaction.LoweredByName.ShouldNotBeNull();
        }
        finally
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(ReactionClassificationService.DowngradeApprovalSetting, facilityId, CancellationToken.None);
        }
    }

    private static string Classify(Guid id) => $"{Bank}/reactions/{id}/classification";

    private async Task<ApiClient> OfficerAsync()
    {
        var userName = "it-bbmo-" + string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26))));
        await server.CreateStaffAccountAsync(userName, SystemRoles.BloodBankOfficer);
        return await server.SignInAsync(userName);
    }

    /// <summary>A unit issued to a newly admitted patient, and a reaction to it reported by the ward.</summary>
    private async Task<TransfusionReactionDto> ReportedReactionAsync(ApiClient officer, ReactionSeverity severity)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var admission = await BloodBedsideCheckTests.AdmitAsync(server, frontOffice, doctor, patient.Id);
        var issue = await BloodBedsideCheckTests.IssueUnitAsync(server, officer, doctor, patient.Id, admission.Id, BloodGroup.OPositive, emergency: false);
        var reported = await nurse.PostAsync<Created>($"{Bank}/issues/{issue.Id}/reactions", new ReportReactionRequest(
            DateTimeOffset.UtcNow, [ReactionSymptom.Fever, ReactionSymptom.ChillsRigors], "T 39.1, P 118", true, 120, "Transfusion stopped, paracetamol", severity));
        return await ReactionAsync(officer, reported.Id);
    }

    private static async Task<TransfusionReactionDto> ReactionAsync(ApiClient api, Guid id) =>
        (await api.GetAsync<PagedResult<TransfusionReactionDto>>($"{Bank}/reactions?pageSize=200")).Items.Single(r => r.Id == id);

    private static async Task<ApprovalRequestDto?> ClassifyAsync(
        ApiClient officer, TransfusionReactionDto reaction, ReactionType type, ReactionSeverity severity, ReactionImputability imputability, string? reason,
        HttpStatusCode expected)
    {
        using var response = await officer.SendRawAsync(HttpMethod.Put, Classify(reaction.Id), new ClassifyReactionRequest(type, severity, imputability, reason, reaction.Version));
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        return expected == HttpStatusCode.Accepted ? await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Put, Classify(reaction.Id)) : null;
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, HttpMethod method, string path, object body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return (response.StatusCode, problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private sealed record Created(Guid Id);
}
