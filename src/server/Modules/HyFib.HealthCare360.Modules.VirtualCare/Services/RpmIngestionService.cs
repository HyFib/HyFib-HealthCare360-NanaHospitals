using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Rpm;
using Npgsql;

namespace HyFib.HealthCare360.Modules.VirtualCare.Services;

/// <summary>
/// Stores readings and evaluates them (RPM-02/03). Each reading is converted to canonical units, checked for
/// plausibility and for the device's capabilities, de-duplicated (devices retry after network drops), classified
/// against the enrolment's limits, and alerts are raised — all in one transaction. A unique index on
/// (enrolment, type, measurement time) is the final guard against duplicates from concurrent retries.
/// </summary>
public sealed class RpmIngestionService(HospitalDbContext db, RpmAlertService alerts, TimeProvider time)
{
    public const int MaxBatch = 500;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    public async Task<Result<IngestResult>> IngestAsync(
        RpmEnrollment enrollment,
        RpmDevice? device,
        IReadOnlyList<ReadingInput> inputs,
        ReadingSource source,
        Guid? recordedBy,
        CancellationToken ct)
    {
        if (enrollment.IsClosed)
        {
            return Error.Rule("rpm.enrollment_closed", "This monitoring enrolment has ended.");
        }

        if (inputs.Count == 0)
        {
            return Error.Field("readings", "Send at least one reading.");
        }

        if (inputs.Count > MaxBatch)
        {
            return Error.Field("readings", $"Send at most {MaxBatch} readings per request.");
        }

        var now = time.GetUtcNow();
        var rejected = new List<string>();
        var candidates = new List<(ReadingInput Input, decimal Value, DateTimeOffset MeasuredAt)>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var line = $"#{i + 1} {input.Type}";
            if (device is not null && !DeviceCapabilities.Measures(device.Type, input.Type))
            {
                rejected.Add($"{line}: a {device.Type} cannot report this reading.");
                continue;
            }

            var normalized = ReadingUnits.Normalize(input.Type, input.Value, input.Unit);
            if (normalized.IsFailure)
            {
                rejected.Add($"{line}: {normalized.Error!.Message}");
                continue;
            }

            var measuredAt = WholeSeconds(input.MeasuredAt ?? now);
            if (measuredAt > now + ClockSkew)
            {
                rejected.Add($"{line}: the measurement time is in the future (check the device clock).");
                continue;
            }

            if (measuredAt < now - MaxAge)
            {
                rejected.Add($"{line}: readings older than {MaxAge.TotalDays:0} days are not accepted.");
                continue;
            }

            candidates.Add((input, normalized.Value, measuredAt));
        }

        var instants = candidates.Select(c => c.MeasuredAt).Distinct().ToList();
        var stored = await db.Set<RpmReading>().AsNoTracking()
            .Where(r => r.EnrollmentId == enrollment.Id && instants.Contains(r.MeasuredAt))
            .Select(r => new { r.Type, r.MeasuredAt })
            .ToListAsync(ct);
        var seen = stored.Select(r => (r.Type, r.MeasuredAt)).ToHashSet();
        var limits = await db.Set<RpmThreshold>().AsNoTracking()
            .Where(t => t.EnrollmentId == enrollment.Id)
            .ToDictionaryAsync(t => t.Type, t => t.Range, ct);

        var accepted = new List<RpmReading>();
        var duplicates = 0;
        foreach (var (input, value, measuredAt) in candidates)
        {
            if (!seen.Add((input.Type, measuredAt)))
            {
                duplicates++;
                continue;
            }

            var severity = ThresholdEvaluator.Evaluate(value, limits.TryGetValue(input.Type, out var range) ? range : null);
            var reading = new RpmReading
            {
                FacilityId = enrollment.FacilityId,
                EnrollmentId = enrollment.Id,
                PatientId = enrollment.PatientId,
                DeviceId = device?.Id,
                Type = input.Type,
                Value = value,
                Unit = ReadingUnits.CanonicalUnit(input.Type),
                TextValue = string.IsNullOrWhiteSpace(input.TextValue) ? null : input.TextValue.Trim()[..Math.Min(200, input.TextValue.Trim().Length)],
                MeasuredAt = measuredAt,
                ReceivedAt = now,
                Severity = severity,
                IsOutOfRange = severity != ReadingSeverity.Normal,
                Source = source,
                RecordedByUserId = recordedBy,
            };
            db.Add(reading);
            accepted.Add(reading);
            enrollment.RecordReading(measuredAt);
        }

        device?.RecordContact(now);
        var raised = enrollment.RaisesAlerts
            ? await alerts.RaiseForReadingsAsync(enrollment, accepted.Where(r => r.IsOutOfRange).ToList(), now, ct)
            : [];

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return Error.Conflict("rpm.duplicate_delivery", "These readings are already being processed. Retry in a moment.");
        }

        await alerts.NotifyInAppAsync(raised.ToList(), ct);
        return new IngestResult(accepted.Count, duplicates, rejected, raised.Count);
    }

    /// <summary>Second precision matches how devices time-stamp readings and makes retries compare equal.</summary>
    private static DateTimeOffset WholeSeconds(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
}
