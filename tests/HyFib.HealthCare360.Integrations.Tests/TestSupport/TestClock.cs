namespace HyFib.HealthCare360.Integrations.Tests.TestSupport;

/// <summary>A clock tests can set and advance.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
