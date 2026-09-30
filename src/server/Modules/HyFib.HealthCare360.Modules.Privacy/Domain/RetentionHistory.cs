namespace HyFib.HealthCare360.Modules.Privacy.Domain;

public enum RetentionRunTrigger
{
    Scheduled = 0,
    Manual,
}

/// <summary>What one kind of data lost in a clean-up: the rows or files removed, and the cut-off date used.</summary>
public sealed class RetentionRunResult
{
    public string Key { get; set; } = string.Empty;
    public int Removed { get; set; }
    public DateTimeOffset Before { get; set; }
}

/// <summary>One run of the retention clean-up, nightly or started by an administrator, with what it removed.</summary>
public sealed class RetentionRun : Entity
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public RetentionRunTrigger Trigger { get; set; }
    public Guid? StartedByUserId { get; set; }
    public List<RetentionRunResult> Results { get; set; } = [];

    /// <summary>Why the run stopped early, if it did (the kinds already done keep their results).</summary>
    public string? Error { get; set; }

    public int TotalRemoved => Results.Sum(r => r.Removed);
}

/// <summary>A change to the retention schedule: who changed which period, from what to what.</summary>
public sealed class RetentionChange : Entity
{
    public string Key { get; set; } = string.Empty;
    public int? PreviousValue { get; set; }
    public int Value { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public Guid? ChangedByUserId { get; set; }

    /// <summary>
    /// Records the change. For medical records it also tells Clinical, which moves the retention date of charts already
    /// completed, in the same transaction.
    /// </summary>
    public static RetentionChange Record(RetentionRule rule, int previous, int value, Guid? userId, DateTimeOffset now)
    {
        var change = new RetentionChange { Key = rule.Key, PreviousValue = previous, Value = value, ChangedAt = now, ChangedByUserId = userId };
        if (rule.Key is RetentionSchedule.OutpatientRecords or RetentionSchedule.InpatientRecords && previous != value)
        {
            change.Raise(new RecordRetentionChanged(rule.Key == RetentionSchedule.InpatientRecords, previous, value));
        }

        return change;
    }
}
