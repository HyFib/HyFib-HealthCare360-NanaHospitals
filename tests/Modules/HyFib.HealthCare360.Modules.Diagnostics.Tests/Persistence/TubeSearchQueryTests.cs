using System.Linq.Expressions;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Persistence;

/// <summary>
/// The lab's searches and look-ups by tube number, composed as their endpoints and services compose them, and translated
/// by the PostgreSQL provider without a database (<c>ToQueryString</c>). Translation runs as it does for a request: the
/// query filter guard first, then Npgsql. So a condition EF cannot translate (a compiled delegate, an <c>Invoke</c>), or
/// one that would lift the facility filter from the rest of the query, fails here as it fails for a request. One did: the
/// tube-number filter read every facility's tubes inside the search, and once <c>lab.tubeCheckDigitSince</c> was set the
/// collection list's search answered 500 for a UHID, and an analyser's results for a tube numbered before the check digit
/// were refused (<see cref="TubeNumbers"/>).
/// </summary>
public sealed class TubeSearchQueryTests
{
    private static readonly DateTimeOffset Switched = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// What the lab types in a search: a UHID, a name, a phone number, the end of a UHID, an order number, a tube number,
    /// one typed with a digit wrong, and the number of a tube from before the check digit.
    /// </summary>
    private static readonly string[] Terms =
        ["NH26000888", "Meena Kumari", "9876543210", "0888", "NHLB260000123", "1000012342", "1000012352", "1000000123"];

    /// <summary>
    /// Each term on a database whose tubes all have a check digit, and on one with tubes from before it, with or without a
    /// tube made since one slip away from the term.
    /// </summary>
    public static TheoryData<string, bool, bool> Searches()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var term in Terms)
        {
            data.Add(term, false, false);
            data.Add(term, true, false);
            data.Add(term, true, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Searches))]
    public async Task The_collection_lists_search_is_one_query_for_every_kind_of_term(string term, bool oldTubes, bool newTubeNear)
    {
        await using var db = Context(inFacility: true);
        var byNumber = await ByNumberAsync(db, term, oldTubes, newTubeNear);

        // As SampleEndpoints.PendingCollection composes it.
        var sql = db.Set<LabSample>().AsNoTracking().Where(s => s.Status == SampleStatus.Pending)
            .Where(SampleEndpoints.SearchFilter(term, byNumber, db, Substitute.For<IFieldProtector>()))
            .OrderByUrgency(s => s.Priority).ThenBy(s => s.CreatedAt)
            .Select(s => new { s.Id, s.Barcode, s.LabOrderId, s.PatientId, s.Priority, s.Container, s.SpecimenType, s.Section, s.RecollectionOfSampleId })
            .ToQueryString();

        FindsByNumber(sql, byNumber);
        sql.ShouldContain("FROM patients.patients", Case.Sensitive, "the patient's name or number still finds the tube");
        sql.ShouldContain("FROM diagnostics.lab_orders", Case.Sensitive, "and the order number");
    }

    [Theory]
    [MemberData(nameof(Searches))]
    public async Task The_worklists_search_is_one_query_for_every_kind_of_term(string term, bool oldTubes, bool newTubeNear)
    {
        await using var db = Context(inFacility: true);
        var byNumber = await ByNumberAsync(db, term, oldTubes, newTubeNear);

        // As LabResultEndpoints.Worklist composes it.
        var sql = db.Set<LabSample>().AsNoTracking().Where(s => s.Status == SampleStatus.Received || s.Status == SampleStatus.InProcess)
            .Where(LabResultEndpoints.SearchFilter(term, byNumber, db))
            .OrderByUrgency(s => s.Priority)
            .ThenByDescending(s => db.Set<LabResult>().Any(r => r.SampleId == s.Id && r.Status != LabResultStatus.Verified
                                                               && (r.Flag == ResultFlag.CriticalLow || r.Flag == ResultFlag.CriticalHigh || r.Flag == ResultFlag.Critical)))
            .ThenBy(s => s.ReceivedAt)
            .Select(s => new { s.Id, s.Barcode, s.LabOrderId, s.PatientId, s.Priority, s.Section, s.Status, s.ReceivedAt })
            .ToQueryString();

        FindsByNumber(sql, byNumber);
        sql.ShouldContain("FROM diagnostics.lab_orders", Case.Sensitive, "the order number still finds the tube");
    }

    /// <summary>
    /// The look-ups of a number as typed or sent: the lab screens' look-up and receipt at the counter (a user of a
    /// facility), and an analyser's results and host queries (no user: the analyser's facility is checked on the tube).
    /// </summary>
    [Theory]
    [MemberData(nameof(Searches))]
    public async Task A_look_up_by_tube_number_is_one_query_for_every_kind_of_number(string term, bool oldTubes, bool newTubeNear)
    {
        foreach (var inFacility in new[] { true, false })
        {
            await using var db = Context(inFacility);
            var byNumber = await ByNumberAsync(db, term, oldTubes, newTubeNear);

            // As SampleService (FindByBarcodeAsync, ReceiveAsync) and InstrumentResultSink (ReceiveAsync, FindTubeAsync) do.
            var sql = db.Set<LabSample>().AsNoTracking().Where(byNumber).ToQueryString();

            FindsByNumber(sql, byNumber);
        }
    }

    /// <summary>
    /// These tests fail as a request would: on a filter that reads every facility's tubes inside a query of tubes (what the
    /// tube-number filter did), and on one EF cannot translate.
    /// </summary>
    [Fact]
    public async Task A_filter_that_lifts_the_facility_filter_or_cannot_be_translated_fails_here()
    {
        await using var db = Context(inFacility: true);
        var everyFacility = db.Set<LabSample>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);
        string[] near = ["1000012342"];
        Func<LabSample, bool> compiled = s => s.Barcode == "1000012342";

        Should.Throw<InvalidOperationException>(() => db.Set<LabSample>()
                .Where(s => s.Barcode == "1000012346" && !everyFacility.Any(t => near.Contains(t.Barcode) && t.CreatedAt >= Switched))
                .ToQueryString())
            .Message.ShouldStartWith("A query would read LabSample without its 'facility' filter");
        Should.Throw<InvalidOperationException>(() => db.Set<LabSample>().Where(s => compiled(s)).ToQueryString())
            .Message.ShouldContain("could not be translated");
    }

    /// <summary>
    /// The query reads tubes once, and finds them by the number's condition when it has one: the number is a condition on
    /// the tube in the caller's own query, not a query of tubes inside it.
    /// </summary>
    private static void FindsByNumber(string sql, Expression<Func<LabSample, bool>> byNumber)
    {
        Regex.Count(sql, "FROM diagnostics.lab_samples").ShouldBe(1, sql);
        sql.Contains("barcode = @", StringComparison.Ordinal).ShouldBe(byNumber.Body is not ConstantExpression { Value: false }, sql);
    }

    /// <summary>
    /// What <see cref="TubeNumbers.MatchingAsync(string, CancellationToken)"/> gives for <paramref name="term"/>, its own
    /// query (is a tube made since one slip away) translated as it reads it: every facility's tubes, on their own.
    /// </summary>
    private static Task<Expression<Func<LabSample, bool>>> ByNumberAsync(HospitalDbContext db, string term, bool oldTubes, bool newTubeNear) =>
        TubeNumbers.MatchingAsync(term, oldTubes ? Switched : null, madeSince =>
        {
            var sql = db.Set<LabSample>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(madeSince).ToQueryString();
            sql.ShouldContain("barcode = ANY", Case.Sensitive, sql);
            sql.ShouldNotContain("facility_id =", Case.Sensitive, "a number may be a mistyping of any facility's tube");
            return Task.FromResult(newTubeNear);
        });

    private static HospitalDbContext Context(bool inFacility)
    {
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only")
            .UseSnakeCaseNamingConvention()
            .Options;
        var user = Substitute.For<ICurrentUser>();
        user.FacilityId.Returns(Guid.CreateVersion7());
        return new HospitalDbContext(options, new ModuleCatalog([new PatientsModule(), new DiagnosticsModule()]), inFacility ? user : null);
    }
}
