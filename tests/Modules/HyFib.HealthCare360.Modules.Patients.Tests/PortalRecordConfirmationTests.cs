using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// "Confirm your details" in the portal (<see cref="PortalRecordConfirmation"/>): which details match a record waiting on
/// the account's number, and the limit on wrong answers, five a day per number, so dates of birth cannot be guessed.
/// </summary>
public sealed class PortalRecordConfirmationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Born = new(1979, 6, 23);
    private const string Uhid = "NHT-2026-000123";

    [Fact]
    public void A_date_of_birth_matches_the_record_that_has_it_as_the_patient_gave_it()
    {
        PortalRecordConfirmation.Matches(Uhid, Born, dateOfBirthEstimated: false, Born, null).ShouldBeTrue();
        PortalRecordConfirmation.Matches(Uhid, Born, dateOfBirthEstimated: false, Born.AddDays(1), null).ShouldBeFalse();
        PortalRecordConfirmation.Matches(Uhid, null, dateOfBirthEstimated: false, Born, null).ShouldBeFalse("the record has no date of birth");
    }

    [Fact]
    public void A_date_of_birth_worked_out_from_an_age_matches_nothing()
    {
        // The desk's estimate from "about 45": not something the patient knows, so not proof that they hold the number.
        PortalRecordConfirmation.Matches(Uhid, Born, dateOfBirthEstimated: true, Born, null).ShouldBeFalse();
        PortalRecordConfirmation.Matches(Uhid, Born, dateOfBirthEstimated: true, null, Uhid).ShouldBeTrue("the UHID works instead");
    }

    [Fact]
    public void A_uhid_matches_as_printed_in_any_case_and_with_spaces_around_it()
    {
        PortalRecordConfirmation.Matches(Uhid, Born, false, null, Uhid).ShouldBeTrue();
        PortalRecordConfirmation.Matches(Uhid, Born, false, null, " nht-2026-000123 ").ShouldBeTrue();
        PortalRecordConfirmation.Matches(Uhid, Born, false, null, "NHT-2026-000124").ShouldBeFalse();
    }

    [Fact]
    public void Given_both_a_record_must_match_both_and_given_neither_none_matches()
    {
        PortalRecordConfirmation.Matches(Uhid, Born, false, Born, Uhid).ShouldBeTrue();
        PortalRecordConfirmation.Matches(Uhid, Born, false, Born.AddYears(1), Uhid).ShouldBeFalse();
        PortalRecordConfirmation.Matches(Uhid, Born, false, Born, "NHT-2026-000124").ShouldBeFalse();
        PortalRecordConfirmation.Matches(Uhid, Born, false, null, "  ").ShouldBeFalse();
        PortalRecordConfirmation.Matches(Uhid, Born, false, null, null).ShouldBeFalse();
    }

    [Fact]
    public void Five_wrong_answers_in_a_day_stop_the_number_until_the_oldest_of_them_is_a_day_old()
    {
        PortalRecordConfirmation.WaitBeforeNext([], Now).ShouldBeNull();
        PortalRecordConfirmation.WaitBeforeNext(Failures(4), Now).ShouldBeNull("four wrong answers leave one more try");
        PortalRecordConfirmation.AttemptsLeft(4).ShouldBe(1);

        var five = Failures(5);
        var wait = PortalRecordConfirmation.WaitBeforeNext(five, Now).ShouldNotBeNull();
        wait.ShouldBe(five[4] + PortalRecordConfirmation.Window - Now);
        PortalRecordConfirmation.AttemptsLeft(5).ShouldBe(0);
        PortalRecordConfirmation.AttemptsLeft(9).ShouldBe(0);

        // A day after the oldest of the five, the number may try once more.
        PortalRecordConfirmation.WaitBeforeNext(five, five[4] + PortalRecordConfirmation.Window).ShouldBeNull();
    }

    [Fact]
    public void A_refusal_says_how_many_tries_are_left_or_how_long_to_wait()
    {
        var wrong = PortalRecordConfirmation.NoMatch(3);
        (wrong.Code, wrong.Type).ShouldBe((PortalRecordConfirmation.NoMatchCode, ErrorType.BusinessRule));
        Value(wrong, "attemptsLeft").ShouldBe(3);

        var throttled = PortalRecordConfirmation.Throttled(TimeSpan.FromSeconds(3599.2));
        (throttled.Code, throttled.Type).ShouldBe((PortalRecordConfirmation.ThrottledCode, ErrorType.BusinessRule));
        Value(throttled, "retryAfterSeconds").ShouldBe(3600);
        Value(throttled, "attemptsLeft").ShouldBe(0);
        Value(PortalRecordConfirmation.Throttled(TimeSpan.FromMilliseconds(10)), "retryAfterSeconds").ShouldBe(1);
    }

    /// <summary>Wrong answers <paramref name="count"/> times, an hour apart, newest first: the first an hour ago.</summary>
    private static List<DateTimeOffset> Failures(int count) => [.. Enumerable.Range(1, count).Select(i => Now.AddHours(-i))];

    private static int Value(Error error, string name) =>
        (int)error.Data!.GetType().GetProperty(name)!.GetValue(error.Data)!;
}
