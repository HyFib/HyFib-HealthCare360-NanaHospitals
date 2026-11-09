using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// The doctors who answer for an in-patient stay: its consultant, the admitting doctor, and every doctor who recorded a
/// consultant visit during it (a cross-consultation, a round for a colleague). The Inpatient module keeps the stay and
/// gives this (<see cref="IStayCareTeams"/>).
/// </summary>
public sealed record StayCareTeam(Guid AdmissionId, Guid PatientId, Guid? ConsultantId, IReadOnlySet<Guid> Doctors)
{
    public bool Includes(Guid practitionerId) => ConsultantId == practitionerId || Doctors.Contains(practitionerId);
}

/// <summary>The care team of a stay, from the Inpatient module (which references Clinical, not the other way round).</summary>
public interface IStayCareTeams
{
    /// <summary>The care team of the stay <paramref name="admissionId"/>, or null for an unknown one.</summary>
    Task<StayCareTeam?> OfAdmissionAsync(Guid admissionId, CancellationToken ct);

    /// <summary>The care team of the patient's stay in a bed now (admitted, or with a discharge in progress), or null.</summary>
    Task<StayCareTeam?> CurrentStayAsync(Guid patientId, CancellationToken ct);
}

/// <summary>
/// Who may sign off another doctor's work (maker-checker, slice 15). Each rule is strict by default and relaxed only for
/// the whole hospital or a facility through its setting, which a second administrator approves (SettingGuard):
/// <list type="bullet">
/// <item>a discharge summary is signed by the stay's consultant or a doctor on its care team, as themselves
/// (<see cref="DischargeSummaryCareTeamSetting"/>);</item>
/// <item>another doctor's signed encounter is amended by its author, or by another doctor with the author's or the head of
/// the department's approval (<see cref="EncounterAmendByAuthorSetting"/>);</item>
/// <item>another doctor's critical result is acknowledged by a doctor covering for them: on the same stay's care team, or
/// with a recorded handover (<see cref="CriticalResultCoveringSetting"/>).</item>
/// </list>
/// </summary>
public static class ClinicalSignOff
{
    /// <summary>On (the default): only the stay's consultant or a doctor on its care team signs its discharge summary.</summary>
    public const string DischargeSummaryCareTeamSetting = "clinical.dischargeSummary.careTeamOnly";

    /// <summary>On (the default): a signed encounter is amended at once only by its author; anyone else asks.</summary>
    public const string EncounterAmendByAuthorSetting = "clinical.encounters.amendByAuthorOnly";

    /// <summary>On (the default): another doctor's critical result is acknowledged only by a covering doctor.</summary>
    public const string CriticalResultCoveringSetting = "clinical.criticalResults.coveringOnly";

    /// <summary>Whether <paramref name="signer"/> may sign the discharge summary of a stay with <paramref name="team"/>.</summary>
    public static bool MaySignDischargeSummary(Guid? signer, StayCareTeam? team) =>
        signer is { } doctor && team is not null && team.Includes(doctor);

    /// <summary>
    /// Whether amending a signed encounter by <paramref name="amender"/> needs another doctor's approval: anyone but its
    /// author (the doctor it was written by and signed as, or the account that signed it) asks.
    /// </summary>
    public static bool AmendNeedsApproval(Encounter encounter, Guid? amenderUserId, Guid? amenderPractitionerId)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        return !IsAuthor(encounter, amenderUserId, amenderPractitionerId);
    }

    public static bool IsAuthor(Encounter encounter, Guid? userId, Guid? practitionerId)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        return (userId is { } user && encounter.SignedByUserId == user)
               || (practitionerId is { } doctor && (encounter.SignedByPractitionerId ?? encounter.PractitionerId) == doctor);
    }

    /// <summary>
    /// Whether <paramref name="acknowledger"/> may acknowledge a critical result <paramref name="orderedBy"/> ordered: the
    /// ordering doctor themselves, any doctor for a result no doctor ordered, a doctor on the care team of the patient's stay
    /// when the ordering doctor is on it too, or a doctor the ordering doctor handed this patient over to.
    /// </summary>
    public static bool MayAcknowledgeCritical(
        Guid? orderedBy, Guid? acknowledger, Guid patientId, StayCareTeam? stay, IEnumerable<DoctorHandover> handovers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(handovers);
        if (orderedBy is not { } ordering)
        {
            return acknowledger is not null;
        }

        if (acknowledger is not { } doctor)
        {
            return false;
        }

        if (doctor == ordering)
        {
            return true;
        }

        if (stay is not null && stay.PatientId == patientId && stay.Includes(ordering) && stay.Includes(doctor))
        {
            return true;
        }

        return handovers.Any(h => h.FromPractitionerId == ordering && h.ToPractitionerId == doctor && (h.PatientId is null || h.PatientId == patientId)
                                  && h.CoversAt(now));
    }
}

/// <summary>An amendment another doctor asked for, as asked.</summary>
public sealed record EncounterAmendPayload(Guid EncounterId, Features.Encounters.AmendEncounterRequest Request);

/// <summary>An amendment: made at once (<paramref name="Amended"/>), or asked for and waiting (<paramref name="Asked"/>).</summary>
public sealed record EncounterAmendOutcome(Features.Encounters.EncounterDetail? Amended, ApprovalRequestDto? Asked);

/// <summary>What an amendment changes, in words, for the approver.</summary>
public static class EncounterAmendments
{
    public static IReadOnlyList<string> Changed(EncounterContent from, EncounterContent to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        List<string> changed = [];
        void Check(string name, object? a, object? b)
        {
            if (!Equals(a is string sa ? sa.Trim() : a, b is string sb ? sb.Trim() : b))
            {
                changed.Add(name);
            }
        }

        Check("chief complaint", from.ChiefComplaint, to.ChiefComplaint);
        Check("history", from.HistoryOfPresentIllness, to.HistoryOfPresentIllness);
        Check("examination", from.Examination, to.Examination);
        Check("assessment", from.Assessment, to.Assessment);
        Check("plan", from.Plan, to.Plan);
        Check("follow-up date", from.FollowUpDate, to.FollowUpDate);
        Check("follow-up advice", from.FollowUpAdvice, to.FollowUpAdvice);
        Check("referral", (from.ReferralDepartmentId, from.ReferralPractitionerId, from.ReferralExternal, from.ReferralReason),
            (to.ReferralDepartmentId, to.ReferralPractitionerId, to.ReferralExternal, to.ReferralReason));
        return changed;
    }
}

/// <summary>Another doctor's amendment of a signed encounter: decided by its author or the head of its department (slice 15).</summary>
internal sealed class EncounterAmendApproval(EncounterService encounters) : IApprovalAction
{
    public string Kind => EncounterService.AmendKind;

    public string Title => "Another doctor's signed encounter amended";

    public string ApprovePermission => ClinicalPermissions.EncountersSign;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => encounters.ApplyAmendAsync(request, ct);
}
