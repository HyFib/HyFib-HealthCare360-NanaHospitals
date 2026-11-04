using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// <see cref="QueryFilterGuard"/>. EF Core ignores a query filter for the whole query, so a source read without a filter
/// inside a query took the filter off the query's other sources too: a front desk listed every branch's appointments
/// because the department names were read from every facility. A query in which a source would lose a filter it did not
/// ask to lose is refused when EF compiles it. The queries are translated with the Npgsql provider, never run.
/// </summary>
public sealed class QueryFilterGuardTests : IDisposable
{
    private const string Facility = HospitalDbContext.FacilityFilter;
    private const string SoftDelete = HospitalDbContext.SoftDeleteFilter;

    private readonly HospitalDbContext _db = new(
        new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options,
        new ModuleCatalog([]));

    public void Dispose() => _db.Dispose();

    [Fact]
    public void A_lookup_read_from_every_facility_inside_one_facilitys_list_is_refused()
    {
        var query =
            from a in _db.Set<ApprovalRequest>()
            join d in _db.Set<Department>().IgnoreQueryFilters([Facility]) on a.SubjectId equals d.Id
            select new { a.Title, d.Name };

        var refused = Should.Throw<InvalidOperationException>(() => query.ToQueryString());

        refused.Message.ShouldContain("ApprovalRequest without its 'facility' filter");
        refused.Message.ShouldNotContain("Department without");
    }

    [Fact]
    public void A_lookup_that_reads_removed_rows_would_bring_back_the_lists_removed_rows_and_is_refused()
    {
        var names =
            from p in _db.Set<Practitioner>()
            join u in _db.Set<User>().IgnoreQueryFilters() on p.Id equals u.PractitionerId
            select new { p.DisplayName, Account = u.DisplayName };
        var approvals =
            from a in _db.Set<ApprovalRequest>()
            join u in _db.Set<User>().IgnoreQueryFilters() on a.RequestedBy equals u.Id
            select new { a.Title, u.DisplayName };

        Should.Throw<InvalidOperationException>(() => names.ToQueryString()).Message.ShouldContain("Practitioner without its 'soft_delete' filter");
        var refused = Should.Throw<InvalidOperationException>(() => approvals.ToQueryString()).Message;
        refused.ShouldContain("ApprovalRequest without its ");
        refused.ShouldContain("'soft_delete'");
        refused.ShouldContain("'facility'");
    }

    [Fact]
    public void A_source_read_from_every_facility_keeps_its_removed_rows_out_unless_it_says_otherwise()
    {
        var query =
            from a in _db.Set<ApprovalRequest>().IgnoreQueryFilters([Facility])
            join u in _db.Set<User>().IgnoreQueryFilters() on a.RequestedBy equals u.Id
            select new { a.Title, u.DisplayName };

        var refused = Should.Throw<InvalidOperationException>(() => query.ToQueryString()).Message;

        refused.ShouldContain("ApprovalRequest without its 'soft_delete' filter", Case.Sensitive, "it asked to lose the facility filter only");
    }

    [Fact]
    public void A_lookup_in_a_subquery_kept_in_a_variable_is_seen_too()
    {
        var everyAccount = _db.Set<User>().IgnoreQueryFilters().Select(u => u.Id);
        var query = _db.Set<ApprovalRequest>().Where(a => everyAccount.Contains(a.RequestedBy));

        Should.Throw<InvalidOperationException>(() => query.ToQueryString()).Message.ShouldContain("ApprovalRequest without its ");
    }

    [Fact]
    public void Sources_that_each_say_they_are_read_from_every_facility_are_read_so()
    {
        var sql = (
                from a in _db.Set<ApprovalRequest>().IgnoreQueryFilters([Facility])
                join d in _db.Set<Department>().IgnoreQueryFilters([Facility]) on a.SubjectId equals d.Id
                select new { a.Title, d.Name })
            .ToQueryString();

        sql.ShouldNotContain("@ef_filter__", Case.Sensitive, "no facility filter on either");
        sql.ShouldContain("NOT (a.is_deleted)", Case.Sensitive, "the requests keep the soft-delete filter");
        sql.ShouldContain("NOT (d.is_deleted)", Case.Sensitive, "and so do the departments");
    }

    [Fact]
    public void A_whole_query_read_from_every_facility_is_read_so()
    {
        var sql = (
                from a in _db.Set<ApprovalRequest>()
                join d in _db.Set<Department>() on a.SubjectId equals d.Id
                select new { a.Title, d.Name })
            .IgnoreQueryFilters([Facility])
            .ToQueryString();

        sql.ShouldNotContain("@ef_filter__");
        sql.ShouldContain("NOT (a.is_deleted)");
    }

    [Fact]
    public void A_source_without_the_ignored_filter_keeps_the_filters_it_has()
    {
        var sql = (
                from p in _db.Set<Practitioner>()
                join d in _db.Set<Department>().IgnoreQueryFilters([Facility]) on p.DepartmentId equals d.Id
                select new { p.DisplayName, d.Name })
            .ToQueryString();

        sql.ShouldContain("NOT (p.is_deleted)", Case.Sensitive, "the doctors keep the soft-delete filter, the only one they have");
    }

    [Fact]
    public void A_source_with_no_filter_is_left_alone()
    {
        var sql = (
                from r in _db.Set<UserRole>()
                join u in _db.Set<User>().IgnoreQueryFilters() on r.UserId equals u.Id
                select new { r.RoleId, u.DisplayName })
            .ToQueryString();

        sql.ShouldNotContain("is_deleted");
    }

    [Fact]
    public void A_query_that_ignores_no_filter_is_never_refused()
    {
        var sql = (
                from a in _db.Set<ApprovalRequest>()
                join d in _db.Set<Department>() on a.SubjectId equals d.Id
                join u in _db.Set<User>() on a.RequestedBy equals u.Id
                select new { a.Title, d.Name, u.DisplayName })
            .ToQueryString();

        sql.ShouldContain("@ef_filter__");
        sql.ShouldContain("NOT (u.is_deleted)");
    }

    [Fact]
    public void NotRemoved_keeps_a_sources_removed_rows_out_where_another_source_reads_its_own()
    {
        var sql = (
                from p in _db.Set<Practitioner>().NotRemoved()
                join u in _db.Set<User>().IgnoreQueryFilters([SoftDelete]) on p.Id equals u.PractitionerId
                select new { p.DisplayName, Account = u.DisplayName })
            .ToQueryString();

        sql.ShouldContain("NOT (p.is_deleted)", Case.Sensitive, "a removed doctor stays out");
        sql.ShouldNotContain("NOT (u.is_deleted)", Case.Sensitive, "a removed account is still named");
    }
}
