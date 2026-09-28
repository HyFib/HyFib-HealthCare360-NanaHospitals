using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

public sealed class SentinelRulesTests
{
    [Theory]
    [InlineData(IncidentSeverity.Death, true, "Death")]
    [InlineData(IncidentSeverity.Severe, true, "Severe harm")]
    [InlineData(IncidentSeverity.Moderate, false, null)]
    [InlineData(IncidentSeverity.Mild, false, null)]
    [InlineData(IncidentSeverity.NoHarm, false, null)]
    [InlineData(IncidentSeverity.NearMiss, false, null)]
    public void Death_and_severe_harm_are_sentinel(IncidentSeverity severity, bool expected, string? reason)
    {
        SentinelRules.IsSentinel(severity, null, false).ShouldBe(expected);
        SentinelRules.Reason(severity, null, false).ShouldBe(reason);
    }

    [Fact]
    public void Never_events_are_sentinel_even_without_harm()
    {
        SentinelRules.IsSentinel(IncidentSeverity.NoHarm, SentinelEventKind.WrongSiteSurgery, false).ShouldBeTrue();
        SentinelRules.Reason(IncidentSeverity.NoHarm, SentinelEventKind.WrongSiteSurgery, false).ShouldBe("Never event: WrongSiteSurgery");
    }

    [Fact]
    public void Anyone_can_flag_an_event_as_sentinel()
    {
        SentinelRules.IsSentinel(IncidentSeverity.Mild, null, true).ShouldBeTrue();
        SentinelRules.Reason(IncidentSeverity.Mild, null, true).ShouldBe("Flagged as sentinel");
    }

    [Fact]
    public void Regrading_reapplies_the_rules()
    {
        var incident = new Incident();

        incident.Classify(IncidentType.PatientFall, IncidentSeverity.Severe, null, false);
        incident.IsSentinel.ShouldBeTrue();

        incident.Classify(IncidentType.PatientFall, IncidentSeverity.Mild, null, false);
        incident.IsSentinel.ShouldBeFalse();
        incident.SentinelReason.ShouldBeNull();

        incident.Classify(IncidentType.MedicationError, IncidentSeverity.Mild, null, true);
        incident.IsSentinel.ShouldBeTrue();
    }

    [Fact]
    public void The_reported_event_carries_no_reporter_identity()
    {
        var incident = new Incident { IncidentNumber = "NHIR2600001", ReporterUserId = Guid.NewGuid(), ReporterName = "Staff Nurse" };
        incident.Classify(IncidentType.NeedleStick, IncidentSeverity.Mild, null, false);

        incident.MarkReported();

        var reported = incident.DomainEvents.OfType<IncidentReported>().ShouldHaveSingleItem();
        reported.IncidentNumber.ShouldBe("NHIR2600001");
        typeof(IncidentReported).GetProperties().Select(p => p.Name).ShouldNotContain(name => name.Contains("Reporter", StringComparison.Ordinal));
    }
}
