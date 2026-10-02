using FluentValidation;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Numbering;

/// <summary>
/// One number series as the administrator sees it: for the signed-in user's facility, or shared by all facilities
/// (<see cref="Scope"/> "facility" or "global"). A series nobody has used or changed yet shows its defaults.
/// </summary>
/// <param name="LastNumber">The running number last issued in the current period (this year, this month…); 0 if none.</param>
/// <param name="NextExample">The number the next allocation would make now.</param>
/// <param name="FacilityCode">What {FAC} stands for in this series' numbers.</param>
/// <param name="IsCustomised">The format or the reset differs from the default.</param>
public sealed record NumberSeriesItem(
    string Key,
    string Name,
    string Area,
    string Scope,
    string Format,
    NumberResetPolicy ResetPolicy,
    long LastNumber,
    string NextExample,
    string? FacilityCode,
    bool IsCustomised,
    bool IsLocked,
    string? LockedReason,
    int MaxLength,
    string DefaultFormat,
    NumberResetPolicy DefaultResetPolicy);

/// <summary>
/// Every series, by area then name, with what the format rules need to know about the hospital: how many facilities it
/// has (more than one: per-facility numbers need {FAC}) and its longest facility code (for the length of numbers).
/// </summary>
public sealed record NumberSeriesList(int FacilityCount, int LongestFacilityCode, IReadOnlyList<NumberSeriesItem> Series);

/// <summary>
/// A change to one series. <see cref="ExpectedFormat"/> and <see cref="ExpectedResetPolicy"/> are the values the
/// administrator started from: if someone else changed them meanwhile, the save is refused (409 numbering.changed).
/// </summary>
/// <param name="StartAt">The next number to issue (for example to continue from an older system); null keeps counting.</param>
public sealed record SaveNumberSeriesRequest(
    string Format,
    NumberResetPolicy ResetPolicy,
    long? StartAt,
    string ExpectedFormat,
    NumberResetPolicy ExpectedResetPolicy);

public sealed class SaveNumberSeriesRequestValidator : AbstractValidator<SaveNumberSeriesRequest>
{
    public SaveNumberSeriesRequestValidator()
    {
        RuleFor(x => x.Format).Custom((format, context) =>
        {
            foreach (var problem in NumberFormatRules.FormatProblems(format?.Trim()))
            {
                context.AddFailure(problem);
            }
        });
        RuleFor(x => x.ResetPolicy).IsInEnum().WithMessage("Choose when the numbers start again from 1.");
        RuleFor(x => x.StartAt).GreaterThanOrEqualTo(1).WithMessage("Enter a number of 1 or more.");
        RuleFor(x => x.ExpectedFormat).NotNull().MaximumLength(NumberFormatRules.FormatColumnLength);
        RuleFor(x => x.ExpectedResetPolicy).IsInEnum();
    }
}

/// <summary>
/// /api/v1/admin/number-series: the formats of the numbers the hospital issues, which administrators can set before the
/// first number is issued. Changes apply to numbers issued afterwards; numbers already issued never change.
/// </summary>
internal static class NumberSeriesEndpoints
{
    /// <summary>Saves give way to numbers being issued at the same moment; after this many tries the administrator retries.</summary>
    private const int SaveAttempts = 5;

    private static readonly Error FacilityRequired = Error.Rule("facility.required", "Select a facility first.");

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/number-series").WithTags("Administration").RequirePermission(PlatformPermissions.NumberingManage);

        group.MapGet("/", List);
        group.MapPut("/{key}", Save).WithValidation<SaveNumberSeriesRequest>();
    }

    private static async Task<IResult> List(HospitalDbContext db, NumberSeriesCatalog catalog, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return FacilityRequired.ToProblem();
        }

        var now = NumberFormat.ToHospitalTime(time.GetUtcNow());
        var codes = await db.Set<Facility>().AsNoTracking().Select(f => f.Code).ToListAsync(ct);
        var rows = (await db.Set<NumberSeries>().AsNoTracking()
                .Where(s => s.FacilityId == null || s.FacilityId == facilityId)
                .ToListAsync(ct))
            .ToDictionary(s => (s.Key, s.FacilityId));
        var facilityCode = await NumberSeriesService.FacilityCodeAsync(db, facilityId, ct);
        var sharedCode = await NumberSeriesService.FacilityCodeAsync(db, null, ct);

        var series = catalog.All
            .Where(s => !s.Hidden)
            .Select(s => ToItem(s, rows.GetValueOrDefault((s.Key, s.PerFacility ? facilityId : null)), s.PerFacility ? facilityCode : sharedCode, now))
            .OrderBy(s => s.Area, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return TypedResults.Ok(new NumberSeriesList(codes.Count, LongestCode(codes), series));
    }

    private static async Task<IResult> Save(
        string key,
        SaveNumberSeriesRequest request,
        HospitalDbContext db,
        NumberSeriesCatalog catalog,
        ICurrentUser user,
        IAuditLogger audit,
        TimeProvider time,
        CancellationToken ct)
    {
        if (catalog.Find(key) is not { Hidden: false } series)
        {
            return new Error("numbering.not_found", "There is no such number series.", ErrorType.NotFound).ToProblem();
        }

        Guid? scope = null;
        if (series.PerFacility)
        {
            if (user.FacilityId is not { } facilityId)
            {
                return FacilityRequired.ToProblem();
            }

            scope = facilityId;
        }

        var format = request.Format.Trim();
        for (var attempt = 0; attempt < SaveAttempts; attempt++)
        {
            if (attempt > 0)
            {
                // A retry starts again from the row as it is now: a number may have been issued meanwhile.
                db.ChangeTracker.Clear();
            }

            var now = NumberFormat.ToHospitalTime(time.GetUtcNow());
            var row = await db.Set<NumberSeries>().FirstOrDefaultAsync(s => s.Key == series.Key && s.FacilityId == scope, ct);
            var currentFormat = row?.Format ?? series.DefaultFormat;
            var currentReset = row?.ResetPolicy ?? series.DefaultReset;
            if (!string.Equals(currentFormat, request.ExpectedFormat.Trim(), StringComparison.Ordinal) || currentReset != request.ExpectedResetPolicy)
            {
                return Error.Conflict(
                    "numbering.changed",
                    "Someone else changed this number format while you were editing it. Check the format as it is now and make your change again.").ToProblem();
            }

            var formatChanged = !string.Equals(format, currentFormat, StringComparison.Ordinal);
            var resetChanged = request.ResetPolicy != currentReset;
            if (series.IsLocked && (formatChanged || resetChanged))
            {
                return Error.Rule("numbering.locked", $"{series.LockedReason} Only the next number can be changed.").ToProblem();
            }

            if (!formatChanged && !resetChanged && request.StartAt is null)
            {
                return TypedResults.NoContent();
            }

            var last = row is null ? 0 : NumberFormat.LastIssued(row.CurrentValue, row.CurrentPeriod, currentReset, now);
            var change = new NumberFormatChange
            {
                Series = series,
                Format = format,
                ResetPolicy = request.ResetPolicy,
                StartAt = request.StartAt,
                CurrentFormat = currentFormat,
                CurrentResetPolicy = currentReset,
                LastNumber = last,
            };
            if (formatChanged || resetChanged)
            {
                var facilities = await db.Set<Facility>().AsNoTracking().Select(f => new { f.Id, f.Code }).ToListAsync(ct);
                change = change with
                {
                    FacilityCount = facilities.Count,
                    LongestFacilityCode = LongestCode(facilities.Select(f => f.Code)),
                    OtherFormats = await OtherFormatsAsync(db, catalog, series, scope, facilities.Select(f => f.Id).ToList(), ct),
                };
            }

            var errors = NumberFormatRules.Check(change);
            if (errors.Count > 0)
            {
                return Error.Validation("validation", errors.First().Value[0], errors).ToProblem();
            }

            if (row is null)
            {
                // Set up before the first number: the row the first allocation would have created, with this change.
                row = new NumberSeries { Key = series.Key, FacilityId = scope, ResetPolicy = currentReset };
                db.Add(row);
            }

            var counter = NumberFormat.CounterAfterChange(
                new NumberCounter(row.CurrentValue, row.CurrentPeriod), currentReset, request.ResetPolicy, request.StartAt, now);
            row.Format = format;
            row.ResetPolicy = request.ResetPolicy;
            row.CurrentValue = counter.Value;
            row.CurrentPeriod = counter.Period;

            audit.Add(new AuditEntry(AuditCategories.Admin, "numbering.changed", Summary(series, change, formatChanged, resetChanged))
            {
                EntityType = nameof(NumberSeries),
                EntityId = series.Key,
                Details = new
                {
                    series = series.Key,
                    facilityId = scope,
                    from = new { format = currentFormat, resetPolicy = currentReset.ToString(), nextNumber = last + 1 },
                    to = new { format, resetPolicy = request.ResetPolicy.ToString(), nextNumber = request.StartAt ?? last + 1 },
                },
                // A new next number leaves a gap in the series, which auditors of bills and receipts ask about.
                Severity = request.StartAt is null ? AuditSeverity.Info : AuditSeverity.Warning,
            });

            try
            {
                await db.SaveChangesAsync(ct);
                return TypedResults.NoContent();
            }
            catch (DbUpdateConcurrencyException)
            {
                // A number was issued between reading the row and saving it: read it again.
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // The first number of the series was issued just now, creating its row: save over that row instead.
            }
        }

        return Error.Conflict("numbering.busy", "Numbers of this kind are being issued right now. Save again in a moment.").ToProblem();
    }

    private static NumberSeriesItem ToItem(NumberSeriesDefinition series, NumberSeries? row, string? facilityCode, DateTimeOffset now)
    {
        var format = row?.Format ?? series.DefaultFormat;
        var reset = row?.ResetPolicy ?? series.DefaultReset;
        var last = row is null ? 0 : NumberFormat.LastIssued(row.CurrentValue, row.CurrentPeriod, reset, now);
        return new NumberSeriesItem(
            series.Key,
            series.Name,
            series.Area,
            series.PerFacility ? "facility" : "global",
            format,
            reset,
            last,
            NumberFormat.Render(format, last + 1, facilityCode, now),
            facilityCode,
            IsCustomised: !string.Equals(format, series.DefaultFormat, StringComparison.Ordinal) || reset != series.DefaultReset,
            series.IsLocked,
            series.LockedReason,
            series.MaxLength,
            series.DefaultFormat,
            series.DefaultReset);
    }

    /// <summary>
    /// The formats a changed format must differ from: the other series of the same facility and the shared ones; for a
    /// shared series, every facility's.
    /// </summary>
    private static async Task<IReadOnlyCollection<OtherNumberFormat>> OtherFormatsAsync(
        HospitalDbContext db,
        NumberSeriesCatalog catalog,
        NumberSeriesDefinition series,
        Guid? scope,
        IReadOnlyList<Guid> facilityIds,
        CancellationToken ct)
    {
        var stored = (await db.Set<NumberSeries>().AsNoTracking().Select(s => new { s.Key, s.FacilityId, s.Format }).ToListAsync(ct))
            .ToDictionary(s => (s.Key, s.FacilityId), s => s.Format);
        var others = new List<OtherNumberFormat>();
        foreach (var other in catalog.All.Where(d => !d.Hidden && d.Key != series.Key))
        {
            IEnumerable<Guid?> scopes = !other.PerFacility ? [null]
                : series.PerFacility ? [scope]
                : facilityIds.Select(id => (Guid?)id);
            foreach (var otherScope in scopes)
            {
                var format = stored.GetValueOrDefault((other.Key, otherScope)) ?? other.DefaultFormat;
                others.Add(new OtherNumberFormat(other.Name, format, string.Equals(format, other.DefaultFormat, StringComparison.Ordinal)));
            }
        }

        return others;
    }

    private static int LongestCode(IEnumerable<string> codes) =>
        Math.Min(codes.Select(c => c.Length).DefaultIfEmpty(0).Max(), NumberFormatRules.MaxFacilityCodeLength);

    /// <summary>"OP bill numbers: format {FAC}OB{YY}{SEQ:7} → {FAC}/OP/{YY}/{SEQ:6}, next number 501".</summary>
    private static string Summary(NumberSeriesDefinition series, NumberFormatChange change, bool formatChanged, bool resetChanged)
    {
        var changes = new List<string>();
        if (formatChanged)
        {
            changes.Add($"format {change.CurrentFormat} → {change.Format}");
        }

        if (resetChanged)
        {
            changes.Add($"restarts {RestartWords(change.CurrentResetPolicy)} → {RestartWords(change.ResetPolicy)}");
        }

        if (change.StartAt is { } startAt)
        {
            changes.Add($"next number {startAt}");
        }

        return $"{series.Name} numbers: {string.Join(", ", changes)}";
    }

    private static string RestartWords(NumberResetPolicy policy) => policy switch
    {
        NumberResetPolicy.Yearly => "every year",
        NumberResetPolicy.Monthly => "every month",
        NumberResetPolicy.Daily => "every day",
        _ => "never",
    };
}
