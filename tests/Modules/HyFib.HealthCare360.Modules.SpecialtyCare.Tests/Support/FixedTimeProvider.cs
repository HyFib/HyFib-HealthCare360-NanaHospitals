namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

/// <summary>A clock frozen at a given instant (validators take <see cref="TimeProvider"/>).</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
