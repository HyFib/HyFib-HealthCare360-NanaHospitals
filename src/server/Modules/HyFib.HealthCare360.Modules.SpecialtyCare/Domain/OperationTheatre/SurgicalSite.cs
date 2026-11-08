using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

/// <summary>
/// What is to be operated on and where: the procedure, its side and its level (a spinal level, a digit, a tooth). The WHO
/// Sign-in and Time-out confirm exactly this, and site marking shows it on the patient.
/// </summary>
public sealed record SurgicalSite(Guid? ProcedureServiceItemId, string ProcedureName, Laterality Laterality, string? Level)
{
    /// <summary>A side or a level is named, so the site must be marked on the patient before the operation.</summary>
    public bool NeedsMarking => Laterality is Laterality.Left or Laterality.Right or Laterality.Bilateral || !string.IsNullOrWhiteSpace(Level);

    /// <summary>"Inguinal hernia repair, right" or "Discectomy, left, L4–L5".</summary>
    public string Words
    {
        get
        {
            var parts = new List<string> { ProcedureName.Trim() };
            if (Laterality != Laterality.NotApplicable)
            {
                parts.Add(Laterality switch { Laterality.Left => "left", Laterality.Right => "right", _ => "bilateral" });
            }

            if (!string.IsNullOrWhiteSpace(Level))
            {
                parts.Add(Level.Trim());
            }

            return string.Join(", ", parts);
        }
    }

    /// <summary>The procedure differs (another catalogue item, or another name).</summary>
    public bool ProcedureDiffers(SurgicalSite other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ProcedureServiceItemId != other.ProcedureServiceItemId
               || !string.Equals(ProcedureName.Trim(), other.ProcedureName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The place on the patient differs (another side, or another level), so a mark already made is on the wrong place.</summary>
    public bool PlaceDiffers(SurgicalSite other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Laterality != other.Laterality
               || !string.Equals(Level?.Trim() ?? string.Empty, other.Level?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public bool Differs(SurgicalSite other) => ProcedureDiffers(other) || PlaceDiffers(other);
}

/// <summary>
/// The WHO checklist phases voided because the side or procedure was changed after the Sign-in: who changed it, why, from
/// what to what, and the confirmations that no longer stand (kept here as the record of what had been checked).
/// </summary>
public sealed record ChecklistVoid(
    DateTimeOffset At,
    Guid? ByUserId,
    string ByName,
    string Reason,
    string Before,
    string After,
    IReadOnlyList<SafetyPhase> Phases,
    IReadOnlyList<SafetyChecklistEntry> Voided);

/// <summary>
/// Who may change the side or procedure once the WHO Sign-in is done (maker-checker slice 10). Before the Sign-in the plan
/// is edited as usual. After it, only the operating surgeon or the anaesthetist of the case, from their own sign-in, with a
/// reason; the Sign-in (and the Time-out, if done) are then redone. After the incision the side and procedure are not changed:
/// what was found and done is recorded in the operation note.
/// </summary>
public static class SiteChangeRule
{
    public static Result Check(SurgeryStatus status, bool signedIn, SurgicalSite before, SurgicalSite asked, Guid? surgeonId, Guid? anaesthetistId,
        Guid? byPractitionerId, string? reason)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(asked);
        if (!before.Differs(asked))
        {
            return Result.Success();
        }

        if (status == SurgeryStatus.InProgress)
        {
            return Error.Rule("surgery.site_after_incision",
                "The side or procedure is not changed after the incision. Record what was found and what was done in the operation note.");
        }

        if (!signedIn)
        {
            return Result.Success();
        }

        if (byPractitionerId is not { } by || (by != surgeonId && by != anaesthetistId))
        {
            return Error.Forbidden("surgery.site_change_team",
                "After the WHO Sign-in, only the operating surgeon or the anaesthetist of this case changes the side or procedure, from their own sign-in.");
        }

        return string.IsNullOrWhiteSpace(reason)
            ? Error.Field("changeReason", "Give the reason for changing the side or procedure after the WHO Sign-in.")
            : Result.Success();
    }
}

/// <summary>
/// Site marking (maker-checker slice 10). When the booked procedure names a side (left, right, bilateral) or a level, the
/// site is marked on the patient: "site marked" is never "not applicable" then, and the pre-op mark is recorded by the
/// surgeon who made it, from their own sign-in, so the record names who marked it. "Not applicable" stays for a midline
/// incision or a single organ, with the reason.
/// </summary>
public static class SiteMarking
{
    /// <summary>The key of the site-marking item on both the pre-op checklist and the WHO Sign-in.</summary>
    public const string Item = "site_marked";

    private static readonly Error Required = Error.Rule("checklist.site_marking_required",
        "A side or level is booked for this procedure, so the site is marked on the patient: \"site marked\" cannot be not applicable.");

    public static Result CheckPreOp(SurgicalSite site, IReadOnlyList<ChecklistEntry> entries, IReadOnlyList<ChecklistInput> inputs, bool canMarkSite)
    {
        ArgumentNullException.ThrowIfNull(site);
        var input = inputs.FirstOrDefault(i => i.Key == Item);
        if (input is null || !site.NeedsMarking)
        {
            return Result.Success();
        }

        if (input.State == ChecklistItemState.NotApplicable)
        {
            return Required;
        }

        return input.State == ChecklistItemState.Done && !canMarkSite && !IsMarked(entries)
            ? Error.Forbidden("checklist.site_marked_by_surgeon",
                "The site is marked by the operating surgeon (or a surgeon of the team), who records it from their own sign-in, so the record names who marked it.")
            : Result.Success();
    }

    public static Result CheckSignIn(SurgicalSite site, IReadOnlyList<ChecklistEntry> preOp, IReadOnlyList<ChecklistInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(site);
        var input = inputs.FirstOrDefault(i => i.Key == Item);
        if (input is null || !site.NeedsMarking)
        {
            return Result.Success();
        }

        if (input.State == ChecklistItemState.NotApplicable)
        {
            return Required;
        }

        return input.State == ChecklistItemState.Done && !IsMarked(preOp)
            ? Error.Rule("surgery.site_not_marked", $"The site ({site.Words}) is not recorded as marked on the pre-op checklist. The surgeon marks it and records it first.")
            : Result.Success();
    }

    /// <summary>A site mark already recorded is kept when the checklist is sent again whole, so it still names who marked it.</summary>
    public static IReadOnlyList<ChecklistInput> KeepMark(IReadOnlyList<ChecklistEntry> entries, IReadOnlyList<ChecklistInput> inputs) =>
        IsMarked(entries) ? inputs.Where(i => !(i.Key == Item && i.State == ChecklistItemState.Done)).ToList() : inputs;

    private static bool IsMarked(IReadOnlyList<ChecklistEntry> entries) => entries.Any(e => e.Key == Item && e.State == ChecklistItemState.Done);
}
