using HyFib.HealthCare360.Modules.Clinical.Features.Emr;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Restricted (VIP) patients on the worklist, for users who may not open restricted records: they appear as
/// "Restricted patient", as on the bed board and the ward round, and their clinical text (complaint, booking reason,
/// note title, result name, deficiency) is left out. The token, time, UHID and safety flags stay, so the work is still
/// seen; opening the chart asks for break-the-glass.
/// </summary>
internal static class WorklistRestriction
{
    public const string RestrictedName = "Restricted patient";
    public const string RestrictedText = "Restricted record";

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

    public static EmrWorklist Mask(EmrWorklist worklist, IReadOnlySet<Guid> restricted) => worklist with
    {
        Queue = [.. worklist.Queue.Select(x => restricted.Contains(x.PatientId)
            ? x with { PatientName = RestrictedName, ChiefComplaint = null, IsRestricted = true }
            : x)],
        Appointments = [.. worklist.Appointments.Select(x => restricted.Contains(x.PatientId)
            ? x with { PatientName = RestrictedName, Reason = null, IsRestricted = true }
            : x)],
        OpenEncounters = [.. worklist.OpenEncounters.Select(x => restricted.Contains(x.PatientId)
            ? x with { PatientName = RestrictedName, ChiefComplaint = null }
            : x)],
        UnsignedNotes = [.. worklist.UnsignedNotes.Select(x => restricted.Contains(x.PatientId)
            ? x with { PatientName = RestrictedName, Title = null }
            : x)],
        DraftPrescriptions = [.. worklist.DraftPrescriptions.Select(x => restricted.Contains(x.PatientId)
            ? x with { PatientName = RestrictedName }
            : x)],
        ResultsToReview = [.. worklist.ResultsToReview.Select(x => restricted.Contains(x.PatientId)
            ? x with { PatientName = RestrictedName, Title = RestrictedText }
            : x)],
        ChartDeficiencies = [.. worklist.ChartDeficiencies.Select(x => restricted.Contains(x.PatientId)
            ? x with { PatientName = RestrictedName, Description = RestrictedText }
            : x)],
    };
}
