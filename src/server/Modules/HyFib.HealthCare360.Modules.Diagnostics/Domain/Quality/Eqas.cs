namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;

/// <summary>How a round's result compares with the scheme's assigned value (ISO 13528 z-score bands).</summary>
public enum EqasPerformance
{
    /// <summary>|z| ≤ 2.</summary>
    Acceptable = 0,

    /// <summary>2 &lt; |z| ≤ 3: a warning signal, to be looked into.</summary>
    Warning,

    /// <summary>|z| &gt; 3: an action signal. A corrective action (CAPA) is linked before the round is accepted.</summary>
    Unacceptable,
}

/// <summary>
/// An external quality assessment (proficiency testing) scheme the lab is enrolled in (NABL 112, ISO 15189 7.3.7.3): who runs
/// it, and how many rounds a year it sends. Rounds are recorded per analyte (<see cref="EqasRound"/>).
/// </summary>
public sealed class EqasScheme : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>"Clinical chemistry — monthly".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"CMC Vellore", "AIIMS", "Bio-Rad EQAS", "RIQAS".</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The lab's participant code with the provider.</summary>
    public string? ParticipantCode { get; set; }

    /// <summary>Rounds a year (12 monthly, 4 quarterly, 2 half-yearly).</summary>
    public int RoundsPerYear { get; set; } = 12;

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// One round of a scheme for one analyte: the sample's due date, the result the lab reported, and the provider's
/// evaluation, judged by the z-score (ISO 13528: z = (x − X) / σpt) against the assigned value — the target, or the mean of
/// the lab's peer group — and the standard deviation for proficiency assessment. An unacceptable round (|z| &gt; 3) has a
/// corrective action in the Quality module's CAPA register linked before the lab accepts its evaluation. A round whose
/// result was not reported by its due date is overdue (lab dashboard).
/// </summary>
public sealed class EqasRound : AuditableEntity, IFacilityScoped
{
    /// <summary>The bands' limits: |z| above <see cref="WarningLimit"/> warns, above <see cref="ActionLimit"/> is unacceptable.</summary>
    public const decimal WarningLimit = 2m;

    public const decimal ActionLimit = 3m;

    public Guid FacilityId { get; set; }
    public Guid SchemeId { get; set; }
    public Guid LabTestId { get; set; }

    /// <summary>The provider's name for the round and sample ("2026/11 sample 2").</summary>
    public string RoundCode { get; set; } = string.Empty;

    /// <summary>When the provider wants the result.</summary>
    public DateOnly DueDate { get; set; }

    public DateOnly? SampleReceivedOn { get; set; }

    /// <summary>The result the lab reported to the provider (in the test's unit).</summary>
    public decimal? ReportedValue { get; set; }

    public DateOnly? ReportedOn { get; set; }
    public Guid? ReportedBy { get; set; }

    /// <summary>The assigned value: the target, or the peer group's mean.</summary>
    public decimal? AssignedValue { get; set; }

    /// <summary>The standard deviation for proficiency assessment (the target's SD, or the peer group's).</summary>
    public decimal? AssignedSd { get; set; }

    /// <summary>"Peer: Roche Cobas c311, enzymatic"; null when the target applies to all methods.</summary>
    public string? PeerGroup { get; set; }

    public DateOnly? EvaluatedOn { get; set; }
    public decimal? ZScore { get; set; }
    public EqasPerformance? Performance { get; set; }

    /// <summary>The corrective action in the Quality module (CAPA register) for an unacceptable round.</summary>
    public Guid? CapaId { get; set; }

    public string? CapaNumber { get; set; }

    /// <summary>The lab's acceptance of the evaluation (the quality manager or pathologist who reviewed it).</summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    public Guid? AcceptedBy { get; set; }
    public string? AcceptanceNote { get; set; }

    public bool IsReported => ReportedValue is not null;

    public bool IsOverdue(DateOnly today) => !IsReported && DueDate < today;

    /// <summary>An unacceptable round with no corrective action linked yet.</summary>
    public bool NeedsCapa => Performance == EqasPerformance.Unacceptable && CapaId is null;

    /// <summary>z = (x − X) / σpt, rounded to two decimals (pure; unit-tested).</summary>
    public static decimal Z(decimal reported, decimal assigned, decimal sd) =>
        Math.Round((reported - assigned) / sd, 2, MidpointRounding.AwayFromZero);

    /// <summary>The ISO 13528 band of a z-score (pure; unit-tested).</summary>
    public static EqasPerformance Judge(decimal z) =>
        Math.Abs(z) > ActionLimit ? EqasPerformance.Unacceptable
        : Math.Abs(z) > WarningLimit ? EqasPerformance.Warning
        : EqasPerformance.Acceptable;

    /// <summary>Records the result the lab reported. Changing it after the evaluation is refused: it was judged on it.</summary>
    public Result Report(decimal value, DateOnly on, Guid? by, DateOnly today)
    {
        if (Performance is not null)
        {
            return Error.Rule("eqas.evaluated", "This round is already evaluated: the result reported cannot change.");
        }

        if (on > today)
        {
            return Error.Field("reportedOn", "The date reported cannot be in the future.");
        }

        ReportedValue = value;
        ReportedOn = on;
        ReportedBy = by;
        return Result.Success();
    }

    /// <summary>Records the provider's evaluation and judges it. An accepted round is not evaluated again.</summary>
    public Result Evaluate(decimal assigned, decimal sd, string? peerGroup, DateOnly on, DateOnly today)
    {
        if (ReportedValue is not { } reported)
        {
            return Error.Rule("eqas.not_reported", "Record the result the lab reported before its evaluation.");
        }

        if (AcceptedAt is not null)
        {
            return Error.Rule("eqas.accepted", "This round's evaluation is already accepted.");
        }

        if (sd <= 0)
        {
            return Error.Field("assignedSd", "The standard deviation must be above zero.");
        }

        if (on > today)
        {
            return Error.Field("evaluatedOn", "The evaluation date cannot be in the future.");
        }

        AssignedValue = assigned;
        AssignedSd = sd;
        PeerGroup = string.IsNullOrWhiteSpace(peerGroup) ? null : peerGroup.Trim();
        EvaluatedOn = on;
        ZScore = Z(reported, assigned, sd);
        Performance = Judge(ZScore.Value);
        return Result.Success();
    }

    /// <summary>Links the corrective action raised in the Quality module.</summary>
    public Result LinkCapa(Guid capaId, string capaNumber)
    {
        if (Performance is null)
        {
            return Error.Rule("eqas.not_evaluated", "Link a corrective action once the round is evaluated.");
        }

        CapaId = capaId;
        CapaNumber = capaNumber;
        return Result.Success();
    }

    /// <summary>The lab accepts the evaluation: an unacceptable round only once its corrective action is linked.</summary>
    public Result Accept(string? note, Guid? by, DateTimeOffset at)
    {
        if (Performance is null)
        {
            return Error.Rule("eqas.not_evaluated", "Record the provider's evaluation first.");
        }

        if (AcceptedAt is not null)
        {
            return Error.Rule("eqas.accepted", "This round's evaluation is already accepted.");
        }

        if (NeedsCapa)
        {
            return Error.Rule("eqas.capa_required",
                $"This round is unacceptable (z = {ZScore:0.00}): link the corrective action from the CAPA register before accepting it.");
        }

        if (Performance != EqasPerformance.Acceptable && string.IsNullOrWhiteSpace(note))
        {
            return Error.Field("note", "Say what was found for a warning or unacceptable round.");
        }

        AcceptedAt = at;
        AcceptedBy = by;
        AcceptanceNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return Result.Success();
    }
}
