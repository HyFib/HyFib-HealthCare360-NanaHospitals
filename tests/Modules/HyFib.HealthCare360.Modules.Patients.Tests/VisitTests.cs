using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

public sealed class VisitTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 10, 0, 0, TimeSpan.FromHours(5.5));

    private static Visit NewVisit(Guid? appointmentId = null) => new()
    {
        FacilityId = Guid.NewGuid(),
        PatientId = Guid.NewGuid(),
        PractitionerId = Guid.NewGuid(),
        AppointmentId = appointmentId,
        Type = VisitType.OutPatient,
        VisitedAt = Now,
    };

    [Fact]
    public void Starting_a_visit_raises_VisitStarted_once_so_the_appointment_follows()
    {
        var appointmentId = Guid.NewGuid();
        var visit = NewVisit(appointmentId);

        visit.Start();
        visit.Start();

        visit.Status.ShouldBe(VisitStatus.InProgress);
        var started = visit.DomainEvents.OfType<VisitStarted>().ShouldHaveSingleItem();
        started.VisitId.ShouldBe(visit.Id);
        started.AppointmentId.ShouldBe(appointmentId);
        started.PractitionerId.ShouldBe(visit.PractitionerId);
    }

    [Fact]
    public void A_closed_visit_does_not_start_again()
    {
        var visit = NewVisit();
        visit.Complete(Now).IsSuccess.ShouldBeTrue();

        visit.Start();

        visit.Status.ShouldBe(VisitStatus.Completed);
        visit.DomainEvents.OfType<VisitStarted>().ShouldBeEmpty();
    }

    [Fact]
    public void Cancelling_a_closed_visit_is_refused()
    {
        var visit = NewVisit();
        visit.Cancel("Registered in error", Now).IsSuccess.ShouldBeTrue();

        visit.Cancel("Again", Now).Error!.Code.ShouldBe("visit.closed");
        visit.DomainEvents.OfType<VisitCancelled>().ShouldHaveSingleItem();
    }
}
