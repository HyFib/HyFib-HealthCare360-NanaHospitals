using System.Net;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Break-the-glass access to a restricted record (SRS EHR-07). It needs a written reason from a role allowed to ask; it
/// opens that one record to the user who asked, for 30 minutes; and it leaves a critical security entry in the audit
/// trail, with the reason and the patient, of which the audit readers are told at once.
/// </summary>
public sealed class BreakGlassTests(HospitalServer server)
{
    private const string Reason = "Unconscious in casualty, needs the allergy history";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Nothing_is_opened_or_recorded_without_a_reason()
    {
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await RegisterRestrictedPatientAsync();

        // No reason, an empty or a blank one, or one shorter than the 15 characters asked for.
        foreach (var reason in new[] { null, "", "               ", "Needs history." })
        {
            using var refused = await nurse.SendRawAsync(HttpMethod.Post, BreakGlass(patient), new BreakGlassRequest(reason!));
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"reason: \"{reason}\"");
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("\"reason\"");
        }

        // The front office may not ask at all, whatever the reason.
        using (var notAllowed = await frontOffice.SendRawAsync(HttpMethod.Post, BreakGlass(patient), new BreakGlassRequest(Reason)))
        {
            notAllowed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var closed = await nurse.SendRawAsync(HttpMethod.Get, $"/api/v1/patients/{patient.Id}"))
        {
            closed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await closed.Content.ReadAsStringAsync(Ct)).ShouldContain("patient.restricted");
        }

        (await BreakGlassEntriesAsync(patient)).ShouldBeEmpty("Nothing was opened, so there is nothing to review.");
    }

    [Fact]
    public async Task The_access_is_a_critical_security_event_with_the_reason_and_audit_readers_are_told()
    {
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var patient = await RegisterRestrictedPatientAsync();

        var asked = DateTimeOffset.UtcNow;
        var grant = await nurse.PostAsync<BreakGlassGrant>(BreakGlass(patient), new BreakGlassRequest($"  {Reason} "));
        var answered = DateTimeOffset.UtcNow;

        grant.PatientId.ShouldBe(patient.Id);
        grant.ExpiresAt.ShouldBeInRange(asked.AddMinutes(30), answered.AddMinutes(30));
        (await nurse.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).Uhid.ShouldBe(patient.Uhid);

        var entry = (await BreakGlassEntriesAsync(patient)).ShouldHaveSingleItem();
        (entry.Category, entry.Severity, entry.Summary).ShouldBe((AuditCategories.Security, AuditSeverity.Critical, Reason));
        (entry.PatientId, entry.EntityType, entry.EntityId).ShouldBe((patient.Id, "Patient", patient.Id.ToString()));
        entry.UserName.ShouldBe(HospitalServer.Users.Nurse);

        // The hospital administrator reads the audit trail, and is told at once, with the link to this patient's entries.
        var auditor = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var alert = (await auditor.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=50")).Items
            .Where(n => n.Link == $"/admin/audit?patientId={patient.Id}").ShouldHaveSingleItem();
        (alert.Title, alert.Category, alert.Severity).ShouldBe(("Emergency access to a restricted record", "security", "critical"));
        alert.Body.ShouldNotBeNull().ShouldContain(patient.Uhid);
        alert.Body.ShouldContain(Reason);
    }

    [Fact]
    public async Task The_access_opens_that_record_to_the_user_who_asked_and_to_nobody_else()
    {
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await RegisterRestrictedPatientAsync();
        var another = await RegisterRestrictedPatientAsync();

        await nurse.PostAsync<BreakGlassGrant>(BreakGlass(patient), new BreakGlassRequest(Reason));

        (await nurse.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).Id.ShouldBe(patient.Id);
        foreach (var (client, record, who) in new[] { (nurse, another, "the nurse, another restricted record"), (doctor, patient, "another user, the same record") })
        {
            using var refused = await client.SendRawAsync(HttpMethod.Get, $"/api/v1/patients/{record.Id}");
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, who);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("patient.restricted");
        }
    }

    [Fact]
    public async Task The_access_ends_thirty_minutes_after_it_was_given()
    {
        var patient = await RegisterRestrictedPatientAsync();
        var nurse = await (await server.SignInAsync(HospitalServer.Users.Nurse)).GetAsync<AuthUser>("/api/v1/auth/me");
        var clock = new MovableClock(DateTimeOffset.UtcNow);

        // The grant is a row (patients.break_glass_grants, SCAL-01) that ends at the time the guard's clock gave it; here the
        // clock is the test's.
        await using var scope = server.Services.CreateAsyncScope();
        var guard = new PatientAccessGuard(
            scope.ServiceProvider.GetRequiredService<HospitalDbContext>(),
            SignedInAs(nurse),
            scope.ServiceProvider.GetRequiredService<IPermissionService>(),
            Substitute.For<IAuditLogger>(),
            Substitute.For<INotificationService>(),
            clock);

        var before = await guard.EnsureCanOpenAsync(patient.Id, Ct);
        var grant = (await guard.BreakGlassAsync(patient.Id, Reason, Ct)).Value;
        var given = clock.Now;
        clock.Now = given.AddMinutes(30).AddSeconds(-1);
        var lastSecond = await guard.EnsureCanOpenAsync(patient.Id, Ct);
        clock.Now = given.AddMinutes(30);
        var after = await guard.EnsureCanOpenAsync(patient.Id, Ct);

        before.Error.ShouldNotBeNull().Code.ShouldBe("patient.restricted");
        grant.ExpiresAt.ShouldBe(given.AddMinutes(30));
        lastSecond.IsSuccess.ShouldBeTrue("The record is open until the thirty minutes are up.");
        after.Error.ShouldNotBeNull().Code.ShouldBe("patient.restricted", "The record is closed again after thirty minutes.");
    }

    private static string BreakGlass(PatientCreated patient) => $"/api/v1/patients/{patient.Id}/break-glass";

    /// <summary>The break-the-glass entries of the patient's audit trail, as an audit reader sees them.</summary>
    private async Task<IReadOnlyList<AuditEventDto>> BreakGlassEntriesAsync(PatientCreated patient)
    {
        var auditor = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        return (await auditor.GetAsync<PagedResult<AuditEventDto>>($"/api/v1/admin/audit?patientId={patient.Id}&action=patient.break_glass")).Items;
    }

    private async Task<PatientCreated> RegisterRestrictedPatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: "Meenakshi", MiddleName: null, LastName: "Restricted" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(9000)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: true, Notes: null,
            ConfirmedNotDuplicate: true));
    }

    private static ICurrentUser SignedInAs(AuthUser user)
    {
        var current = Substitute.For<ICurrentUser>();
        current.IsAuthenticated.Returns(true);
        current.UserId.Returns(user.Id);
        current.UserName.Returns(user.UserName);
        current.DisplayName.Returns(user.DisplayName);
        current.FacilityId.Returns(user.FacilityId);
        current.Roles.Returns(user.Roles);
        return current;
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
