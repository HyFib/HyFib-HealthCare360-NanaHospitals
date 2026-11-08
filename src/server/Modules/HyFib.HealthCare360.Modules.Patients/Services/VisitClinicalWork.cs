namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// The clinical work done on a visit so far, as Clinical keeps it: the encounters (and how many are signed), the notes
/// written in them, the order lines and the e-prescriptions not cancelled, and the doctors who did it. The desk reads it
/// to decide whether cancelling the visit is still a front-office matter (<see cref="VisitService.AskCancelAsync"/>).
/// </summary>
public sealed record VisitClinicalWork(int Encounters, int SignedEncounters, int Notes, int OrderLines, int Prescriptions, IReadOnlyList<Guid> Practitioners)
{
    public static readonly VisitClinicalWork None = new(0, 0, 0, 0, 0, []);

    public bool Any => Encounters + Notes + OrderLines + Prescriptions > 0;

    /// <summary>"1 encounter (not signed), 2 order lines, 1 e-prescription".</summary>
    public string Words()
    {
        var parts = new List<string>();
        if (Encounters > 0)
        {
            var signed = SignedEncounters == Encounters ? "signed" : SignedEncounters == 0 ? "not signed" : $"{SignedEncounters} signed";
            parts.Add($"{Count(Encounters, "encounter")} ({signed})");
        }

        if (Notes > 0)
        {
            parts.Add(Count(Notes, "note"));
        }

        if (OrderLines > 0)
        {
            parts.Add(Count(OrderLines, "order line"));
        }

        if (Prescriptions > 0)
        {
            parts.Add(Count(Prescriptions, "e-prescription"));
        }

        return parts.Count == 0 ? "Nothing recorded yet" : string.Join(", ", parts);
    }

    private static string Count(int n, string thing) => n == 1 ? $"1 {thing}" : $"{n} {thing}s";
}

/// <summary>What the clinical record holds for a visit. Clinical answers; without it (a test host) nothing is recorded.</summary>
public interface IVisitClinicalWork
{
    Task<VisitClinicalWork> OfVisitAsync(Guid visitId, CancellationToken ct);
}

internal sealed class NoVisitClinicalWork : IVisitClinicalWork
{
    public Task<VisitClinicalWork> OfVisitAsync(Guid visitId, CancellationToken ct) => Task.FromResult(VisitClinicalWork.None);
}
