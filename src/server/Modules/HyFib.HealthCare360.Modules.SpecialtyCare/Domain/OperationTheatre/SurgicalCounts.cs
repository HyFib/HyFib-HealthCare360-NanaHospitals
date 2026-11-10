using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

/// <summary>What is counted in the theatre so that nothing is left inside the patient (WHO Sign-out; NABH COP).</summary>
public enum CountCategory
{
    Swabs = 0,
    Needles,
    Instruments,
}

/// <summary>When a count is made: before the incision, when more are opened during the operation, and before closure.</summary>
public enum CountStage
{
    Initial = 0,
    Additional,
    Final,
}

/// <summary>How a count that did not tally was settled.</summary>
public enum DiscrepancyResolution
{
    /// <summary>The missing item was found and accounted for (where is noted).</summary>
    Found = 0,

    /// <summary>An X-ray was taken and showed no retained item (its result is noted).</summary>
    XrayNoRetainedItem,

    /// <summary>The item is retained in the patient: a never event, raised in the Quality incident register.</summary>
    RetainedItem,
}

/// <summary>
/// One count of one category, made by one member of the theatre team from their own sign-in (the scrub or the circulating
/// nurse) and witnessed by the other at the same screen (<c>POST /api/v1/auth/witness</c>, purpose <c>ot.count</c>). The
/// number is what was counted (initial, final) or what was added to the field (additional).
/// </summary>
public sealed record SurgicalCount(
    Guid Id,
    CountCategory Category,
    CountStage Stage,
    int Number,
    Guid? CountedByUserId,
    string CountedByName,
    Guid WitnessUserId,
    string WitnessName,
    DateTimeOffset At,
    string? Remarks);

/// <summary>
/// A final count that did not tally with the initial and additional counts. It is open until it is settled (found, an
/// X-ray with its result, or a retained item raised as an incident), and the WHO Sign-out does not complete while it is.
/// </summary>
public sealed record CountDiscrepancy(
    Guid Id,
    CountCategory Category,
    int Expected,
    int Counted,
    Guid FinalCountId,
    DateTimeOffset OpenedAt,
    DiscrepancyResolution? Resolution = null,
    string? ResolutionNote = null,
    string? XrayResult = null,
    Guid? IncidentId = null,
    string? IncidentNumber = null,
    Guid? ResolvedByUserId = null,
    string? ResolvedByName = null,
    DateTimeOffset? ResolvedAt = null)
{
    public bool IsOpen => Resolution is null;
}

/// <summary>A category's counts so far: what is expected back at the end, and whether the final count tallies.</summary>
public sealed record CountTally(CountCategory Category, int? Initial, int Added, int? Final)
{
    public int? Expected => Initial is { } initial ? initial + Added : null;

    public bool IsFinal => Final is not null;

    public bool Tallies => Final is { } final && Expected == final;
}

/// <summary>
/// The rules of the swab, needle and instrument count (maker-checker slice 10): each count is made by two members of the
/// team; a final count that does not tally opens a discrepancy that must be settled before the WHO Sign-out.
/// </summary>
public static class SurgicalCounts
{
    /// <summary>The most a single count may say: a count larger than this is a typing error.</summary>
    public const int MaxNumber = 9_999;

    public static string Describe(CountCategory category) => category switch
    {
        CountCategory.Swabs => "swabs",
        CountCategory.Needles => "needles",
        _ => "instruments",
    };

    public static string Describe(CountStage stage) => stage switch
    {
        CountStage.Initial => "initial",
        CountStage.Additional => "additional",
        _ => "final",
    };

    public static string Describe(DiscrepancyResolution resolution) => resolution switch
    {
        DiscrepancyResolution.Found => "found",
        DiscrepancyResolution.XrayNoRetainedItem => "X-ray taken, no retained item",
        _ => "retained item",
    };

    /// <summary>Each category counted so far, in the order of <see cref="CountCategory"/>.</summary>
    public static IReadOnlyList<CountTally> Tally(IReadOnlyCollection<SurgicalCount> counts) =>
        Enum.GetValues<CountCategory>()
            .Where(c => counts.Any(n => n.Category == c))
            .Select(c => new CountTally(
                c,
                counts.FirstOrDefault(n => n.Category == c && n.Stage == CountStage.Initial)?.Number,
                counts.Where(n => n.Category == c && n.Stage == CountStage.Additional).Sum(n => n.Number),
                counts.FirstOrDefault(n => n.Category == c && n.Stage == CountStage.Final)?.Number))
            .ToList();

    /// <summary>
    /// Whether a count may be added now. Counts are kept from the day of surgery until the Sign-out: the initial count before
    /// the incision (or when a category is first opened), more added during the operation, and the final count once surgery
    /// has started. A category is counted initially once and finally once.
    /// </summary>
    public static Result CanRecord(SurgeryStatus status, bool signedOut, IReadOnlyCollection<SurgicalCount> counts, CountCategory category, CountStage stage, int number)
    {
        if (signedOut || status is not (SurgeryStatus.Scheduled or SurgeryStatus.InPreOp or SurgeryStatus.InProgress))
        {
            return Error.Rule("surgery.counts_closed", "Counts are recorded on the day of surgery, until the WHO Sign-out.");
        }

        if (number is < 0 or > MaxNumber || (stage == CountStage.Additional && number == 0))
        {
            return Error.Field("number", stage == CountStage.Additional
                ? $"Enter how many {Describe(category)} were added (1 to {MaxNumber})."
                : $"Enter the number of {Describe(category)} counted (0 to {MaxNumber}).");
        }

        var initial = counts.Any(c => c.Category == category && c.Stage == CountStage.Initial);
        var final = counts.Any(c => c.Category == category && c.Stage == CountStage.Final);
        return stage switch
        {
            CountStage.Initial when initial =>
                Error.Rule("surgery.count_recorded", $"The initial count of {Describe(category)} is already recorded; add more as an additional count."),
            CountStage.Initial => Result.Success(),
            _ when !initial => Error.Rule("surgery.count_no_initial", $"Record the initial count of {Describe(category)} first."),
            _ when final => Error.Rule("surgery.count_recorded", $"The final count of {Describe(category)} is already recorded."),
            CountStage.Final when status != SurgeryStatus.InProgress =>
                Error.Rule("surgery.count_not_started", "The final count is made before closure, once surgery has started."),
            _ => Result.Success(),
        };
    }

    /// <summary>
    /// What the WHO Sign-out's "counts are correct" may say. Confirmed only when something was counted, every category
    /// counted at the start was counted at the end, and no discrepancy is open; "not applicable" only when nothing was
    /// counted (a procedure with no swabs, needles or instruments to count).
    /// </summary>
    public static Result CanConfirm(ChecklistItemState state, IReadOnlyCollection<SurgicalCount> counts, IReadOnlyCollection<CountDiscrepancy> discrepancies)
    {
        if (discrepancies.FirstOrDefault(d => d.IsOpen) is { } open)
        {
            return Error.Rule("surgery.count_discrepancy_open",
                $"The final count of {Describe(open.Category)} did not tally ({open.Counted} counted, {open.Expected} expected). Settle it (found, X-ray, or a retained item reported) before the Sign-out.");
        }

        if (state == ChecklistItemState.NotApplicable)
        {
            return counts.Count == 0
                ? Result.Success()
                : Error.Rule("surgery.counts_recorded", "Counts were recorded for this operation: confirm them, not \"not applicable\".");
        }

        if (state != ChecklistItemState.Done)
        {
            return Result.Success();
        }

        if (counts.Count == 0)
        {
            return Error.Rule("surgery.counts_missing",
                "Record the swab, needle and instrument counts, each with a second member of staff, before confirming them (or mark them not applicable, with the reason, when nothing was counted).");
        }

        var unfinished = Tally(counts).Where(t => !t.IsFinal).Select(t => Describe(t.Category)).ToList();
        return unfinished.Count == 0
            ? Result.Success()
            : Error.Rule("surgery.counts_incomplete", $"Record the final count of {string.Join(" and ", unfinished)}, with a second member of staff, first.");
    }
}
