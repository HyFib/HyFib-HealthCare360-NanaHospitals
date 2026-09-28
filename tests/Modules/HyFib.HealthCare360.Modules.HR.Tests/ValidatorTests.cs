using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Attendance;
using HyFib.HealthCare360.Modules.HR.Features.Employees;
using HyFib.HealthCare360.Modules.HR.Features.Leave;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;
using HyFib.HealthCare360.Modules.HR.Features.Rostering;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class ValidatorTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly TimeProvider Time = new FixedTime(new DateTimeOffset(2026, 9, 25, 6, 0, 0, TimeSpan.Zero));

    private static SaveEmployeeRequest Employee(
        string? pan = null,
        DateOnly? dateOfBirth = null,
        EmployeeStatus status = EmployeeStatus.Active,
        BankDetailsInput? bank = null) => new(
        "Ms.", "Deepa", null, "Raman", Gender.Female, dateOfBirth ?? new DateOnly(1995, 6, 1), "9876543210", "deepa@example.com", null,
        null, null, null, null, EmploymentType.Permanent, status, new DateOnly(2026, 9, 1), null, null, null, null, "1001", null,
        Weekday.Sunday, false, SalaryPaymentMode.BankTransfer, pan, "100123456789", null, "3112345678", null, null, bank, null);

    [Fact]
    public void A_complete_employee_is_valid()
    {
        new SaveEmployeeRequestValidator(Time).Validate(Employee(pan: "ABCDE1234F", bank: new BankDetailsInput("SBI", "001234567890", "SBIN0001234")))
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Rejects_a_malformed_pan()
    {
        new SaveEmployeeRequestValidator(Time).Validate(Employee(pan: "ABCDE12345")).Errors.ShouldContain(e => e.PropertyName == "Pan");
    }

    [Fact]
    public void Employees_must_be_adults_on_joining()
    {
        new SaveEmployeeRequestValidator(Time).Validate(Employee(dateOfBirth: new DateOnly(2010, 1, 1))).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Separation_is_not_done_through_the_edit_form()
    {
        new SaveEmployeeRequestValidator(Time).Validate(Employee(status: EmployeeStatus.Separated)).Errors.ShouldContain(e => e.PropertyName == "Status");
    }

    [Fact]
    public void Bank_details_are_validated_when_supplied()
    {
        var result = new SaveEmployeeRequestValidator(Time).Validate(Employee(bank: new BankDetailsInput(null, "1234", "SBIN1234567")));

        result.Errors.ShouldContain(e => e.PropertyName == "Bank.AccountNumber");
        result.Errors.ShouldContain(e => e.PropertyName == "Bank.Ifsc");
    }

    [Fact]
    public void A_one_day_leave_cannot_run_from_the_second_half_to_the_first()
    {
        var day = new DateOnly(2026, 10, 5);

        new ApplyLeaveRequestValidator().Validate(new ApplyLeaveRequest(Guid.NewGuid(), day, LeaveSession.SecondHalf, day, LeaveSession.FirstHalf, "Personal", null))
            .IsValid.ShouldBeFalse();
        new ApplyLeaveRequestValidator().Validate(new ApplyLeaveRequest(Guid.NewGuid(), day, LeaveSession.SecondHalf, day, LeaveSession.SecondHalf, "Personal", null))
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_roster_cell_is_either_a_shift_or_a_week_off()
    {
        var validator = new SaveRosterRequestValidator();
        var day = new DateOnly(2026, 10, 5);

        validator.Validate(new SaveRosterRequest([new RosterAssignmentInput(Guid.NewGuid(), day, Guid.NewGuid(), false, null)])).IsValid.ShouldBeTrue();
        validator.Validate(new SaveRosterRequest([new RosterAssignmentInput(Guid.NewGuid(), day, Guid.NewGuid(), true, null)])).IsValid.ShouldBeFalse();
        validator.Validate(new SaveRosterRequest([new RosterAssignmentInput(Guid.NewGuid(), day, null, false, null)])).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Regularisations_are_for_recent_past_days_and_need_times_for_missed_punches()
    {
        var validator = new RequestRegularisationRequestValidator(Time);

        validator.Validate(new RequestRegularisationRequest(new DateOnly(2026, 9, 24), RegularisationKind.MissedPunch, new TimeOnly(9, 0), null, "Terminal offline")).IsValid.ShouldBeTrue();
        validator.Validate(new RequestRegularisationRequest(new DateOnly(2026, 9, 26), RegularisationKind.OnDuty, null, null, "Camp")).IsValid.ShouldBeFalse();
        validator.Validate(new RequestRegularisationRequest(new DateOnly(2026, 6, 1), RegularisationKind.OnDuty, null, null, "Camp")).IsValid.ShouldBeFalse();
        validator.Validate(new RequestRegularisationRequest(new DateOnly(2026, 9, 24), RegularisationKind.MissedPunch, null, null, "Forgot")).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_shift_cannot_be_all_break()
    {
        var validator = new SaveShiftRequestValidator();

        validator.Validate(new SaveShiftRequest("GEN", "General", new TimeOnly(9, 0), new TimeOnly(17, 30), 30, 10, false, null, null, 30, "#1A73E8", true, null))
            .IsValid.ShouldBeTrue();
        validator.Validate(new SaveShiftRequest("SHORT", "Short", new TimeOnly(9, 0), new TimeOnly(10, 0), 60, 10, false, null, null, 30, null, true, null))
            .IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Professional_tax_tables_are_checked_before_saving()
    {
        var validator = new SaveProfessionalTaxRequestValidator();

        validator.Validate(new SaveProfessionalTaxRequest("Karnataka", ProfessionalTaxFrequency.Monthly, [], [new(0, 24_999, 0, null), new(25_000, null, 200, 300)]))
            .IsValid.ShouldBeTrue();
        validator.Validate(new SaveProfessionalTaxRequest("Karnataka", ProfessionalTaxFrequency.Monthly, [], [new(25_000, null, 200, null)]))
            .IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Statutory_heads_cannot_be_created_as_ordinary_components()
    {
        var validator = new SaveSalaryComponentRequestValidator();

        validator.Validate(new SaveSalaryComponentRequest("PF", "Provident fund", SalaryComponentKind.Deduction, SalaryCalculationType.Fixed, 0, false, 1, true, null))
            .IsValid.ShouldBeFalse();
        validator.Validate(new SaveSalaryComponentRequest("HRA", "HRA", SalaryComponentKind.Earning, SalaryCalculationType.PercentOfBasic, 140, true, 1, true, null))
            .IsValid.ShouldBeFalse();
    }
}
