using System.Globalization;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Features.Schedules;
using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>
/// Scheduled report e-mails (MIS-05, BI-04): create, change, pause, delete and list, with the rules that keep them safe —
/// only reports the author may open, never reports that list patients, a relative period so each e-mail covers new
/// dates, and (when configured) recipients limited to the hospital's e-mail domains. Saving a schedule makes the saver
/// its owner: the report always runs with the owner's current permissions.
/// </summary>
internal sealed class ScheduleService(HospitalDbContext db, ReportCatalog catalog, ISettingsService settings, TimeProvider time)
{
    public async Task<IReadOnlyList<ScheduleDto>> ListAsync(ReportActor actor, CancellationToken ct)
    {
        var schedules = await db.Set<ReportSchedule>().AsNoTracking().OrderBy(s => s.Name).Take(500).ToListAsync(ct);
        return schedules.Select(s => ToDto(s, actor)).ToList();
    }

    public async Task<Result<ScheduleDto>> GetAsync(ReportActor actor, Guid id, CancellationToken ct) =>
        await db.Set<ReportSchedule>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) is { } schedule
            ? ToDto(schedule, actor)
            : Error.NotFound("Schedule", id);

    public async Task<Result<ScheduleDto>> CreateAsync(ReportActor actor, SaveScheduleRequest request, CancellationToken ct)
    {
        if (actor.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var validated = await ValidateAsync(actor, request, facilityId, ct);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var schedule = new ReportSchedule { FacilityId = facilityId };
        Apply(schedule, request, validated.Value, actor);
        schedule.Reschedule(time.GetUtcNow());
        db.Add(schedule);
        await db.SaveChangesAsync(ct);
        return ToDto(schedule, actor);
    }

    public async Task<Result<ScheduleDto>> UpdateAsync(ReportActor actor, Guid id, SaveScheduleRequest request, CancellationToken ct)
    {
        var schedule = await db.Set<ReportSchedule>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null)
        {
            return Error.NotFound("Schedule", id);
        }

        var validated = await ValidateAsync(actor, request, schedule.FacilityId, ct);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(schedule, version);
        }

        Apply(schedule, request, validated.Value, actor);
        schedule.Reschedule(time.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.ConcurrencyConflict;
        }

        return ToDto(schedule, actor);
    }

    public async Task<Result> DeleteAsync(Guid id, uint? version, CancellationToken ct)
    {
        var schedule = await db.Set<ReportSchedule>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null)
        {
            return Error.NotFound("Schedule", id);
        }

        if (version is { } expected)
        {
            db.SetExpectedVersion(schedule, expected);
        }

        schedule.IsActive = false;
        schedule.NextRunAt = null;
        db.Remove(schedule);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.ConcurrencyConflict;
        }

        return Result.Success();
    }

    public ScheduleDto ToDto(ReportSchedule s, ReportActor actor) => new(
        s.Id,
        s.Name,
        s.ReportId,
        catalog.Find(s.ReportId)?.Descriptor.Title ?? s.ReportId,
        ScheduleParameters.Read(s.ParametersJson).Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.Ordinal),
        s.Format,
        s.Frequency,
        s.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture),
        s.DayOfWeek,
        s.DayOfMonth,
        s.Describe(),
        s.Recipients,
        s.IsActive,
        s.OwnerName,
        s.OwnerUserId == actor.UserId,
        s.NextRunAt,
        s.LastRunAt,
        s.LastStatus,
        s.LastError,
        s.Version);

    private static void Apply(ReportSchedule schedule, SaveScheduleRequest request, ValidatedSchedule validated, ReportActor actor)
    {
        schedule.Name = request.Name.Trim();
        schedule.ReportId = validated.ReportId;
        schedule.ParametersJson = ScheduleParameters.Write(validated.Parameters);
        schedule.Format = request.Format;
        schedule.Frequency = request.Frequency;
        schedule.TimeOfDay = validated.Time;
        schedule.DayOfWeek = request.Frequency == ScheduleFrequency.Weekly ? request.DayOfWeek ?? Weekday.Monday : null;
        schedule.DayOfMonth = request.Frequency == ScheduleFrequency.Monthly ? request.DayOfMonth ?? 1 : null;
        schedule.Recipients = validated.Recipients;
        schedule.IsActive = request.IsActive;
        schedule.OwnerUserId = actor.UserId;
        schedule.OwnerName = actor.DisplayName;
    }

    private async Task<Result<ValidatedSchedule>> ValidateAsync(ReportActor actor, SaveScheduleRequest request, Guid facilityId, CancellationToken ct)
    {
        var report = catalog.Find(request.ReportId)?.Descriptor;
        if (report is null || !ReportCatalog.CanRead(report, actor))
        {
            return Error.Field("reportId", "Choose a report you have access to.");
        }

        if (!report.CanSchedule)
        {
            return Error.Field("reportId", "Reports that list patients cannot be e-mailed. Open them in HC360 instead.");
        }

        // Schedules always run for the facility they belong to.
        var inputs = (request.Parameters ?? new Dictionary<string, string?>())
            .Where(kv => !string.Equals(kv.Key, ReportKeys.Facility, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (report.HasPeriod)
        {
            inputs.TryGetValue(ReportKeys.Period, out var period);
            if (period is null && !inputs.ContainsKey(ReportKeys.From))
            {
                inputs[ReportKeys.Period] = ReportPeriods.DefaultFor(request.Frequency);
            }
            else if (period is null or ReportPeriods.Custom)
            {
                return Error.Field(ReportKeys.Period, "Choose a relative period (for example Yesterday or Last month) so each e-mail covers new dates.");
            }
        }

        var arguments = ReportParameterBinder.Bind(report, inputs, AnalyticsClock.Today(time));
        if (arguments.IsFailure)
        {
            return arguments.Error!;
        }

        var recipients = (request.Recipients ?? [])
            .Select(r => r.Trim().ToLowerInvariant())
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var allowed = await settings.GetAsync<string[]>(AnalyticsSettings.AllowedEmailDomains, facilityId, ct) ?? [];
        var domains = allowed.Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).ToList();
        if (domains.Count > 0 && recipients.FirstOrDefault(r => !domains.Any(d => r.EndsWith("@" + d, StringComparison.Ordinal))) is { } outside)
        {
            return Error.Field("recipients", $"{outside} is outside the hospital's e-mail domains ({string.Join(", ", domains)}).");
        }

        var sendAt = TimeOnly.ParseExact(request.Time, "HH:mm", CultureInfo.InvariantCulture);
        var canonical = arguments.Value.ToCanonical()
            .Where(kv => kv.Key != ReportKeys.Facility)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return new ValidatedSchedule(report.Id, canonical, recipients, sendAt);
    }

    private sealed record ValidatedSchedule(string ReportId, IReadOnlyDictionary<string, string> Parameters, List<string> Recipients, TimeOnly Time);
}
