using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Capas;
using HyFib.HealthCare360.Modules.Quality.Features.Incidents;
using HyFib.HealthCare360.Modules.Quality.Features.Indicators;
using HyFib.HealthCare360.Modules.Quality.Features.Risks;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

public sealed class ValidatorTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeProvider Time = new FixedTime(Now);

    private static ReportIncidentRequest Report(DateTimeOffset occurredAt, string description) => new(
        IncidentType.PatientFall, IncidentSeverity.Mild, occurredAt, "Ward 3B, bed 12", null, null, null, description, "Patient assessed; no injury",
        null, null, false, IsAnonymous: true);

    [Fact]
    public void A_proper_report_is_valid()
    {
        new ReportIncidentRequestValidator(Time).Validate(Report(Now.AddHours(-2), "Patient slipped near the washroom at 2 am while unattended.")).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Reports_cannot_be_in_the_future_or_too_brief()
    {
        var validator = new ReportIncidentRequestValidator(Time);

        validator.Validate(Report(Now.AddHours(2), "Patient slipped near the washroom at 2 am.")).IsValid.ShouldBeFalse();
        validator.Validate(Report(Now.AddHours(-2), "Fell")).Errors.ShouldContain(e => e.PropertyName == "Description");
    }

    [Fact]
    public void Indicator_values_need_a_positive_denominator()
    {
        var validator = new RecordIndicatorValueRequestValidator();

        validator.Validate(new RecordIndicatorValueRequest(new DateOnly(2026, 8, 1), 3, 2400, null)).IsValid.ShouldBeTrue();
        validator.Validate(new RecordIndicatorValueRequest(new DateOnly(2026, 8, 1), 3, 0, null)).IsValid.ShouldBeFalse();
        validator.Validate(new RecordIndicatorValueRequest(new DateOnly(2026, 8, 1), -1, 10, null)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Risks_are_rated_one_to_five()
    {
        var validator = new SaveRiskRequestValidator();

        validator.Validate(new SaveRiskRequest("Oxygen supply failure", null, RiskCategory.Operational, null, 2, 5, 1, 5, "Backup cylinders", null, null,
            new DateOnly(2026, 12, 31), RiskStatus.Open, null)).IsValid.ShouldBeTrue();
        validator.Validate(new SaveRiskRequest("Oxygen supply failure", null, RiskCategory.Operational, null, 6, 5, null, null, null, null, null,
            new DateOnly(2026, 12, 31), RiskStatus.Open, null)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Capas_from_incidents_name_the_incident()
    {
        var validator = new CreateCapaRequestValidator(Time);

        validator.Validate(new CreateCapaRequest(CapaSource.Incident, null, null, CapaType.Corrective, "Install grab bars", Guid.NewGuid(), new DateOnly(2026, 10, 15)))
            .Errors.ShouldContain(e => e.PropertyName == "IncidentId");
        validator.Validate(new CreateCapaRequest(CapaSource.Audit, null, null, CapaType.Preventive, "Install grab bars", Guid.NewGuid(), new DateOnly(2026, 10, 15)))
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public void An_rca_has_a_problem_statement_and_bounded_lists()
    {
        var validator = new SaveInvestigationRequestValidator();

        validator.Validate(new SaveInvestigationRequest(RcaMethod.FiveWhys, "", null, null, null, null, null)).IsValid.ShouldBeFalse();
        validator.Validate(new SaveInvestigationRequest(RcaMethod.FiveWhys, "Fall at night", Enumerable.Repeat("why", 11).ToList(), null, null, null, null))
            .IsValid.ShouldBeFalse();
    }
}
