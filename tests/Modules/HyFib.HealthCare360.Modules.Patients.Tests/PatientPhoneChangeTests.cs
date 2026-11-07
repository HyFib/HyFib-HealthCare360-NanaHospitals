using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// The rules of a change of a patient's registered mobile number (<see cref="PatientPhoneChange"/>): five tries at a code,
/// ten minutes, used once, a newer code supersedes a waiting one, and three codes to a number in fifteen minutes.
/// </summary>
public sealed class PatientPhoneChangeTests
{
    private static readonly DateTimeOffset Sent = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Staff = Guid.CreateVersion7();

    [Fact]
    public void The_right_code_confirms_the_change_once_and_says_who_entered_it()
    {
        var change = Change("482913");

        change.Confirm(PatientPhoneChange.HashOf(change.Id, " 482913 "), Staff, Sent.AddMinutes(3)).IsSuccess.ShouldBeTrue();

        (change.Status, change.ConfirmedAt, change.ConfirmedBy, change.Attempts).ShouldBe((PatientPhoneChangeStatus.Confirmed, Sent.AddMinutes(3), Staff, 1));
        var again = change.Confirm(PatientPhoneChange.HashOf(change.Id, "482913"), Staff, Sent.AddMinutes(4));
        again.Error!.Code.ShouldBe(PatientPhoneChange.ExpiredCode);
        change.Attempts.ShouldBe(1, "a used code is not tried again");
    }

    [Fact]
    public void A_wrong_code_counts_and_says_how_many_tries_are_left()
    {
        var change = Change("482913");

        var wrong = change.Confirm(PatientPhoneChange.HashOf(change.Id, "000000"), Staff, Sent.AddMinutes(1));

        wrong.Error!.Code.ShouldBe(PatientPhoneChange.WrongCode);
        wrong.Error.Type.ShouldBe(ErrorType.Validation);
        wrong.Error.Fields!["code"].ShouldBe(["The code is incorrect. 4 tries left."]);
        Value(wrong.Error, "attemptsLeft").ShouldBe(4);
        (change.Attempts, change.AttemptsLeft, change.Status, change.ConfirmedAt).ShouldBe((1, 4, PatientPhoneChangeStatus.Pending, (DateTimeOffset?)null));
    }

    [Fact]
    public void The_fifth_wrong_code_ends_the_change_and_the_right_one_is_refused_after_it()
    {
        var change = Change("482913");
        for (var i = 1; i < PatientPhoneChange.MaxAttempts; i++)
        {
            change.Confirm(PatientPhoneChange.HashOf(change.Id, "000000"), Staff, Sent.AddMinutes(1)).Error!.Code.ShouldBe(PatientPhoneChange.WrongCode);
        }

        var last = change.Confirm(PatientPhoneChange.HashOf(change.Id, "000000"), Staff, Sent.AddMinutes(2));

        last.Error!.Fields!["code"].ShouldBe(["The code is incorrect, and that was the last try. Send a new code."]);
        Value(last.Error, "attemptsLeft").ShouldBe(0);
        change.Status.ShouldBe(PatientPhoneChangeStatus.Expired);
        change.Confirm(PatientPhoneChange.HashOf(change.Id, "482913"), Staff, Sent.AddMinutes(3)).Error!.Code.ShouldBe(PatientPhoneChange.ExpiredCode);
        change.Attempts.ShouldBe(PatientPhoneChange.MaxAttempts);
    }

    [Fact]
    public void A_code_works_for_ten_minutes()
    {
        var change = Change("482913");

        change.IsOpen(Sent + PatientPhoneChange.Lifetime - TimeSpan.FromSeconds(1)).ShouldBeTrue();
        change.IsOpen(Sent + PatientPhoneChange.Lifetime).ShouldBeFalse();
        var late = change.Confirm(PatientPhoneChange.HashOf(change.Id, "482913"), Staff, Sent + PatientPhoneChange.Lifetime);

        late.Error!.Message.ShouldContain("expired");
        (change.Status, change.Attempts).ShouldBe((PatientPhoneChangeStatus.Expired, 0));
    }

    [Fact]
    public void A_newer_code_supersedes_a_waiting_change_and_leaves_a_finished_one_as_it_was()
    {
        var waiting = Change("482913");
        var done = Change("482913");
        done.Confirm(PatientPhoneChange.HashOf(done.Id, "482913"), Staff, Sent.AddMinutes(1));

        waiting.Supersede();
        done.Supersede();

        waiting.Status.ShouldBe(PatientPhoneChangeStatus.Superseded);
        waiting.WhyClosed(Sent.AddMinutes(1))!.Message.ShouldContain("newer code");
        done.Status.ShouldBe(PatientPhoneChangeStatus.Confirmed);
    }

    [Fact]
    public void A_code_is_salted_with_its_change_so_one_hash_says_nothing_of_another_change()
    {
        var first = Change("482913");
        var second = Change("482913");

        first.CodeHash.ShouldNotBe(second.CodeHash);
        second.Confirm(first.CodeHash, Staff, Sent.AddMinutes(1)).Error!.Code.ShouldBe(PatientPhoneChange.WrongCode);
    }

    [Fact]
    public void A_number_gets_three_codes_in_fifteen_minutes_and_the_refusal_says_when_the_next_may_go()
    {
        var now = Sent.AddMinutes(10);

        PatientPhoneChange.WaitBeforeNext([], now).ShouldBeNull();
        PatientPhoneChange.WaitBeforeNext([Sent.AddMinutes(9), Sent.AddMinutes(1)], now).ShouldBeNull();
        var wait = PatientPhoneChange.WaitBeforeNext([Sent.AddMinutes(9), Sent.AddMinutes(5), Sent.AddMinutes(1)], now);
        wait.ShouldBe(TimeSpan.FromMinutes(6), "the third latest leaves the 15 minutes at 9:16");
        PatientPhoneChange.WaitBeforeNext([Sent.AddMinutes(9), Sent.AddMinutes(5), Sent.AddMinutes(-5)], now).ShouldBeNull();

        var refusal = PatientPhoneChange.Throttled(TimeSpan.FromSeconds(359.2));
        (refusal.Code, refusal.Type).ShouldBe((PatientPhoneChange.ThrottledCode, ErrorType.BusinessRule));
        Value(refusal, "retryAfterSeconds").ShouldBe(360);
        Value(PatientPhoneChange.Throttled(TimeSpan.FromMilliseconds(10)), "retryAfterSeconds").ShouldBe(1);
    }

    [Theory]
    [InlineData("9876543210", true)]
    [InlineData("+91 98765 43210", true)]
    [InlineData("098765 43210", true)]
    [InlineData("020 2612 3456", false)]
    [InlineData("+44 7700 900123", false)]
    [InlineData("", false)]
    public void Only_an_indian_mobile_number_is_sent_a_code(string phone, bool valid)
    {
        var result = new RequestPhoneChangeRequestValidator().Validate(new RequestPhoneChangeRequest(phone));

        result.IsValid.ShouldBe(valid);
        if (!valid)
        {
            result.Errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe(PatientPhoneChange.IndianMobileMessage);
        }
    }

    [Theory]
    [InlineData("482913", true)]
    [InlineData(" 482913 ", true)]
    [InlineData("48291", false)]
    [InlineData("48a913", false)]
    public void A_code_is_six_digits(string code, bool valid) =>
        new ConfirmPhoneChangeRequestValidator().Validate(new ConfirmPhoneChangeRequest(code)).IsValid.ShouldBe(valid);

    /// <summary>A value the refusal carries for the app (<see cref="Error.Data"/>), as the problem's <c>data</c> has it.</summary>
    private static object? Value(Error error, string name) => error.Data!.GetType().GetProperty(name)!.GetValue(error.Data);

    private static PatientPhoneChange Change(string code)
    {
        var change = new PatientPhoneChange
        {
            PatientId = Guid.CreateVersion7(),
            Phone = "+919900011122",
            PreviousPhone = "+919876543210",
            ExpiresAt = Sent + PatientPhoneChange.Lifetime,
            Status = PatientPhoneChangeStatus.Pending,
        };
        change.CodeHash = PatientPhoneChange.HashOf(change.Id, code);
        return change;
    }
}
