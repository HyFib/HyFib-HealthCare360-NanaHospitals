using System.Collections.Concurrent;
using System.Reflection;
using Xunit.v3;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Notes each test while it runs and, once a minute, writes the ones that have run for more than three minutes to the
/// test process's output, which the runner prints even when it stops a run that has hung. The journeys run one after
/// another, so a test that never finishes holds up every test after it; this names it.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class LongRunningTestWatchAttribute : BeforeAfterTestAttribute
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);
    private static readonly ConcurrentDictionary<string, (string Name, DateTimeOffset Started)> Running = new();
    private static readonly Timer Watch = new(_ => Report(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        GC.KeepAlive(Watch);
        Running[test.UniqueID] = (test.TestDisplayName, DateTimeOffset.UtcNow);
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        Running.TryRemove(test.UniqueID, out _);
    }

    private static void Report()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (name, started) in Running.Values)
        {
            if (now - started > Limit)
            {
                var minutes = (now - started).TotalMinutes;
                Console.Out.WriteLine($"[{now:HH:mm:ss}] Still running after {minutes:0} minutes: {name}");
            }
        }
    }
}
