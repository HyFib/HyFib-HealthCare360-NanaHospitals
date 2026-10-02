using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.OnlineBooking;

namespace HyFib.HealthCare360.Modules.Scheduling.Tests;

/// <summary>While video consultations are switched off, the public booking directory offers patients no video at all.</summary>
public sealed class PublicDirectoryTests
{
    [Theory]
    [InlineData(ConsultationMode.InPerson, ConsultationMode.InPerson)]
    [InlineData(ConsultationMode.Telehealth, ConsultationMode.Telehealth)]
    [InlineData(ConsultationMode.Both, ConsultationMode.Both)]
    public void With_video_on_sessions_are_offered_as_scheduled(ConsultationMode scheduled, ConsultationMode offered) =>
        PublicDoctorEndpoints.Offered(scheduled, video: true).ShouldBe(offered);

    [Fact]
    public void With_video_off_a_session_for_both_is_offered_in_person_only() =>
        PublicDoctorEndpoints.Offered(ConsultationMode.Both, video: false).ShouldBe(ConsultationMode.InPerson);

    [Fact]
    public void With_video_off_an_in_person_session_is_unchanged() =>
        PublicDoctorEndpoints.Offered(ConsultationMode.InPerson, video: false).ShouldBe(ConsultationMode.InPerson);
}
