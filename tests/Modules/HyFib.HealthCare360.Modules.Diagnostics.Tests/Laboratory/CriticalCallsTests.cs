using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;
using CallMade = HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules.CriticalCallMade;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// L2: a critical value is verified only on a call made for that value. A value phoned to the ward and then changed
/// before verification (entered again, rerun, reopened) needs a new call, even when the new value is not critical: that
/// call corrects what the ward was told. An unchanged value verifies on its call, as before.
/// </summary>
public sealed class CriticalCallsTests
{
    [Fact]
    public void A_critical_value_called_then_corrected_to_one_that_is_not_critical_needs_a_new_call()
    {
        var potassium = Value("Potassium", "7.2", "mmol/L", ResultFlag.CriticalHigh);
        CallMade[] called = [Called(potassium, minutesAgo: 30)];

        // Entered again (a haemolysed sample, rerun): 2.7 is low, and not critical.
        Change(potassium, "2.7", ResultFlag.Low);

        CriticalCalls.NeedsCall(potassium, called).ShouldBeTrue("the ward acted on 7.2 and must be told it was wrong");
        CriticalCalls.NeedsCall(potassium, [.. called, Called(potassium, minutesAgo: 1)]).ShouldBeFalse("the correction was phoned");
    }

    [Fact]
    public void A_critical_value_called_then_corrected_to_another_critical_value_needs_a_new_call()
    {
        var sodium = Value("Sodium", "118", "mmol/L", ResultFlag.CriticalLow);
        CallMade[] called = [Called(sodium, minutesAgo: 20)];

        Change(sodium, "168", ResultFlag.CriticalHigh);

        CriticalCalls.NeedsCall(sodium, called).ShouldBeTrue("the ward was told 118, not 168");
    }

    [Fact]
    public void An_unchanged_critical_value_is_verified_on_its_call()
    {
        var potassium = Value("Potassium", "7.2", "mmol/L", ResultFlag.CriticalHigh);

        CriticalCalls.NeedsCall(potassium, []).ShouldBeTrue("nobody has been told yet");
        CriticalCalls.NeedsCall(potassium, [Called(potassium, minutesAgo: 5)]).ShouldBeFalse();
        CriticalCalls.NeedsCall(potassium, [Called(potassium, minutesAgo: 9), Called(potassium, minutesAgo: 5)])
            .ShouldBeFalse("a second call, to the ward, about the same value");
    }

    [Fact]
    public void What_counts_is_the_last_value_the_ward_was_told()
    {
        var potassium = Value("Potassium", "7.2", "mmol/L", ResultFlag.CriticalHigh);
        var first = Called(potassium, minutesAgo: 40);
        Change(potassium, "2.7", ResultFlag.Low);
        var correction = Called(potassium, minutesAgo: 20);

        // Back to 7.2 (the rerun was the wrong tube): the ward last heard 2.7.
        Change(potassium, "7.2", ResultFlag.CriticalHigh);

        CriticalCalls.NeedsCall(potassium, [first, correction]).ShouldBeTrue();
    }

    [Fact]
    public void A_value_never_critical_and_never_called_needs_no_call() =>
        CriticalCalls.NeedsCall(Value("Potassium", "4.1", "mmol/L", ResultFlag.Normal), []).ShouldBeFalse();

    [Fact]
    public void The_value_decides_not_the_flag_beside_it_and_a_call_made_with_an_amendment_counts_for_its_value()
    {
        var potassium = Value("Potassium", "6.4", "mmol/L", ResultFlag.CriticalHigh);
        var called = Called(potassium, minutesAgo: 10);

        // The laboratory raised the critical limit since: 6.4 is high now, and the ward was told 6.4.
        potassium.Flag = ResultFlag.High;
        CriticalCalls.NeedsCall(potassium, [called]).ShouldBeFalse();

        var amended = new CallMade($"{CriticalCalls.Finding(potassium)} (amended)", Now.AddMinutes(-2), Now.AddMinutes(-2));
        CriticalCalls.NeedsCall(potassium, [amended]).ShouldBeFalse();
    }

    [Fact]
    public void A_call_records_the_test_the_value_and_its_flag() =>
        CriticalCalls.Finding(Value("Potassium", "7.2", "mmol/L", ResultFlag.CriticalHigh)).ShouldBe("Potassium 7.2 mmol/L (CriticalHigh)");

    private static LabResult Value(string test, string value, string unit, ResultFlag flag)
    {
        var result = Result(LabResultStatus.Entered, flag);
        result.TestName = test;
        result.Unit = unit;
        result.ValueText = value;
        return result;
    }

    private static void Change(LabResult result, string value, ResultFlag flag)
    {
        result.ValueText = value;
        result.Flag = flag;
    }

    private static CallMade Called(LabResult result, int minutesAgo) =>
        new(CriticalCalls.Finding(result), Now.AddMinutes(-minutesAgo), Now.AddMinutes(-minutesAgo));
}
