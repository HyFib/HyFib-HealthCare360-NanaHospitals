using System.Globalization;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Features.Schedules;
using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>
/// Scheduled report e-mails (MIS-05, BI-04) and BI extracts (BI-05): create, change, pause, delete and list, with the
/// rules that keep them safe — only reports the author may open, never reports that list patients, a relative period so
/// each run covers new dates, (when configured) recipients limited to the hospital's e-mail domains, and extract file
/// names that are plain and unique in the facility. Saving a schedule makes the saver its owner: the report always runs
/// with the owner's current permissions.
/// </summary>
internal sealed class ScheduleService(
    HospitalDbContext db,
    ReportCatalog catalog,
    ISettingsService settings,
    ExtractFolder extracts,
    ExtractMonitor monitor,
    TimeProvider time)
{
    /// <summary>The facility's schedules by name, at most 500: e-mails and extracts, or only <paramref name="destination"/>.</summary>
    public async Task<IReadOnlyList<ScheduleDto>> ListAsync(ReportActor actor, ScheduleDestination? destination, CancellationToken ct)
    {
        var query = db.Set<ReportSchedule>().AsNoTracking();
        if (destination is { } kind)
        {
            query = query.Where(s => s.Destination == kind);
        }

        var schedules = await query.OrderBy(s => s.Name).ToListAsync(ct);
        return schedules.Where(s => CanOpen(actor, s.ReportId)).Take(500).Select(s => ToDto(s, actor)).ToList();
    }

    /// <summary>
    /// Whether <paramref name="actor"/> may open the report <paramref name="reportId"/>. A schedule, its deliveries and their
    /// files are as closed as the report: to anyone who may not open it they are not there.
    /// </summary>
    public bool CanOpen(ReportActor actor, string reportId) =>
        catalog.Find(reportId)?.Descriptor is { } report && ReportCatalog.CanRead(report, actor);

    public async Task<Result<ScheduleDto>> GetAsync(ReportActor actor, Guid id, CancellationToken ct) =>
        await db.Set<ReportSchedule>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) is { } schedule && CanOpen(actor, schedule.ReportId)
            ? ToDto(schedule, actor)
            : Error.NotFound("Schedule", id);

    public async Task<Result<ScheduleDto>> CreateAsync(ReportActor actor, SaveScheduleRequest request, CancellationToken ct)
    {
        if (actor.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var validated = await ValidateAsync(actor, request, facilityId, null, ct);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var schedule = new ReportSchedule { FacilityId = facilityId };
        Apply(schedule, request, validated.Value, actor);
        schedule.Reschedule(time.GetUtcNow());
        db.Add(schedule);
        await db.SaveChangesAsync(ct);
        if (schedule.IsExtract)
        {
            await monitor.WriteRefreshLogAsync(ct);
        }

        return ToDto(schedule, actor);
    }

    public async Task<Result<ScheduleDto>> UpdateAsync(ReportActor actor, Guid id, SaveScheduleRequest request, CancellationToken ct)
    {
        var schedule = await db.Set<ReportSchedule>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null || !CanOpen(actor, schedule.ReportId))
        {
            return Error.NotFound("Schedule", id);
        }

        var validated = await ValidateAsync(actor, request, schedule.FacilityId, schedule.Id, ct);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(schedule, version);
        }

        var previousFile = schedule.IsExtract ? schedule.ExtractName : null;
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

        if (previousFile is not null || schedule.IsExtract)
        {
            if (previousFile is not null && previousFile != schedule.ExtractName)
            {
                await RemoveFileAsync(schedule.FacilityId, previousFile, ct);
            }

            await monitor.WriteRefreshLogAsync(ct);
        }

        return ToDto(schedule, actor);
    }

    public async Task<Result> DeleteAsync(ReportActor actor, Guid id, uint? version, CancellationToken ct)
    {
        var schedule = await db.Set<ReportSchedule>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null || !CanOpen(actor, schedule.ReportId))
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

        if (schedule is { IsExtract: true, ExtractName: { } file })
        {
            await RemoveFileAsync(schedule.FacilityId, file, ct);
            await monitor.WriteRefreshLogAsync(ct);
        }

        return Result.Success();
    }

    /// <summary>The folder where the signed-in user's facility's extracts are written, and whether HC360 can write it.</summary>
    public async Task<ExtractFolderDto> FolderAsync(Guid? facilityId, CancellationToken ct)
    {
        var code = facilityId is { } id ? await db.Set<Facility>().AsNoTracking().Where(f => f.Id == id).Select(f => f.Code).FirstOrDefaultAsync(ct) : null;
        return new ExtractFolderDto(
            extracts.Root,
            code is null ? null : extracts.FacilityFolder(code),
            Path.Combine(extracts.Root, ExtractFolder.RefreshLogFile),
            extracts.Check());
    }

    private async Task RemoveFileAsync(Guid facilityId, string extractName, CancellationToken ct)
    {
        if (await db.Set<Facility>().AsNoTracking().Where(f => f.Id == facilityId).Select(f => f.Code).FirstOrDefaultAsync(ct) is { } code)
        {
            extracts.Remove(code, extractName);
        }
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
        s.Version,
        s.Destination,
        s.ExtractName,
        s.IsExtract && s.ExtractName is { } name && ExtractFolder.IsValidName(name) ? ExtractFolder.FileName(name) : null,
        s.LastSucceededAt,
        s.IsExtract ? s.State(time.GetUtcNow()) : null);

    private static void Apply(ReportSchedule schedule, SaveScheduleRequest request, ValidatedSchedule validated, ReportActor actor)
    {
        // The file in the folder is no longer what an extract describes once its name, report or inputs change: the
        // extract waits for the refresh that writes the new one.
        if (request.IsExtract && (!schedule.IsExtract || schedule.ExtractName != validated.ExtractName || schedule.ReportId != validated.ReportId
                                  || !SameInputs(schedule.ParametersJson, validated.Parameters)))
        {
            schedule.LastSucceededAt = null;
        }

        schedule.Name = request.Name.Trim();
        schedule.ReportId = validated.ReportId;
        schedule.ParametersJson = ScheduleParameters.Write(validated.Parameters);
        schedule.Destination = request.Destination;
        schedule.ExtractName = validated.ExtractName;
        schedule.Format = request.IsExtract ? ExportFormat.Csv : request.Format;
        schedule.Frequency = request.Frequency;
        schedule.TimeOfDay = validated.Time;
        schedule.DayOfWeek = request.Frequency == ScheduleFrequency.Weekly ? request.DayOfWeek ?? Weekday.Monday : null;
        schedule.DayOfMonth = request.Frequency == ScheduleFrequency.Monthly ? request.DayOfMonth ?? 1 : null;
        schedule.Recipients = validated.Recipients;
        schedule.IsActive = request.IsActive;
        schedule.OwnerUserId = actor.UserId;
        schedule.OwnerName = actor.DisplayName;
    }

    private static bool SameInputs(string storedJson, IReadOnlyDictionary<string, string> inputs)
    {
        var stored = ScheduleParameters.Read(storedJson);
        return stored.Count == inputs.Count && inputs.All(kv => stored.TryGetValue(kv.Key, out var value) && value == kv.Value);
    }

    private async Task<Result<ValidatedSchedule>> ValidateAsync(ReportActor actor, SaveScheduleRequest request, Guid facilityId, Guid? scheduleId, CancellationToken ct)
    {
        var report = catalog.Find(request.ReportId)?.Descriptor;
        if (report is null || !ReportCatalog.CanRead(report, actor))
        {
            return Error.Field("reportId", "Choose a report you have access to.");
        }

        if (!report.CanSchedule)
        {
            return Error.Field("reportId", request.IsExtract
                ? "Reports that list patients cannot be BI extracts. Open them in HC360 instead."
                : "Reports that list patients cannot be e-mailed. Open them in HC360 instead.");
        }

        string? extractName = null;
        if (request.IsExtract)
        {
            extractName = request.ExtractName?.Trim().ToLowerInvariant();
            if (!ExtractFolder.IsValidName(extractName))
            {
                return Error.Field("extractName", ExtractFolder.IsDeviceName(extractName)
                    ? ExtractFolder.DeviceNameMessage
                    : "Name the file with lower-case letters, digits and dashes, for example daily-revenue.");
            }

            if (await db.Set<ReportSchedule>().AnyAsync(s => s.FacilityId == facilityId && s.ExtractName == extractName && s.Id != scheduleId, ct))
            {
                return Error.Field("extractName", $"Another BI extract of this facility already writes {ExtractFolder.FileName(extractName!)}.");
            }
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
                inputs[ReportKeys.Period] = request.IsExtract ? ReportPeriods.ExtractDefault : ReportPeriods.DefaultFor(request.Frequency);
            }
            else if (period is null or ReportPeriods.Custom)
            {
                return Error.Field(ReportKeys.Period, request.IsExtract
                    ? "Choose a relative period (for example This financial year) so each refresh covers new dates."
                    : "Choose a relative period (for example Yesterday or Last month) so each e-mail covers new dates.");
            }
        }

        var arguments = ReportParameterBinder.Bind(report, inputs, AnalyticsClock.Today(time));
        if (arguments.IsFailure)
        {
            return arguments.Error!;
        }

        // An extract goes to the extract folder only, never to e-mail addresses.
        var recipients = request.IsExtract
            ? []
            : (request.Recipients ?? [])
                .Select(r => r.Trim().ToLowerInvariant())
                .Where(r => r.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        var allowed = recipients.Count == 0 ? [] : await settings.GetAsync<string[]>(AnalyticsSettings.AllowedEmailDomains, facilityId, ct) ?? [];
        var domains = allowed.Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).ToList();
        if (domains.Count > 0 && recipients.FirstOrDefault(r => !domains.Any(d => r.EndsWith("@" + d, StringComparison.Ordinal))) is { } outside)
        {
            return Error.Field("recipients", $"{outside} is outside the hospital's e-mail domains ({string.Join(", ", domains)}).");
        }

        var sendAt = TimeOnly.ParseExact(request.Time, "HH:mm", CultureInfo.InvariantCulture);
        var canonical = arguments.Value.ToCanonical()
            .Where(kv => kv.Key != ReportKeys.Facility)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return new ValidatedSchedule(report.Id, canonical, recipients, sendAt, extractName);
    }

    private sealed record ValidatedSchedule(string ReportId, IReadOnlyDictionary<string, string> Parameters, List<string> Recipients, TimeOnly Time, string? ExtractName);
}
