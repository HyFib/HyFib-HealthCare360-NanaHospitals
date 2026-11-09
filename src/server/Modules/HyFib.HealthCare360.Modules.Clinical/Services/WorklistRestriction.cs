using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Patients on the worklist whose chart the user may not open (<see cref="PatientAccessGuard.WithheldChartsAsync"/>):
/// <list type="bullet">
/// <item>a restricted (VIP) record appears as "Restricted patient", as on the bed board and the ward round;</item>
/// <item>a record in a sensitive category the user does not hold (HIV, mental health, MTP, sexual violence) keeps its
/// name, as the bed board does, marked confidential.</item>
/// </list>
/// Either way the clinical text (complaint, booking reason, note title, result name, deficiency) is left out, as the
/// chart would refuse to show it. The token, time, UHID and safety flags stay, so the work is still seen; opening the
/// chart asks for break-the-glass.
/// </summary>
internal static class WorklistRestriction
{
    public const string RestrictedName = "Restricted patient";
    public const string RestrictedText = "Restricted record";
    public const string ConfidentialText = "Confidential record";

    public static IReadOnlyCollection<Guid> PatientIds(EmrWorklist worklist) =>
    [
        .. worklist.Queue.Select(x => x.PatientId)
            .Concat(worklist.Appointments.Select(x => x.PatientId))
            .Concat(worklist.OpenEncounters.Select(x => x.PatientId))
            .Concat(worklist.UnsignedNotes.Select(x => x.PatientId))
            .Concat(worklist.DraftPrescriptions.Select(x => x.PatientId))
            .Concat(worklist.ResultsToReview.Select(x => x.PatientId))
            .Concat(worklist.ChartDeficiencies.Select(x => x.PatientId))
            .Distinct(),
    ];

    /// <summary>Masks the VIP records in <paramref name="restricted"/> (the worklist before sensitive categories were withheld).</summary>
    public static EmrWorklist Mask(EmrWorklist worklist, IReadOnlySet<Guid> restricted) =>
        Mask(worklist, restricted.ToDictionary(id => id, _ => ChartWithholding.Restricted));

    public static EmrWorklist Mask(EmrWorklist worklist, IReadOnlyDictionary<Guid, ChartWithholding> withheld)
    {
        bool Vip(Guid id) => withheld.TryGetValue(id, out var why) && why == ChartWithholding.Restricted;
        bool Confidential(Guid id) => withheld.TryGetValue(id, out var why) && why == ChartWithholding.Confidential;
        string Name(Guid id, string name) => Vip(id) ? RestrictedName : name;
        string Text(Guid id) => Vip(id) ? RestrictedText : ConfidentialText;

        return worklist with
        {
            Queue = [.. worklist.Queue.Select(x => withheld.ContainsKey(x.PatientId)
                ? x with { PatientName = Name(x.PatientId, x.PatientName), ChiefComplaint = null, IsRestricted = Vip(x.PatientId), IsConfidential = Confidential(x.PatientId) }
                : x)],
            Appointments = [.. worklist.Appointments.Select(x => withheld.ContainsKey(x.PatientId)
                ? x with { PatientName = Name(x.PatientId, x.PatientName), Reason = null, IsRestricted = Vip(x.PatientId), IsConfidential = Confidential(x.PatientId) }
                : x)],
            OpenEncounters = [.. worklist.OpenEncounters.Select(x => withheld.ContainsKey(x.PatientId)
                ? x with { PatientName = Name(x.PatientId, x.PatientName), ChiefComplaint = null }
                : x)],
            UnsignedNotes = [.. worklist.UnsignedNotes.Select(x => withheld.ContainsKey(x.PatientId)
                ? x with { PatientName = Name(x.PatientId, x.PatientName), Title = null }
                : x)],
            DraftPrescriptions = [.. worklist.DraftPrescriptions.Select(x => withheld.ContainsKey(x.PatientId)
                ? x with { PatientName = Name(x.PatientId, x.PatientName) }
                : x)],
            ResultsToReview = [.. worklist.ResultsToReview.Select(x => withheld.ContainsKey(x.PatientId)
                ? x with { PatientName = Name(x.PatientId, x.PatientName), Title = Text(x.PatientId) }
                : x)],
            ChartDeficiencies = [.. worklist.ChartDeficiencies.Select(x => withheld.ContainsKey(x.PatientId)
                ? x with { PatientName = Name(x.PatientId, x.PatientName), Description = Text(x.PatientId) }
                : x)],
        };
    }
}
