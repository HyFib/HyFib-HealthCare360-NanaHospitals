namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The tests that cross midnight in India (<see cref="MidnightInIndiaTests"/>), on a server of their own whose clock they
/// set. xUnit runs a collection with parallelization disabled by itself, after all the others, so that server never runs
/// beside the shared server's tests, and none of them waits for it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MidnightInIndiaCollection : ICollectionFixture<MidnightInIndiaServer>
{
    public const string Name = "Midnight in India";
}

/// <summary>
/// A hospital server on a database of its own and on a clock the tests set (<see cref="TestClock"/>), seeded as the shared
/// server is. The clock starts at 09:00 on Monday 21 December 2026 in India and only goes forward. Each test takes the next
/// evening it needs (<see cref="NextEvening"/>): the test of the year's turn takes New Year's Eve, which no other test takes,
/// and the others an ordinary day each, so the tests may run in any order and none waits for another's day.
/// </summary>
public sealed class MidnightInIndiaServer : IAsyncLifetime
{
    /// <summary>India Standard Time: UTC+05:30 all year, with no daylight saving.</summary>
    public static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    /// <summary>When a test's evening begins: an hour before midnight, so a test's sign-ins (two hours) outlast its midnight.</summary>
    public static readonly TimeOnly Evening = new(23, 0);

    public MidnightInIndiaServer()
    {
        Clock = new TestClock(new DateTimeOffset(2026, 12, 21, 9, 0, 0, Ist));
        Server = new HospitalServer(Clock);
    }

    public TestClock Clock { get; }

    public HospitalServer Server { get; }

    public ValueTask InitializeAsync() => Server.InitializeAsync();

    public ValueTask DisposeAsync() => Server.DisposeAsync();

    /// <summary>
    /// Moves the clock to <see cref="Evening"/> on the first day from now that <paramref name="suits"/>, and has each account's
    /// next sign-in start afresh: the sessions of a day gone by have ended. That day.
    /// </summary>
    public DateOnly NextEvening(Func<DateOnly, bool> suits)
    {
        ArgumentNullException.ThrowIfNull(suits);
        var now = Clock.GetUtcNow().ToOffset(Ist);
        var day = DateOnly.FromDateTime(now.DateTime);
        if (TimeOnly.FromDateTime(now.DateTime) > Evening)
        {
            day = day.AddDays(1);
        }

        while (!suits(day))
        {
            day = day.AddDays(1);
        }

        At(day, Evening);
        Server.ForgetSignIns();
        return day;
    }

    /// <summary>Moves the clock to <paramref name="time"/> on <paramref name="day"/> in India.</summary>
    public void At(DateOnly day, TimeOnly time) => Clock.MoveTo(new DateTimeOffset(day.ToDateTime(time), Ist));
}
