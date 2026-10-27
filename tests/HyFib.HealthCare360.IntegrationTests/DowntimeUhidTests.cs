using System.Net;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Downtime;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Downtime UHIDs over the real API (NABH IMS continuity): the medical records officer issues a block for a desk's
/// downtime kit; the desk's registrations on paper are entered afterwards under the UHIDs on the forms, with the time on
/// the forms; a UHID is entered once only, a voided one never, and a number HC360 did not issue for a kit not at all;
/// and the UHIDs of a block are never issued to a registration made in HC360.
/// </summary>
public sealed class DowntimeUhidTests(HospitalServer server)
{
    private const string Blocks = "/api/v1/patients/downtime-blocks";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_kit_is_issued_its_paper_registrations_are_entered_once_and_unused_numbers_are_voided()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);

        // Front office registers from the forms but does not issue kits.
        using (var refused = await frontOffice.SendRawAsync(HttpMethod.Post, Blocks, new IssueDowntimeUhidsRequest("Main reception, kit 1", 3)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var kit = await mrd.PostAsync<DowntimeUhidBlockDetail>(Blocks, new IssueDowntimeUhidsRequest("Main reception, kit 1", 3));
        kit.Numbers.Select(n => n.Position).ShouldBe([1, 2, 3]);
        kit.Numbers.ShouldAllBe(n => n.Status == DowntimeUhidStatus.Open);
        (kit.Block.FirstUhid, kit.Block.LastUhid).ShouldBe((kit.Numbers[0].Uhid, kit.Numbers[2].Uhid));
        (await mrd.GetAsync<List<DowntimeUhidBlockSummary>>(Blocks)).ShouldContain(b => b.Id == kit.Block.Id && b.Open == 3);

        // A registration made in HC360 now is not given a kit's UHID.
        var live = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration("Live"));
        kit.Numbers.ShouldNotContain(n => n.Uhid == live.Uhid);

        // The first form is entered under its UHID and keeps the time written on it (the kit was issued just before).
        var writtenAt = DateTimeOffset.UtcNow;
        var first = kit.Numbers[0].Uhid;
        var entered = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients",
            Registration("Paper") with { Paper = new PaperRegistration(first.ToLowerInvariant(), writtenAt) });
        entered.Uhid.ShouldBe(first);
        var patient = await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{entered.Id}");
        patient.RegisteredAt.ShouldBe(writtenAt, TimeSpan.FromSeconds(1));

        // The same UHID cannot be entered again, nor a number HC360 did not issue for a kit.
        await RefusedAsync(frontOffice, first, HttpStatusCode.Conflict, "already been entered");
        await RefusedAsync(frontOffice, live.Uhid, HttpStatusCode.BadRequest, "is not a downtime UHID");

        // A spoilt label is voided, and its UHID can no longer be entered; an entered one cannot be voided.
        var spoilt = kit.Numbers[1];
        await mrd.PostAsync($"{Blocks}/{kit.Block.Id}/void", new VoidDowntimeUhidRequest(spoilt.Uhid, "Label torn", spoilt.Version));
        await RefusedAsync(frontOffice, spoilt.Uhid, HttpStatusCode.BadRequest, "was voided (Label torn)");
        using (var inUse = await mrd.SendRawAsync(HttpMethod.Post, $"{Blocks}/{kit.Block.Id}/void", new VoidDowntimeUhidRequest(first, "Mistake", 0)))
        {
            inUse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        var after = await mrd.GetAsync<DowntimeUhidBlockDetail>($"{Blocks}/{kit.Block.Id}");
        (after.Block.Entered, after.Block.Voided, after.Block.Open).ShouldBe((1, 1, 1));
        var row = after.Numbers[0];
        (row.Status, row.PatientId, row.PatientName).ShouldBe((DowntimeUhidStatus.Entered, entered.Id, patient.FullName));
        row.PaperRegisteredAt!.Value.ShouldBe(writtenAt, TimeSpan.FromSeconds(1));
        (after.Numbers[1].Status, after.Numbers[1].VoidReason).ShouldBe((DowntimeUhidStatus.Voided, "Label torn"));
    }

    [Fact]
    public async Task A_form_dated_before_its_kit_or_in_the_future_is_refused()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var kit = await mrd.PostAsync<DowntimeUhidBlockDetail>(Blocks, new IssueDowntimeUhidsRequest("Casualty desk", 1));
        var uhid = kit.Numbers[0].Uhid;

        await RefusedAsync(frontOffice, uhid, HttpStatusCode.BadRequest, "dated before its UHID was issued", DateTimeOffset.UtcNow.AddDays(-2));
        await RefusedAsync(frontOffice, uhid, HttpStatusCode.BadRequest, "cannot be in the future", DateTimeOffset.UtcNow.AddHours(2));

        // Still open: a refused entry leaves it for the right form.
        (await mrd.GetAsync<DowntimeUhidBlockDetail>($"{Blocks}/{kit.Block.Id}")).Block.Open.ShouldBe(1);
    }

    private static async Task RefusedAsync(ApiClient client, string uhid, HttpStatusCode status, string reason, DateTimeOffset? at = null)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, "/api/v1/patients",
            Registration("Refused") with { Paper = new PaperRegistration(uhid, at ?? DateTimeOffset.UtcNow) });
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, body);
        body.ShouldContain(reason);
    }

    private static RegisterPatientRequest Registration(string lastName) => new(
        Title: null, FirstName: TestPatients.FirstName("Downtime"), MiddleName: null, LastName: lastName, Gender: Gender.Male,
        DateOfBirth: null, AgeYears: 20 + Random.Shared.Next(60), AgeMonths: null, Phone: null, AlternatePhone: null, Email: null,
        Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
        EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null,
        PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false,
        Notes: null, ConfirmedNotDuplicate: true);
}
