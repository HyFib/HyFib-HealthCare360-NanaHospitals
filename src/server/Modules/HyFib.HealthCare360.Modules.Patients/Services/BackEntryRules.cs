using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>A validator rule for a time in the past: within its record type's back-entry window, or under a late back-entry approval.</summary>
public static class BackEntryRules
{
    public static IRuleBuilderOptions<T, DateTimeOffset?> WithinBackEntry<T>(this IRuleBuilder<T, DateTimeOffset?> rule, BackEntryService backEntry, BackEntryRecord record) =>
        rule.MustAsync((at, ct) => backEntry.AllowsAsync(record, at, ct)).WithErrorCode("backentry.late").WithMessage(Late(record));

    public static IRuleBuilderOptions<T, DateTimeOffset> WithinBackEntry<T>(this IRuleBuilder<T, DateTimeOffset> rule, BackEntryService backEntry, BackEntryRecord record) =>
        rule.MustAsync((at, ct) => backEntry.AllowsAsync(record, at, ct)).WithErrorCode("backentry.late").WithMessage(Late(record));

    /// <summary>The time is not in the future (a desk clock a few minutes fast is allowed).</summary>
    public static IRuleBuilderOptions<T, DateTimeOffset?> NotInFuture<T>(this IRuleBuilder<T, DateTimeOffset?> rule, TimeProvider time) =>
        rule.Must(at => at is null || at <= time.GetUtcNow() + BackEntryService.ClockTolerance);

    private static string Late(BackEntryRecord record) =>
        $"This is older than the back-entry window for {BackEntryWindows.For(record).Name.ToLowerInvariant()}. Ask for a late back-entry approval (Downtime → Late back-entry), then enter it.";
}
