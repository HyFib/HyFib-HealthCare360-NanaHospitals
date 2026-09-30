using System.Security.Claims;
using HyFib.HealthCare360.Platform.Realtime;
using HyFib.HealthCare360.Platform.Security;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class RealtimeTopicAuthorizerTests
{
    private static readonly Guid UserId = Guid.Parse("0f3e2d1c-0000-4000-8000-000000000001");
    private static readonly Guid FacilityId = Guid.Parse("0f3e2d1c-0000-4000-8000-00000000000a");
    private static readonly Guid OtherFacilityId = Guid.Parse("0f3e2d1c-0000-4000-8000-00000000000b");
    private static readonly Guid PatientId = Guid.Parse("0f3e2d1c-0000-4000-8000-0000000000aa");
    private static readonly Guid WardId = Guid.Parse("0f3e2d1c-0000-4000-8000-0000000000bb");

    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RealtimeTopicAuthorizer Authorizer(params RealtimeTopicPolicy[] policies) =>
        new(policies, _permissions, Substitute.For<IServiceProvider>());

    private static ClaimsPrincipal Staff(Guid? facilityId = null, bool restricted = false)
    {
        List<Claim> claims = [new(HcClaims.UserId, UserId.ToString()), new(HcClaims.UserType, HcClaims.StaffType)];
        if (facilityId is { } facility)
        {
            claims.Add(new Claim(HcClaims.Facility, facility.ToString()));
        }

        if (restricted)
        {
            claims.Add(new Claim(HcClaims.Restriction, "mfa-enrolment"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static ClaimsPrincipal Portal(Guid patientId) => new(new ClaimsIdentity(
        [new(HcClaims.UserId, UserId.ToString()), new(HcClaims.UserType, HcClaims.PatientType), new(HcClaims.Patient, patientId.ToString())], "test"));

    private void Grant(params string[] permissions)
    {
        _permissions.HasPermissionAsync(UserId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(permissions.Contains(call.ArgAt<string>(1))));
    }

    private static RealtimeTopicPolicy LabPolicy => new("lab", RealtimeTopicScope.Facility, ["lab.orders.read"]);

    [Fact]
    public void A_staff_session_joins_its_facility_events_on_connect() =>
        RealtimeTopicAuthorizer.FacilityToJoin(Staff(FacilityId)).ShouldBe(FacilityId);

    [Fact]
    public void A_restricted_staff_session_joins_no_facility_events() =>
        RealtimeTopicAuthorizer.FacilityToJoin(Staff(FacilityId, restricted: true)).ShouldBeNull();

    [Fact]
    public void A_patient_joins_no_facility_events_even_with_a_facility_claim()
    {
        var patient = Portal(PatientId);
        ((ClaimsIdentity)patient.Identity!).AddClaim(new Claim(HcClaims.Facility, FacilityId.ToString()));

        RealtimeTopicAuthorizer.FacilityToJoin(patient).ShouldBeNull();
    }

    [Fact]
    public async Task Staff_follow_their_own_facility_topic_with_the_permission()
    {
        Grant("lab.orders.read");

        var topic = await Authorizer(LabPolicy).AuthorizeAsync(Staff(FacilityId), $"lab:{FacilityId}", Ct);

        topic.ShouldBe($"lab:{FacilityId}");
    }

    [Fact]
    public async Task Another_facilitys_topic_is_refused()
    {
        Grant("lab.orders.read");

        (await Authorizer(LabPolicy).AuthorizeAsync(Staff(FacilityId), $"lab:{OtherFacilityId}", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Staff_without_the_permission_are_refused()
    {
        Grant("billing.charges.read");

        (await Authorizer(LabPolicy).AuthorizeAsync(Staff(FacilityId), $"lab:{FacilityId}", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Staff_without_a_facility_or_with_a_restricted_token_are_refused()
    {
        Grant("lab.orders.read");
        var authorizer = Authorizer(LabPolicy);

        (await authorizer.AuthorizeAsync(Staff(), $"lab:{FacilityId}", Ct)).ShouldBeNull();
        (await authorizer.AuthorizeAsync(Staff(FacilityId, restricted: true), $"lab:{FacilityId}", Ct)).ShouldBeNull();
    }

    [Theory]
    [InlineData("user:0f3e2d1c-0000-4000-8000-000000000001")]
    [InlineData("unknown:0f3e2d1c-0000-4000-8000-00000000000a")]
    [InlineData("lab:not-a-guid")]
    [InlineData("lab")]
    [InlineData("")]
    public async Task Unknown_or_malformed_topics_are_refused(string topic)
    {
        Grant("lab.orders.read");

        (await Authorizer(LabPolicy).AuthorizeAsync(Staff(FacilityId), topic, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Topics_are_joined_in_canonical_form()
    {
        Grant("lab.orders.read");

        var topic = await Authorizer(LabPolicy).AuthorizeAsync(Staff(FacilityId), $"lab:{FacilityId.ToString().ToUpperInvariant()}", Ct);

        topic.ShouldBe($"lab:{FacilityId}");
    }

    [Fact]
    public async Task Record_topics_ask_the_module_about_the_record()
    {
        Grant("inpatient.admissions.read");
        var policy = new RealtimeTopicPolicy(
            "ward", RealtimeTopicScope.Record, ["inpatient.admissions.read"],
            (_, subscriber, id, _) => Task.FromResult(id == WardId && subscriber.FacilityId == FacilityId));
        var authorizer = Authorizer(policy);

        (await authorizer.AuthorizeAsync(Staff(FacilityId), $"ward:{WardId}", Ct)).ShouldBe($"ward:{WardId}");
        (await authorizer.AuthorizeAsync(Staff(OtherFacilityId), $"ward:{WardId}", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Portal_users_follow_only_topics_that_admit_them()
    {
        var own = new RealtimeTopicPolicy(
            "patient", RealtimeTopicScope.Record, ["patients.read"],
            (_, subscriber, id, _) => Task.FromResult(subscriber.IsPortalUser && subscriber.PatientId == id), AllowPortal: true);
        var authorizer = Authorizer(own, LabPolicy);

        (await authorizer.AuthorizeAsync(Portal(PatientId), $"patient:{PatientId}", Ct)).ShouldBe($"patient:{PatientId}");
        (await authorizer.AuthorizeAsync(Portal(PatientId), $"patient:{Guid.NewGuid()}", Ct)).ShouldBeNull();
        (await authorizer.AuthorizeAsync(Portal(PatientId), $"lab:{FacilityId}", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Anonymous_callers_are_refused() =>
        (await Authorizer(LabPolicy).AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), $"lab:{FacilityId}", Ct)).ShouldBeNull();

    [Fact]
    public void A_prefix_registered_twice_is_a_startup_error() =>
        Should.Throw<InvalidOperationException>(() => Authorizer(LabPolicy, LabPolicy));
}
