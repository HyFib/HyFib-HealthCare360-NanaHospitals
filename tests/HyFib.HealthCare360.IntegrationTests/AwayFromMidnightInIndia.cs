using System.Reflection;
using HyFib.HealthCare360.SharedKernel;
using Xunit.v3;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Starts no test on the real clock in the minutes either side of midnight in India. The hospital keeps its day by India
/// Standard Time (<see cref="HospitalTimeZone"/>): a patient's cash in a day, a night's room rent, today's QC, the patients in
/// the clinic today. A test that reads "today" while the day turns, or dates a thing a little before now, compares one day
/// with the next and fails though nothing is wrong. Every test passes through here, so none needs a guard of its own: one
/// that would start less than <see cref="BeforeMidnight"/> before midnight, or less than <see cref="AfterMidnight"/> after
/// it, waits until <see cref="AfterMidnight"/> past midnight. A run that crosses midnight waits once, for less than the two
/// together. The tests of the day's turn itself keep the hospital's time on a clock of their own
/// (<see cref="OnTestClockAttribute"/>, <see cref="MidnightInIndiaTests"/>): they cross midnight on purpose, whatever the
/// real hour, and never wait.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class AwayFromMidnightInIndiaAttribute : BeforeAfterTestAttribute
{
    /// <summary>
    /// Longer than a test takes, so that one started before it ends before midnight: the slowest takes two and a half
    /// minutes, and <see cref="LongRunningTestWatchAttribute"/> names one still running after three.
    /// </summary>
    internal static readonly TimeSpan BeforeMidnight = TimeSpan.FromMinutes(5);

    /// <summary>Longer than any test dates a thing before now and expects it on the same day.</summary>
    internal static readonly TimeSpan AfterMidnight = TimeSpan.FromMinutes(2);

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (OnTestClock(methodUnderTest.ReflectedType))
        {
            return;
        }

        var now = TimeProvider.System.GetUtcNow();
        var wait = WaitAt(now);
        if (wait > TimeSpan.Zero)
        {
            Console.Out.WriteLine($"[{now:HH:mm:ss}] Midnight in India: waiting {wait.TotalSeconds:0} s before {test.TestDisplayName}");
            Thread.Sleep(wait);
        }
    }

    /// <summary>How long a test about to start at <paramref name="now"/> waits: until <see cref="AfterMidnight"/> past midnight, or not at all.</summary>
    internal static TimeSpan WaitAt(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, HospitalTimeZone.Zone);
        var midnight = new DateTimeOffset(local.Date, local.Offset);
        var sinceMidnight = local - midnight;
        if (sinceMidnight < AfterMidnight)
        {
            return AfterMidnight - sinceMidnight;
        }

        var untilMidnight = midnight.AddDays(1) - local;
        return untilMidnight < BeforeMidnight ? untilMidnight + AfterMidnight : TimeSpan.Zero;
    }

    /// <summary>Whether the tests of <paramref name="testClass"/> keep the hospital's time on a clock of their own, not the real one.</summary>
    internal static bool OnTestClock(Type? testClass) => testClass?.IsDefined(typeof(OnTestClockAttribute), inherit: false) == true;
}
