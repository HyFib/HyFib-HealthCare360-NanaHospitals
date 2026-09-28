namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;

/// <summary>
/// A dialyzer tracked for reuse (DIA-04). Reprocessed dialyzers belong to exactly one patient — using another
/// patient's dialyzer transmits infection — and are discarded after the maximum number of uses.
/// </summary>
public sealed class Dialyzer : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public decimal? SurfaceAreaM2 { get; set; }

    /// <summary>Sessions this dialyzer has been used in (1 = first use, new).</summary>
    public int UsesCount { get; set; }

    public int MaxUses { get; set; } = 1;
    public DateTimeOffset? FirstUsedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public bool IsDiscarded { get; set; }
    public DateTimeOffset? DiscardedAt { get; set; }
    public string? DiscardReason { get; set; }

    public int RemainingUses => IsDiscarded ? 0 : Math.Max(0, MaxUses - UsesCount);

    public Result EnsureUsableFor(Guid patientId)
    {
        if (patientId != PatientId)
        {
            return Error.Rule("dialyzer.other_patient", $"Dialyzer {SerialNumber} belongs to another patient and must never be used for this patient.");
        }

        if (IsDiscarded)
        {
            return Error.Rule("dialyzer.discarded", $"Dialyzer {SerialNumber} has been discarded.");
        }

        return UsesCount >= MaxUses
            ? Error.Rule("dialyzer.max_uses", $"Dialyzer {SerialNumber} has reached its maximum of {MaxUses} use(s).")
            : Result.Success();
    }

    /// <summary>Uses the dialyzer for one session and returns the use number printed on the chart ("use 3 of 8").</summary>
    public Result<int> Use(Guid patientId, DateTimeOffset now)
    {
        var usable = EnsureUsableFor(patientId);
        if (usable.IsFailure)
        {
            return usable.Error!;
        }

        UsesCount++;
        FirstUsedAt ??= now;
        LastUsedAt = now;
        if (UsesCount >= MaxUses)
        {
            Discard($"Reached the maximum of {MaxUses} use(s)", now);
        }

        return UsesCount;
    }

    public void Discard(string reason, DateTimeOffset now)
    {
        if (IsDiscarded)
        {
            return;
        }

        IsDiscarded = true;
        DiscardedAt = now;
        DiscardReason = reason.Trim();
    }
}
