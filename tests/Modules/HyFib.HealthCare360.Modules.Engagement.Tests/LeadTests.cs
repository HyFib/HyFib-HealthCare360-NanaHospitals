using HyFib.HealthCare360.Modules.Engagement.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

public sealed class LeadTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);

    private static Lead NewLead(LeadStatus status = LeadStatus.New) => new() { LeadNumber = "NHLD2600001", Name = "Ravi Kumar", Status = status };

    [Fact]
    public void The_first_contact_moves_a_new_lead_to_contacted_and_schedules_the_next_call()
    {
        var lead = NewLead();

        lead.RecordContact(Now, Now.AddDays(2));

        lead.Status.ShouldBe(LeadStatus.Contacted);
        lead.LastContactedAt.ShouldBe(Now);
        lead.NextFollowUpAt.ShouldBe(Now.AddDays(2));
    }

    [Fact]
    public void Contacting_a_qualified_lead_keeps_its_stage()
    {
        var lead = NewLead(LeadStatus.Qualified);

        lead.RecordContact(Now, null);

        lead.Status.ShouldBe(LeadStatus.Qualified);
        lead.NextFollowUpAt.ShouldBeNull();
    }

    [Fact]
    public void Losing_a_lead_needs_a_reason_and_clears_the_next_call()
    {
        var lead = NewLead(LeadStatus.Contacted);
        lead.NextFollowUpAt = Now.AddDays(1);

        lead.MoveTo(LeadStatus.Lost, "  ").Error!.Fields!.ShouldContainKey("lostReason");
        lead.MoveTo(LeadStatus.Lost, " Chose another hospital ").IsSuccess.ShouldBeTrue();

        lead.LostReason.ShouldBe("Chose another hospital");
        lead.NextFollowUpAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(LeadStatus.New, true)]
    [InlineData(LeadStatus.Contacted, true)]
    [InlineData(LeadStatus.Qualified, false)]
    public void A_lost_lead_can_only_be_reopened_at_the_start_of_the_funnel(LeadStatus target, bool allowed) =>
        LeadWorkflow.CanMove(LeadStatus.Lost, target, null).IsSuccess.ShouldBe(allowed);

    [Fact]
    public void Conversion_happens_only_through_convert()
    {
        LeadWorkflow.CanMove(LeadStatus.Qualified, LeadStatus.Converted, null).Error!.Code.ShouldBe("lead.convert_explicitly");
        LeadWorkflow.CanMove(LeadStatus.Converted, LeadStatus.Contacted, null).Error!.Code.ShouldBe("lead.converted");
        LeadWorkflow.CanMove(LeadStatus.New, LeadStatus.New, null).Error!.Code.ShouldBe("lead.same_status");
    }

    [Fact]
    public void Converting_links_the_patient_and_appointment_once()
    {
        var lead = NewLead(LeadStatus.Qualified);
        lead.LostReason = "stale";
        lead.NextFollowUpAt = Now;
        var patientId = Guid.NewGuid();
        var appointmentId = Guid.NewGuid();

        lead.Convert(patientId, appointmentId, Now).IsSuccess.ShouldBeTrue();

        lead.Status.ShouldBe(LeadStatus.Converted);
        lead.ConvertedPatientId.ShouldBe(patientId);
        lead.ConvertedAppointmentId.ShouldBe(appointmentId);
        lead.ConvertedAt.ShouldBe(Now);
        lead.NextFollowUpAt.ShouldBeNull();
        lead.LostReason.ShouldBeNull();
        lead.Convert(patientId, null, Now).Error!.Code.ShouldBe("lead.converted");
    }

    [Fact]
    public void Converting_needs_a_patient_or_an_appointment() =>
        NewLead().Convert(null, null, Now).IsFailure.ShouldBeTrue();

    [Fact]
    public void A_lost_lead_can_still_convert_when_the_person_comes_in()
    {
        var lead = NewLead(LeadStatus.Lost);

        lead.Convert(Guid.NewGuid(), null, Now).IsSuccess.ShouldBeTrue();
        lead.IsOpen.ShouldBeFalse();
    }
}
