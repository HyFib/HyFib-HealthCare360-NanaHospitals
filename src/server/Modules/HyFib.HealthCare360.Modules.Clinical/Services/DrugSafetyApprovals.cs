using System.Globalization;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>A drug–drug interaction as the prescribing check reads it: the pair, how severe, what it says, and whether it is checked.</summary>
public sealed record InteractionState(string DrugA, string DrugB, InteractionSeverity Severity, string Effect, string? Management, bool IsActive)
{
    public static InteractionState Of(DrugInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        return new(interaction.DrugA, interaction.DrugB, interaction.Severity, interaction.Effect, interaction.Management, interaction.IsActive);
    }

    public void ApplyTo(DrugInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        (interaction.DrugA, interaction.DrugB, interaction.Severity) = (DrugA, DrugB, Severity);
        (interaction.Effect, interaction.Management, interaction.IsActive) = (Effect, Management, IsActive);
    }

    /// <summary>"warfarin + aspirin: Major, blocks signing", or "…: switched off".</summary>
    public string Words() => $"{DrugA} + {DrugB}: " + (IsActive ? DrugSafetyChanges.SeverityWords(Severity) : "switched off (not checked)");
}

/// <summary>A maximum daily dose as the prescribing check reads it.</summary>
public sealed record DoseLimitState(string GenericName, decimal MaxDailyDoseMg, string? Route, string? Notes, bool IsActive)
{
    public static DoseLimitState Of(DrugDoseLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        return new(limit.GenericName, limit.MaxDailyDoseMg, limit.Route, limit.Notes, limit.IsActive);
    }

    public void ApplyTo(DrugDoseLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        (limit.GenericName, limit.MaxDailyDoseMg, limit.Route, limit.Notes, limit.IsActive) = (GenericName, MaxDailyDoseMg, Route, Notes, IsActive);
    }

    /// <summary>"paracetamol, oral: at most 4,000 mg a day", or "…: switched off".</summary>
    public string Words() =>
        $"{GenericName}, {Route ?? "any route"}: " + (IsActive ? $"at most {DrugSafetyChanges.Mg(MaxDailyDoseMg)} a day" : "switched off (not checked)");
}

/// <summary>
/// Which changes to the prescribing-safety tables loosen them (ERX-02). The tables make the alerts every prescriber in
/// the hospital gets before signing: a Major interaction and a dose above the maximum block signing. A change that
/// switches an alert off or weakens it (an interaction switched off, made less severe or moved to another pair of drugs; a
/// maximum dose switched off, raised, or moved to another drug or a narrower route) waits for a second person; one that
/// adds or strengthens an alert, or changes only the words, is saved at once. Only what alerts now counts: a row already
/// switched off alerts nobody, so changing it loosens nothing.
/// </summary>
public static class DrugSafetyChanges
{
    public static bool Loosens(InteractionState before, InteractionState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (!before.IsActive)
        {
            return false;
        }

        return !after.IsActive || !SamePair(before, after) || after.Severity < before.Severity;
    }

    public static bool Loosens(DoseLimitState before, DoseLimitState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (!before.IsActive)
        {
            return false;
        }

        // No route checks every route: from any route to one, or from one route to another, leaves routes unchecked.
        var narrower = !string.Equals(before.Route, after.Route, StringComparison.Ordinal) && after.Route is not null;
        return !after.IsActive || !string.Equals(before.GenericName, after.GenericName, StringComparison.Ordinal) || narrower
               || after.MaxDailyDoseMg > before.MaxDailyDoseMg;
    }

    /// <summary>What the change switches off for prescribers, in words, for the person who decides.</summary>
    public static string Consequences(InteractionState before, InteractionState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var pair = $"{before.DrugA} with {before.DrugB}";
        if (!after.IsActive || !SamePair(before, after))
        {
            return before.Severity == InteractionSeverity.Major
                ? $"Prescribing {pair} no longer blocks signing, nor warns, for every prescriber in the hospital."
                : $"Prescribing {pair} no longer warns, for every prescriber in the hospital.";
        }

        return before.Severity == InteractionSeverity.Major
            ? $"Prescribing {pair} no longer blocks signing: the doctor acknowledges a warning instead, for every prescriber in the hospital."
            : $"Prescribing {pair} warns as a {after.Severity.ToString().ToLowerInvariant()} interaction instead of a {before.Severity.ToString().ToLowerInvariant()} one.";
    }

    public static string Consequences(DoseLimitState before, DoseLimitState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (!after.IsActive || !string.Equals(before.GenericName, after.GenericName, StringComparison.Ordinal))
        {
            return $"A prescription with any daily dose of {before.GenericName} is signed without the maximum-dose block, for every prescriber in the hospital.";
        }

        List<string> effects = [];
        if (after.MaxDailyDoseMg > before.MaxDailyDoseMg)
        {
            effects.Add($"a daily dose of {before.GenericName} up to {Mg(after.MaxDailyDoseMg)} (above {Mg(before.MaxDailyDoseMg)} today) is signed without the block");
        }

        if (!string.Equals(before.Route, after.Route, StringComparison.Ordinal) && after.Route is not null)
        {
            effects.Add(before.Route is null
                ? $"only the {after.Route} route is checked, no longer every route"
                : $"the {before.Route} route is no longer checked (the {after.Route} route is instead)");
        }

        var text = string.Join("; ", effects) + ", for every prescriber in the hospital.";
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    public static string SeverityWords(InteractionSeverity severity) => severity switch
    {
        InteractionSeverity.Major => "Major, blocks signing",
        InteractionSeverity.Moderate => "Moderate, a warning",
        _ => "Minor, a warning",
    };

    public static string Mg(decimal value) => value.ToString("#,##0.##", CultureInfo.InvariantCulture) + " mg";

    private static bool SamePair(InteractionState a, InteractionState b) =>
        string.Equals(a.DrugA, b.DrugA, StringComparison.Ordinal) && string.Equals(a.DrugB, b.DrugB, StringComparison.Ordinal);
}

/// <summary>An interaction loosened, as asked: the row, what it is now, and what it becomes.</summary>
public sealed record DrugInteractionChange(Guid Id, InteractionState Before, InteractionState After);

/// <summary>A maximum dose loosened, as asked: the row, what it is now, and what it becomes.</summary>
public sealed record DoseLimitChange(Guid Id, DoseLimitState Before, DoseLimitState After);

/// <summary>
/// The prescribing-safety tables loosened only when two people agree (maker-checker): whoever keeps the tables
/// (<c>clinical.templates.manage</c>) asks, with a reason, and another who holds <see cref="ClinicalPermissions.DrugSafetyApprove"/>
/// (the pharmacy and therapeutics committee: pharmacists and hospital administrators by default) decides, never the one
/// who asked. The row stays as it is until then; it is changed only if it is still as it was asked against
/// (<c>approval.stale</c>), and the change is audited with both names. Tightening is saved at once
/// (<see cref="DrugSafetyChanges"/>).
/// </summary>
public sealed class DrugSafetyApprovals(HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals)
{
    public const string InteractionKind = "clinical.drug_interaction";
    public const string DoseLimitKind = "clinical.dose_limit";

    public async Task<Result<ApprovalRequestDto>> AskAsync(DrugInteraction row, InteractionState after, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        var before = InteractionState.Of(row);
        List<ApprovalDetail> details =
        [
            new("Interaction", $"{before.DrugA} + {before.DrugB}"),
            new("Now", before.Words()),
            new("Asked for", after.Words()),
            new("What it changes", DrugSafetyChanges.Consequences(before, after)),
        ];
        if (!string.Equals(before.Effect, after.Effect, StringComparison.Ordinal) || !string.Equals(before.Management, after.Management, StringComparison.Ordinal))
        {
            details.Add(new("Effect and advice asked for", $"{after.Effect}{(after.Management is null ? "" : " — " + after.Management)}"));
        }

        return await approvals.RequestAsync(
            InteractionKind,
            new ApprovalSubject(nameof(DrugInteraction), row.Id, row.Version, await FacilityAsync(ct), null),
            new DrugInteractionChange(row.Id, before, after),
            $"{before.Words()} → {(after.IsActive ? DrugSafetyChanges.SeverityWords(after.Severity) : "switched off")}",
            details,
            reason,
            ct);
    }

    public async Task<Result<ApprovalRequestDto>> AskAsync(DrugDoseLimit row, DoseLimitState after, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        var before = DoseLimitState.Of(row);
        return await approvals.RequestAsync(
            DoseLimitKind,
            new ApprovalSubject(nameof(DrugDoseLimit), row.Id, row.Version, await FacilityAsync(ct), null),
            new DoseLimitChange(row.Id, before, after),
            $"{before.Words()} → {(after.IsActive ? $"{after.GenericName}, {after.Route ?? "any route"}: at most {DrugSafetyChanges.Mg(after.MaxDailyDoseMg)} a day" : "switched off")}",
            [
                new("Maximum dose", before.GenericName),
                new("Now", before.Words()),
                new("Asked for", after.Words()),
                new("What it changes", DrugSafetyChanges.Consequences(before, after)),
            ],
            reason,
            ct);
    }

    /// <summary>The rows among these with a loosening waiting for approval, and since when.</summary>
    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> WaitingInteractionsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        approvals.WaitingSinceAsync(InteractionKind, nameof(DrugInteraction), ids, ct);

    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> WaitingDoseLimitsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        approvals.WaitingSinceAsync(DoseLimitKind, nameof(DrugDoseLimit), ids, ct);

    internal async Task<Result<string>> ApplyInteractionAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<DrugInteractionChange>(request.Payload);
        var row = await db.Set<DrugInteraction>().FirstOrDefaultAsync(i => i.Id == asked.Id, ct);
        if (row is null)
        {
            return Error.NotFound(nameof(DrugInteraction), asked.Id);
        }

        if (row.Version != request.SubjectVersion || InteractionState.Of(row) != asked.Before)
        {
            return Error.Rule("approval.stale",
                $"The interaction {asked.Before.DrugA} + {asked.Before.DrugB} changed after this was asked for, so nothing was changed. Ask again, for the interaction as it is now.");
        }

        if (await db.Set<DrugInteraction>().AnyAsync(i => i.DrugA == asked.After.DrugA && i.DrugB == asked.After.DrugB && i.Id != row.Id, ct))
        {
            return Error.Conflict("interaction.exists", $"An interaction between {asked.After.DrugA} and {asked.After.DrugB} is already defined.");
        }

        asked.After.ApplyTo(row);
        var words = $"{asked.After.Words()} (was {asked.Before.Words()})";
        audit.Add(Entry("clinical.drug_interaction_changed", nameof(DrugInteraction), row.Id, words, request, new { before = asked.Before, after = asked.After }));
        return words;
    }

    internal async Task<Result<string>> ApplyDoseLimitAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<DoseLimitChange>(request.Payload);
        var row = await db.Set<DrugDoseLimit>().FirstOrDefaultAsync(l => l.Id == asked.Id, ct);
        if (row is null)
        {
            return Error.NotFound("Dose limit", asked.Id);
        }

        if (row.Version != request.SubjectVersion || DoseLimitState.Of(row) != asked.Before)
        {
            return Error.Rule("approval.stale",
                $"The maximum dose of {asked.Before.GenericName} changed after this was asked for, so nothing was changed. Ask again, for the limit as it is now.");
        }

        if (await db.Set<DrugDoseLimit>().AnyAsync(l => l.GenericName == asked.After.GenericName && l.Route == asked.After.Route && l.Id != row.Id, ct))
        {
            return Error.Conflict("dose_limit.exists", $"A limit for {asked.After.GenericName} is already defined for this route.");
        }

        asked.After.ApplyTo(row);
        var words = $"{asked.After.Words()} (was {asked.Before.Words()})";
        audit.Add(Entry("clinical.dose_limit_changed", nameof(DrugDoseLimit), row.Id, words, request, new { before = asked.Before, after = asked.After }));
        return words;
    }

    private AuditEntry Entry(string action, string type, Guid id, string words, ApprovalRequest request, object change) =>
        new(AuditCategories.Clinical, action, Fit($"{words}, asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName}", 1000))
        {
            EntityType = type,
            EntityId = id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { change, approval = request.Id, requestedBy = request.RequestedBy },
        };

    // The tables are the hospital's, not a facility's: the request is kept in the facility it was asked from.
    private async Task<Guid> FacilityAsync(CancellationToken ct) =>
        current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>An interaction switched off or weakened: whoever keeps the tables asks, another on the pharmacy and therapeutics committee decides.</summary>
internal sealed class DrugInteractionApproval(DrugSafetyApprovals tables) : IApprovalAction
{
    public string Kind => DrugSafetyApprovals.InteractionKind;

    public string Title => "Drug interaction loosened";

    public string ApprovePermission => ClinicalPermissions.DrugSafetyApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => tables.ApplyInteractionAsync(request, ct);
}

/// <summary>A maximum dose raised or switched off: whoever keeps the tables asks, another on the pharmacy and therapeutics committee decides.</summary>
internal sealed class DoseLimitApproval(DrugSafetyApprovals tables) : IApprovalAction
{
    public string Kind => DrugSafetyApprovals.DoseLimitKind;

    public string Title => "Maximum dose loosened";

    public string ApprovePermission => ClinicalPermissions.DrugSafetyApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => tables.ApplyDoseLimitAsync(request, ct);
}
