using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Queue;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// What the visit lists, a visit's details and the OPD queue show of a patient whose chart the user may not open
/// (<see cref="PatientAccessGuard.WithheldChartsAsync"/>), as the doctor's worklist shows it:
/// <list type="bullet">
/// <item>a restricted (VIP) record is named by its initials, "R. K. S. (restricted)", as patient search names it, so the desk
/// can tell two patients apart;</item>
/// <item>a record in a sensitive category the user does not hold (HIV, mental health, MTP, sexual violence) keeps its name,
/// as on the bed board, so the patient is still called and served.</item>
/// </list>
/// Either way the visit's clinical text is left out (the complaint, the notes, the MLC number), as the chart would refuse
/// to show it. The token, the UHID, the age and the sex stay. Nothing says why: the desk is not told the categories.
/// </summary>
internal static class VisitPrivacy
{
    /// <summary>The name a list shows: initials for a restricted record, the full name otherwise.</summary>
    public static string Name(string fullName, ChartWithholding? withheld) =>
        withheld == ChartWithholding.Restricted ? PatientNames.Restricted(fullName) : fullName;

    /// <summary>A queue entry whose chart is closed to the user (<paramref name="withheld"/> says why).</summary>
    public static QueueEntry Mask(QueueEntry entry, ChartWithholding withheld) =>
        entry with { PatientName = Name(entry.PatientName, withheld), ChiefComplaint = null };

    /// <summary>A visit whose patient's chart is closed to the user, though the patient's record opens to them.</summary>
    public static VisitDetail Withhold(VisitDetail visit) => visit with { ChiefComplaint = null, Notes = null, MlcNumber = null };
}
