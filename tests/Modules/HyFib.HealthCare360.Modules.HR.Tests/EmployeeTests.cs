using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class EmployeeTests
{
    private static Employee NewEmployee() => new() { EmployeeCode = "NHEMP00007", JoiningDate = new DateOnly(2024, 4, 1) };

    [Fact]
    public void Names_are_trimmed_and_composed()
    {
        var employee = NewEmployee();

        employee.SetName(" Ms. ", " Deepa ", " ", " Raman ");

        employee.Title.ShouldBe("Ms.");
        employee.MiddleName.ShouldBeNull();
        employee.FullName.ShouldBe("Deepa Raman");
    }

    [Fact]
    public void Employment_covers_joining_to_leaving_day_inclusive()
    {
        var employee = NewEmployee();
        employee.LeavingDate = new DateOnly(2026, 9, 30);

        employee.IsEmployedOn(new DateOnly(2024, 3, 31)).ShouldBeFalse();
        employee.IsEmployedOn(new DateOnly(2024, 4, 1)).ShouldBeTrue();
        employee.IsEmployedOn(new DateOnly(2026, 9, 30)).ShouldBeTrue();
        employee.IsEmployedOn(new DateOnly(2026, 10, 1)).ShouldBeFalse();
    }

    [Fact]
    public void Separation_closes_the_record_and_notifies_other_modules()
    {
        var employee = NewEmployee();

        employee.Separate(new DateOnly(2026, 9, 30), SeparationReason.Resignation, " Higher studies ").IsSuccess.ShouldBeTrue();

        employee.Status.ShouldBe(EmployeeStatus.Separated);
        employee.LeavingDate.ShouldBe(new DateOnly(2026, 9, 30));
        employee.SeparationNote.ShouldBe("Higher studies");
        employee.DomainEvents.OfType<EmployeeSeparated>().ShouldHaveSingleItem().Reason.ShouldBe(SeparationReason.Resignation);
        employee.Separate(new DateOnly(2026, 10, 30), SeparationReason.Other, null).Error!.Code.ShouldBe("employee.already_separated");
    }

    [Fact]
    public void Separation_cannot_predate_joining()
    {
        NewEmployee().Separate(new DateOnly(2024, 3, 1), SeparationReason.Termination, null).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Joining_raises_an_event_with_the_code()
    {
        var employee = NewEmployee();

        employee.MarkJoined();

        employee.DomainEvents.OfType<EmployeeJoined>().ShouldHaveSingleItem().EmployeeCode.ShouldBe("NHEMP00007");
    }

    [Fact]
    public void Reporting_loops_are_detected()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var managers = new Dictionary<Guid, Guid?> { [a] = b, [b] = null, [c] = null };
        Guid? ManagerOf(Guid id) => managers.GetValueOrDefault(id);

        ReportingChain.CreatesCycle(b, a, ManagerOf).ShouldBeTrue();
        ReportingChain.CreatesCycle(a, a, ManagerOf).ShouldBeTrue();
        ReportingChain.CreatesCycle(c, a, ManagerOf).ShouldBeFalse();
        ReportingChain.CreatesCycle(c, null, ManagerOf).ShouldBeFalse();
    }

    [Fact]
    public void Implausibly_deep_chains_are_rejected()
    {
        var chain = Enumerable.Range(0, ReportingChain.MaxDepth + 2).Select(_ => Guid.NewGuid()).ToList();
        Guid? ManagerOf(Guid id)
        {
            var index = chain.IndexOf(id);
            return index + 1 < chain.Count ? chain[index + 1] : null;
        }

        ReportingChain.CreatesCycle(Guid.NewGuid(), chain[0], ManagerOf).ShouldBeTrue();
    }

    [Theory]
    [InlineData("ABCDE1234F", true)]
    [InlineData("abcde1234f", true)]
    [InlineData("ABCD1234F", false)]
    [InlineData("12345ABCDE", false)]
    public void Validates_pan(string pan, bool expected) => HrIdentifiers.IsValidPan(pan).ShouldBe(expected);

    [Theory]
    [InlineData("SBIN0001234", true)]
    [InlineData("hdfc0abc123", true)]
    [InlineData("SBIN1001234", false)]
    [InlineData("SBI0001234", false)]
    public void Validates_ifsc(string ifsc, bool expected) => HrIdentifiers.IsValidIfsc(ifsc).ShouldBe(expected);

    [Theory]
    [InlineData("12345678", false)]
    [InlineData("123456789", true)]
    [InlineData("1234 5678 9012", true)]
    [InlineData("1234567890123456789", false)]
    [InlineData("12345678A", false)]
    public void Validates_bank_accounts(string account, bool expected) => HrIdentifiers.IsValidBankAccount(account).ShouldBe(expected);

    [Fact]
    public void Validates_statutory_numbers()
    {
        HrIdentifiers.IsValidUan("100123456789").ShouldBeTrue();
        HrIdentifiers.IsValidUan("10012345678").ShouldBeFalse();
        HrIdentifiers.IsValidEsiNumber("3112345678").ShouldBeTrue();
        HrIdentifiers.IsValidEsiNumber("31123456781234567").ShouldBeTrue();
        HrIdentifiers.IsValidEsiNumber("311234").ShouldBeFalse();
    }

    [Fact]
    public void Masks_identifiers_for_display()
    {
        HrIdentifiers.MaskAccount("001234567890").ShouldBe("XXXXXXXX7890");
        HrIdentifiers.MaskIfsc("SBIN0001234").ShouldBe("SBIN0XXXXXX");
        HrIdentifiers.MaskPan("ABCDE1234F").ShouldBe("XXXXXX234F");
    }

    [Fact]
    public void Night_shifts_end_on_the_next_day()
    {
        var night = new ShiftTimes(new TimeOnly(21, 0), new TimeOnly(7, 0), 30, 10, 240, 510, 30);
        var day = new DateOnly(2026, 9, 21);

        night.CrossesMidnight.ShouldBeTrue();
        night.DurationMinutes.ShouldBe(600);
        night.NetMinutes.ShouldBe(570);
        night.EndOn(day).ShouldBe(new DateTime(2026, 9, 22, 7, 0, 0));
    }

    [Fact]
    public void Equal_start_and_end_is_a_24_hour_duty()
    {
        new ShiftTimes(new TimeOnly(8, 0), new TimeOnly(8, 0), 60, 10, 240, 900, 30).DurationMinutes.ShouldBe(24 * 60);
    }

    [Fact]
    public void A_double_duty_combines_into_one_continuous_shift()
    {
        var morning = new ShiftTimes(new TimeOnly(7, 0), new TimeOnly(14, 0), 15, 10, 180, 360, 30);
        var evening = new ShiftTimes(new TimeOnly(14, 0), new TimeOnly(21, 0), 15, 5, 180, 360, 30);

        var combined = ShiftTimes.Combine([evening, morning]);

        combined.Start.ShouldBe(new TimeOnly(7, 0));
        combined.End.ShouldBe(new TimeOnly(21, 0));
        combined.BreakMinutes.ShouldBe(30);
        combined.GraceMinutes.ShouldBe(10);
        combined.NetMinutes.ShouldBe(810);
    }
}
