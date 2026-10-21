using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The feature switches over the real API: an administrator turns video consultations or the patient portal off on the
/// Features screen, the server refuses them at once with 503 <c>feature.disabled</c>, and they work again when turned back
/// on. The switches are hospital-wide and the server and database are shared by the whole run, so these tests are in a
/// collection of their own that xUnit runs by itself after every other test (<see cref="FeatureSwitchCollection"/>), and
/// each one turns its switch back on in a <c>finally</c> block, even when it fails.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class FeatureSwitchTests(HospitalServer server)
{
    [Fact]
    public async Task The_server_reports_its_switches_before_anyone_signs_in()
    {
        var info = await server.Anonymous().GetAsync<JsonElement>("/api/v1/system/info");
        info.GetProperty("features").GetProperty("patientPortal").GetBoolean().ShouldBeTrue("Every feature is on until an administrator turns it off.");
        info.GetProperty("features").GetProperty("telehealth").GetBoolean().ShouldBeTrue();

        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        try
        {
            await SwitchAsync(FeatureKeys.Telehealth, enabled: false);

            var off = await server.Anonymous().GetAsync<JsonElement>("/api/v1/system/info");
            off.GetProperty("features").GetProperty("telehealth").GetBoolean().ShouldBeFalse();
            off.GetProperty("features").GetProperty("patientPortal").GetBoolean().ShouldBeTrue();
            (await admin.GetAsync<JsonElement>("/api/v1/settings/public")).GetProperty(FeatureKeys.Telehealth).GetBoolean().ShouldBeFalse();
        }
        finally
        {
            await SwitchAsync(FeatureKeys.Telehealth, enabled: true);
        }
    }

    [Fact]
    public async Task Video_consultations_are_refused_while_switched_off_and_work_again_when_switched_on()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var patient = await RegisterPatientAsync(frontOffice);
        const string Sessions = "/api/v1/telehealth/sessions?mine=true";

        (await doctor.GetAsync<JsonElement>(Sessions)).ValueKind.ShouldBe(JsonValueKind.Array);
        try
        {
            await SwitchAsync(FeatureKeys.Telehealth, enabled: false);

            using (var list = await doctor.SendRawAsync(HttpMethod.Get, Sessions))
            {
                list.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
                var problem = await ProblemAsync(list);
                problem.GetProperty("code").GetString().ShouldBe(FeatureProblems.Code);
                problem.GetProperty("title").GetString().ShouldBe("Video consultations are turned off");
            }

            // Nothing new is booked as a video consultation.
            using (var booking = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/scheduling/appointments", new BookAppointmentRequest(
                       patient.Id, practitionerId, DateTimeOffset.UtcNow.AddDays(1), AppointmentChannel.Phone, AppointmentVisitType.New,
                       IsTelehealth: true, "Follow-up over video", Notes: null)))
            {
                booking.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                (await ProblemAsync(booking)).GetProperty("code").GetString().ShouldBe(FeatureProblems.Code);
            }

            await SwitchAsync(FeatureKeys.Telehealth, enabled: true);

            (await doctor.GetAsync<JsonElement>(Sessions)).ValueKind.ShouldBe(JsonValueKind.Array);
        }
        finally
        {
            await SwitchAsync(FeatureKeys.Telehealth, enabled: true);
        }
    }

    [Fact]
    public async Task While_video_consultations_are_off_nobody_joins_one_on_the_hub()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var patient = await RegisterPatientAsync(frontOffice);
        var slot = await TeleconsultationJourneyTests.FreeVideoSlotAsync(frontOffice, practitionerId);
        var appointment = await frontOffice.PostAsync<AppointmentDetail>("/api/v1/scheduling/appointments", new BookAppointmentRequest(
            patient.Id, practitionerId, slot.StartsAt, AppointmentChannel.Phone, AppointmentVisitType.New, IsTelehealth: true,
            "Review over video", Notes: null));
        var day = DateOnly.FromDateTime(slot.StartsAt.ToOffset(TimeSpan.FromMinutes(330)).DateTime);
        var sessionId = (await doctor.GetAsync<List<TelehealthSessionListItem>>($"/api/v1/telehealth/sessions?date={day:O}&practitionerId={practitionerId}"))
            .Single(s => s.AppointmentId == appointment.Id).Id;
        try
        {
            await SwitchAsync(FeatureKeys.Telehealth, enabled: false);

            // The hub itself has no switch: guests and patients connect without a facility, so joining checks the
            // consultation's facility instead, and nothing works in a consultation without joining it.
            await using var hub = await LongPollingHub.ConnectAsync(doctor, "/hubs/telehealth");
            (await hub.InvokeAsync("JoinAsClinician", sessionId)).ShouldNotBeNull().ShouldContain("Video consultations are turned off");
            (await hub.InvokeAsync("SendChat", sessionId, "Can you hear me?")).ShouldNotBeNull();
        }
        finally
        {
            await SwitchAsync(FeatureKeys.Telehealth, enabled: true);
        }
    }

    [Fact]
    public async Task Nobody_signs_in_to_the_portal_while_it_is_switched_off()
    {
        var signedIn = await server.SignInToPortalAsync(NewMobile());
        (await signedIn.GetAsync<JsonElement>("/api/v1/portal/profiles")).ValueKind.ShouldBe(JsonValueKind.Array);
        var visitor = server.AnonymousOnPortal(ownAddress: true);
        try
        {
            await SwitchAsync(FeatureKeys.PatientPortal, enabled: false);

            using (var code = await visitor.SendRawAsync(HttpMethod.Post, "/api/v1/auth/otp/request", new OtpRequest(NewMobile())))
            {
                code.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
                var problem = await ProblemAsync(code);
                problem.GetProperty("code").GetString().ShouldBe(FeatureProblems.Code);
                problem.GetProperty("detail").GetString().ShouldBe("The patient portal is not available right now. Please contact the hospital.");
            }

            using (var directory = await visitor.SendRawAsync(HttpMethod.Get, "/api/v1/public/doctors/"))
            {
                directory.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, "Booking online needs the portal.");
            }

            // A patient who was already signed in can no longer use the portal, nor anything else with that sign-in.
            using (var profiles = await signedIn.SendRawAsync(HttpMethod.Get, "/api/v1/portal/profiles"))
            {
                profiles.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            }

            using (var me = await server.OnStaffAddress(signedIn).SendRawAsync(HttpMethod.Get, "/api/v1/auth/me"))
            {
                me.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            }

            // What does not need the portal keeps working.
            using (var notice = await visitor.SendRawAsync(HttpMethod.Get, "/api/v1/public/privacy"))
            {
                notice.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            await SwitchAsync(FeatureKeys.PatientPortal, enabled: true);

            using var codeAgain = await visitor.SendRawAsync(HttpMethod.Post, "/api/v1/auth/otp/request", new OtpRequest(NewMobile()));
            codeAgain.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await SwitchAsync(FeatureKeys.PatientPortal, enabled: true);
        }
    }

    [Fact]
    public async Task Administrators_see_every_switch_and_each_change_is_audited()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        var features = await admin.GetAsync<List<FeatureDto>>("/api/v1/admin/features");

        features.Select(f => f.Key).ShouldBe([FeatureKeys.PatientPortal, FeatureKeys.Telehealth, FeatureKeys.Abdm]);
        features.ShouldAllBe(f => f.Name.Length > 0 && f.WhenOff.Length > 0);
        features.Where(f => f.Key != FeatureKeys.Abdm).ShouldAllBe(f => f.Enabled && f.DefaultEnabled);

        // ABDM health records are new and send records outside the hospital: off until an administrator turns them on.
        features.Single(f => f.Key == FeatureKeys.Abdm).ShouldSatisfyAllConditions(
            f => f.Enabled.ShouldBeFalse("ABDM health records are off until an administrator turns them on"),
            f => f.DefaultEnabled.ShouldBeFalse());
        try
        {
            var off = await admin.PutAsync<FeatureDto>($"/api/v1/admin/features/{FeatureKeys.Telehealth}", new SetFeatureRequest(false));
            off.Enabled.ShouldBeFalse();
            (await admin.GetAsync<List<FeatureDto>>("/api/v1/admin/features")).Single(f => f.Key == FeatureKeys.Telehealth).Enabled.ShouldBeFalse();

            var audited = await LastChangeAsync(FeatureKeys.Telehealth);
            audited.ShouldNotBeNull().Summary.ShouldBe("Video consultations: turned off");
            audited.Category.ShouldBe(AuditCategories.Admin);
            audited.Severity.ShouldBe(AuditSeverity.Warning);

            using var nobody = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/admin/features/{FeatureKeys.Telehealth}", new { });
            nobody.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "A request that does not say on or off changes nothing.");

            var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
            using var denied = await doctor.SendRawAsync(HttpMethod.Put, $"/api/v1/admin/features/{FeatureKeys.Telehealth}", new SetFeatureRequest(true));
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            await SwitchAsync(FeatureKeys.Telehealth, enabled: true);
        }
    }

    /// <summary>Turns a feature on or off hospital-wide, as the Features screen does.</summary>
    private async Task SwitchAsync(string feature, bool enabled)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        (await admin.PutAsync<FeatureDto>($"/api/v1/admin/features/{feature}", new SetFeatureRequest(enabled))).Enabled.ShouldBe(enabled);
    }

    private async Task<AuditEvent?> LastChangeAsync(string feature)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await db.Set<AuditEvent>().AsNoTracking()
            .Where(a => a.Action == "feature.changed" && a.EntityId == feature)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/problem+json");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, TestContext.Current.CancellationToken);
    }

    private static Task<PatientCreated> RegisterPatientAsync(ApiClient frontOffice) =>
        frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Arjun"), MiddleName: null, LastName: "Switch" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1975, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: NewMobile(), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));

    private static string NewMobile() => "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}

/// <summary>
/// Tests that turn a hospital-wide switch off (or, for ABDM health records, on). xUnit runs a collection with
/// parallelization disabled by itself, after all the others, so no other test can meet a switch these tests changed.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FeatureSwitchCollection
{
    public const string Name = "Feature switches";
}
