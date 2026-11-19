using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

/// <summary>
/// <see cref="MarChartAheadJob"/> keeps the medication chart a day ahead, every quarter of an hour: a course the pharmacy
/// issued at once is charted round by round to its end, and never past it, though the issue charted one day; an order
/// whose doses are held, a stopped one and an e-prescription line the pharmacy has not supplied get no dose from it. The
/// ward is kept in memory here, on a clock the test turns, with the order's own rules (<see cref="MedicationOrder.KeptChartedAhead"/>,
/// <see cref="MedicationOrder.DosesToChart"/>) as <see cref="MarService"/> applies them; its queries run against
/// PostgreSQL end to end (MarChartAheadTests) and are translated in MarQueryTests.
/// </summary>
public sealed class MarChartAheadJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(int day, int hour, int minute = 0) => IndiaClock.AtLocal(new DateOnly(2026, 12, day), new TimeOnly(hour, minute));

    [Fact]
    public async Task A_five_day_course_issued_at_once_is_charted_a_day_ahead_to_its_end()
    {
        // Inj. Ceftriaxone 1 g IV BD for 5 days, signed at 09:50 on the 1st. The pharmacy issues all ten vials at 10:00, which
        // charts the next 24 hours: 21:00 that day and 09:00 the next.
        var clock = new ManualClock(At(1, 10));
        var ward = new Ward(clock);
        var ceftriaxone = ward.Prescribed("Inj. Ceftriaxone", "1 g", "BD", signedAt: At(1, 9, 50), days: 5);
        ward.Issued(ceftriaxone);
        ward.DosesOf(ceftriaxone).ShouldBe([At(1, 21), At(2, 9)]);

        await RunAsync(ward, clock, until: At(8, 10));

        // Every round of days 2 to 5, and 09:00 on day 6, before the course ends at 09:50: the ten vials. Nothing after the end.
        ward.DosesOf(ceftriaxone).ShouldBe([At(1, 21), At(2, 9), At(2, 21), At(3, 9), At(3, 21), At(4, 9), At(4, 21), At(5, 9), At(5, 21), At(6, 9)]);
        ward.DosesOf(ceftriaxone).ShouldAllBe(at => at < ceftriaxone.EndAt!.Value);

        // Each dose the job charted was on the ward's round a day before it was due, less the quarter of an hour between runs:
        // a scheduled dose of the order, due, as the ward's list and the overdue and missed-dose handling expect.
        var byJob = ward.Charted.Where(c => c.Dose.MedicationOrderId == ceftriaxone.Id && c.At > At(1, 10)).ToList();
        byJob.Count.ShouldBe(8);
        byJob.ShouldAllBe(c => c.Dose.ScheduledAt - c.At >= MarRules.ChartAhead - MarChartAheadJob.Interval);
        byJob.ShouldAllBe(c => c.Dose.Status == MarStatus.Due && !c.Dose.IsAdHoc && c.Dose.AdmissionId == ceftriaxone.AdmissionId);
    }

    [Fact]
    public async Task Held_stopped_and_never_supplied_orders_get_no_dose_from_the_job_until_they_may()
    {
        var clock = new ManualClock(At(1, 10));
        var ward = new Ward(clock);

        // An e-prescription line the pharmacy has not supplied: charted only once the medicine is on the ward.
        var waiting = ward.Prescribed("Inj. Pantoprazole", "40 mg", "OD", signedAt: At(1, 9, 50), days: 5);

        // A nurse's stop waiting for the doctor holds every dose; the doctor declines it the next morning.
        var stopAsked = ward.Prescribed("Inj. Ondansetron", "4 mg", "BD", signedAt: At(1, 9, 50), days: 5);
        ward.Issued(stopAsked);
        stopAsked.RequestStop(Guid.NewGuid(), "Sr. Anitha", "Vomiting settled", At(1, 10, 5), At(2, 10, 5)).IsSuccess.ShouldBeTrue();

        // A course the doctor stops.
        var stopped = ward.Prescribed("Tab. Paracetamol", "650 mg", "TDS", signedAt: At(1, 9, 50), days: 5);
        ward.Issued(stopped);
        stopped.Stop(Guid.NewGuid(), "Afebrile", At(1, 10, 5)).IsSuccess.ShouldBeTrue();

        await RunAsync(ward, clock, until: At(2, 10));
        ward.DosesOf(waiting).ShouldBeEmpty();
        ward.DosesOf(stopAsked).ShouldBe([At(1, 21), At(2, 9)], "charted at the issue; nothing more while the stop waits");
        ward.DosesOf(stopped).ShouldBe([At(1, 14), At(1, 20), At(2, 8)], "charted at the issue; nothing after the stop");

        // Supplied at 10:00 on the 2nd, the pantoprazole is charted from the issue and kept ahead; the ondansetron's doses go on
        // from the decline. The last run, at 09:45 on the 3rd, reaches 09:00 on the 4th.
        ward.Issued(waiting);
        stopAsked.DeclineStop().IsSuccess.ShouldBeTrue();
        await RunAsync(ward, clock, until: At(3, 10));
        ward.DosesOf(waiting).ShouldBe([At(3, 9), At(4, 9)]);
        ward.DosesOf(stopAsked).ShouldBe([At(1, 21), At(2, 9), At(2, 21), At(3, 9), At(3, 21), At(4, 9)]);
        ward.DosesOf(stopped).Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_ward_order_is_kept_ahead_from_its_start_but_a_held_one_waits_for_the_doctor()
    {
        var clock = new ManualClock(At(1, 10));
        var ward = new Ward(clock);

        // The doctor's own order, written in his name, and a telephone order of insulin (high-alert) the nurse wrote, which gives
        // no dose until the doctor countersigns it.
        var heparin = ward.Ordered("Inj. Heparin", "5000 IU", "BD");
        var insulin = ward.Ordered("Inj. Insulin regular", "8 IU", "TDS", highAlert: true, mode: MedicationOrderMode.Telephone);
        var sos = ward.Ordered("Tab. Paracetamol", "650 mg", "SOS");

        await RunAsync(ward, clock, until: At(2, 22));
        ward.DosesOf(heparin).ShouldBe([At(1, 21), At(2, 9), At(2, 21), At(3, 9), At(3, 21)]);
        ward.DosesOf(insulin).ShouldBeEmpty();
        ward.DosesOf(sos).ShouldBeEmpty("only when needed: recorded as given, never on the rounds");

        insulin.Countersign(Guid.NewGuid(), "Dr. Rao", clock.GetUtcNow()).IsSuccess.ShouldBeTrue();
        await RunAsync(ward, clock, until: At(2, 22, 30));
        ward.DosesOf(insulin).ShouldBe([At(3, 8), At(3, 14), At(3, 20)]);
    }

    [Fact]
    public async Task An_order_that_cannot_be_charted_does_not_stop_the_others()
    {
        var clock = new ManualClock(At(1, 10));
        var ward = new Ward(clock);
        var first = ward.Ordered("Inj. Heparin", "5000 IU", "BD");
        var second = ward.Ordered("Tab. Amlodipine", "5 mg", "OD");
        clock.Advance(TimeSpan.FromHours(24));
        ward.Failing.Add(first.Id);

        await using var services = Services(ward);
        (await Job(services, clock).RunOnceAsync(Ct)).ShouldBe(1);

        ward.DosesOf(second).ShouldBe([At(2, 9), At(3, 9)]);
        ward.DosesOf(first).ShouldBe([At(1, 21), At(2, 9)], "only what was charted when it was written");
    }

    private static async Task RunAsync(Ward ward, ManualClock clock, DateTimeOffset until)
    {
        await using var services = Services(ward);
        var job = Job(services, clock);
        while (clock.GetUtcNow() < until)
        {
            await job.RunOnceAsync(Ct);
            clock.Advance(MarChartAheadJob.Interval);
        }
    }

    private static ServiceProvider Services(Ward ward) => new ServiceCollection().AddScoped<IMarChartAhead>(_ => ward).BuildServiceProvider();

    private static MarChartAheadJob Job(ServiceProvider services, TimeProvider clock) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new PlatformOptions()), clock, NullLogger<MarChartAheadJob>.Instance);

    /// <summary>A clock the test turns.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// One patient's chart in memory, kept as <see cref="MarService"/> keeps it: an issue charts the next day from the issue
    /// (ChartDispenseAsync), a ward order from its start (AddOrderAsync), and the job tops up what its rules keep ahead.
    /// </summary>
    private sealed class Ward(TimeProvider time) : IMarChartAhead
    {
        private readonly Guid _admissionId = Guid.CreateVersion7();
        private readonly List<MedicationOrder> _orders = [];

        /// <summary>Each dose with the time it was charted.</summary>
        public List<(MedicationAdministration Dose, DateTimeOffset At)> Charted { get; } = [];

        /// <summary>Orders whose charting fails, as one would with the database refusing it.</summary>
        public HashSet<Guid> Failing { get; } = [];

        public MedicationOrder Prescribed(string drug, string dose, string frequency, DateTimeOffset signedAt, int days) =>
            Add(new MedicationOrder
            {
                PrescriptionId = Guid.CreateVersion7(),
                PrescriptionLineId = Guid.CreateVersion7(),
                DrugName = drug,
                Dose = dose,
                Frequency = frequency,
                DurationDays = days,
                StartAt = signedAt,
                EndAt = signedAt.AddDays(days),
                Source = MedicationOrderSource.Prescription,
            });

        /// <summary>A ward order written now, charted from its start as it is written, unless its doses are held.</summary>
        public MedicationOrder Ordered(string drug, string dose, string frequency, bool highAlert = false, MedicationOrderMode mode = MedicationOrderMode.Written)
        {
            var order = Add(new MedicationOrder
            {
                DrugName = drug,
                Dose = dose,
                Frequency = frequency,
                StartAt = time.GetUtcNow(),
                Source = MedicationOrderSource.Manual,
                IsHighAlert = highAlert,
                Mode = mode,
            });
            Chart(order, time.GetUtcNow());
            return order;
        }

        /// <summary>The pharmacy issues the order now: the next day of its doses is charted.</summary>
        public void Issued(MedicationOrder order)
        {
            order.LastDispensedAt = time.GetUtcNow();
            Chart(order, time.GetUtcNow());
        }

        public List<DateTimeOffset> DosesOf(MedicationOrder order) =>
            [.. Charted.Where(c => c.Dose.MedicationOrderId == order.Id).Select(c => c.Dose.ScheduledAt).Order()];

        public Task<IReadOnlyList<Guid>> OrdersToChartAsync(CancellationToken ct)
        {
            var now = time.GetUtcNow();
            return Task.FromResult<IReadOnlyList<Guid>>(
                [.. _orders.Where(o => o.KeptChartedAhead(DosesOf(o).Count > 0, now) && o.DosesToChart(now, DosesOf(o)).Count > 0).Select(o => o.Id)]);
        }

        public Task<int> ChartAheadAsync(Guid orderId, CancellationToken ct)
        {
            if (Failing.Contains(orderId))
            {
                throw new InvalidOperationException("The database refused the dose.");
            }

            var order = _orders.Single(o => o.Id == orderId);
            return Task.FromResult(order.KeptChartedAhead(DosesOf(order).Count > 0, time.GetUtcNow()) ? Chart(order, time.GetUtcNow()) : 0);
        }

        private MedicationOrder Add(MedicationOrder order)
        {
            order.AdmissionId = _admissionId;
            order.Status = MedicationOrderStatus.Active;
            _orders.Add(order);
            return order;
        }

        private int Chart(MedicationOrder order, DateTimeOffset from)
        {
            if (order.Status != MedicationOrderStatus.Active || order.DosesHeld)
            {
                return 0;
            }

            var doses = order.DosesToChart(from, DosesOf(order));
            foreach (var at in doses)
            {
                Charted.Add((new MedicationAdministration
                {
                    AdmissionId = order.AdmissionId,
                    MedicationOrderId = order.Id,
                    DrugName = order.DrugName,
                    Dose = order.Dose,
                    ScheduledAt = at,
                    Status = MarStatus.Due,
                }, time.GetUtcNow()));
            }

            return doses.Count;
        }
    }
}
