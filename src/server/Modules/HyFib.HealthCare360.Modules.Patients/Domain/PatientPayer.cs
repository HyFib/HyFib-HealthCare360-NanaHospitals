namespace HyFib.HealthCare360.Modules.Patients.Domain;

/// <summary>What the payer rule needs to know of a visit.</summary>
public sealed record PayerVisit(Guid VisitId, VisitStatus Status, DateTimeOffset VisitedAt, PayerType PayerType, Guid? PayerId);

/// <summary>
/// Who pays for the patient's care, as the patient banner shows it (UX-04): the "primary payer". The payer is recorded on
/// each visit (and admission), not on the patient, so it is the payer of the patient's open visit (checked in or in
/// consultation), the newest when there are several; else of the newest visit that was not cancelled, shown as the
/// "Last payer"; else none (the patient has had no visit). A self-paying visit's payer is self-pay. Screens that know the
/// payer better, an admission's, show their own.
/// </summary>
public static class PatientPayerRule
{
    /// <summary>The visit whose payer to show, and whether it is open; null when there is none.</summary>
    public static (PayerVisit Visit, bool IsOpen)? Choose(IEnumerable<PayerVisit> visits)
    {
        var newestFirst = visits.OrderByDescending(v => v.VisitedAt).ToList();
        var open = newestFirst.FirstOrDefault(v => v.Status is VisitStatus.Open or VisitStatus.InProgress);
        if (open is not null)
        {
            return (open, true);
        }

        var last = newestFirst.FirstOrDefault(v => v.Status != VisitStatus.Cancelled);
        return last is null ? null : (last, false);
    }
}
