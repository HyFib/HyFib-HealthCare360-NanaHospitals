using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// The automatic charges billing refused (<see cref="ChargeRequest.Automatic"/>: a bed's day, a nursing day, a
/// consultant's visit). Their modules post them again on each run, and a refusal (the bed's service taken
/// out of use, the patient merged into another record) used to be a log line while the day went unbilled. Each is now
/// kept until its line posts or its module cancels it (or its stay is cancelled): the first refusal and the clearing are
/// audited, System health warns while one stands, and a stay is neither final-billed nor cleared for discharge over one.
/// </summary>
public sealed class ChargeRefusals(HospitalDbContext db, IAuditLogger audit, TimeProvider time)
{
    /// <summary>The final bill refused, and the System health warning, while an automatic charge of a stay stands refused.</summary>
    public const string RefusedCode = "billing.charges_refused";

    /// <summary>The refusals named in a message; the rest are counted.</summary>
    private const int Named = 3;

    /// <summary>Keeps the refusal of an automatic charge, or counts it again (staged: the caller's save keeps it).</summary>
    internal async Task RefusedAsync(ChargeRequest request, Error error, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var refusal = await FindAsync(request.SourceModule, request.SourceReference, ct);
        if (refusal is null)
        {
            refusal = new ChargeRefusal
            {
                SourceModule = request.SourceModule.Trim(),
                SourceReference = request.SourceReference.Trim(),
                PatientId = request.PatientId,
                VisitId = request.VisitId,
                AdmissionId = request.AdmissionId,
                ServiceItemId = request.ServiceItemId,
                Description = Fit(request.Description ?? request.SourceModule, 300),
                ServiceDate = request.ServiceDate,
                FirstRefusedAt = now,
            };
            db.Add(refusal);
            audit.Add(new AuditEntry(AuditCategories.Data, "billing.charge_refused",
                Fit($"{refusal.Description} was not charged, and will go unbilled until it is: {error.Message}", 1000))
            {
                EntityType = nameof(ChargeRefusal),
                EntityId = refusal.Id.ToString(),
                PatientId = request.PatientId,
                Severity = AuditSeverity.Warning,
                Details = new { source = refusal.SourceModule, reference = refusal.SourceReference, code = error.Code, admission = request.AdmissionId },
            });
        }

        refusal.Refused(error.Code, error.Message, now);
    }

    /// <summary>Clears the refusal of a line that posted, or that its module cancelled (staged).</summary>
    internal async Task ClearedAsync(string sourceModule, string sourceReference, bool posted, CancellationToken ct)
    {
        if (await FindAsync(sourceModule, sourceReference, ct) is { } refusal)
        {
            Clear(refusal, posted ? "was charged" : "was cancelled by its module", posted);
        }
    }

    /// <summary>
    /// Clears the refusals of a stay cancelled as made in error (staged): nothing of it is owed by itself, and its module
    /// no longer posts its days.
    /// </summary>
    internal async Task StayCancelledAsync(Guid admissionId, CancellationToken ct)
    {
        foreach (var refusal in await db.Set<ChargeRefusal>().Where(r => r.AdmissionId == admissionId).ToListAsync(ct))
        {
            Clear(refusal, "is not owed: its stay was cancelled", posted: false);
        }
    }

    /// <summary>The automatic charges of a stay that stand refused, oldest first.</summary>
    public async Task<IReadOnlyList<ChargeRefusal>> ForAdmissionAsync(Guid admissionId, CancellationToken ct) =>
        await db.Set<ChargeRefusal>().AsNoTracking()
            .Where(r => r.AdmissionId == admissionId)
            .OrderBy(r => r.ServiceDate).ThenBy(r => r.FirstRefusedAt)
            .ToListAsync(ct);

    /// <summary>The refusal of a stay's final bill while its automatic charges stand refused, naming them.</summary>
    public static Error FinalBillHeld(IReadOnlyList<ChargeRefusal> refused)
    {
        ArgumentNullException.ThrowIfNull(refused);
        return Error.Rule(RefusedCode,
            $"Billing refused {Count(refused.Count)} automatic charge(s) of this stay, which would go unbilled: {Describe(refused)}. " +
            "Fix their cause (a service out of use, say); they are charged within the hour, then refresh the final bill and finalize it.");
    }

    /// <summary>The refusals as a message names them: what, on which day, and why.</summary>
    internal static string Describe(IReadOnlyList<ChargeRefusal> refused)
    {
        var named = refused.Take(Named).Select(r => $"{What(r)} ({r.Message})");
        return string.Join("; ", named) + (refused.Count > Named ? $"; and {Count(refused.Count - Named)} more" : string.Empty);
    }

    /// <summary>The charge in words, with its day unless its description already says it ("Bed charges for 01-11-2026").</summary>
    private static string What(ChargeRefusal r) =>
        r.ServiceDate is { } at && !r.Description.Contains(Date(at), StringComparison.Ordinal) ? $"{r.Description} on {Date(at)}" : r.Description;

    private async Task<ChargeRefusal?> FindAsync(string sourceModule, string sourceReference, CancellationToken ct)
    {
        var module = sourceModule.Trim();
        var reference = sourceReference.Trim();
        return db.Set<ChargeRefusal>().Local.FirstOrDefault(r => r.SourceModule == module && r.SourceReference == reference)
               ?? await db.Set<ChargeRefusal>().FirstOrDefaultAsync(r => r.SourceModule == module && r.SourceReference == reference, ct);
    }

    private void Clear(ChargeRefusal refusal, string outcome, bool posted)
    {
        db.Remove(refusal);
        audit.Add(new AuditEntry(AuditCategories.Data, "billing.charge_refusal_cleared",
            Fit($"{refusal.Description} {outcome} after {Count(refusal.Attempts)} refusal(s) since {Date(refusal.FirstRefusedAt)}", 1000))
        {
            EntityType = nameof(ChargeRefusal),
            EntityId = refusal.Id.ToString(),
            PatientId = refusal.PatientId,
            Details = new { source = refusal.SourceModule, reference = refusal.SourceReference, posted },
        });
    }

    private static string Date(DateTimeOffset at) => BillingTime.LocalDate(at).ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

    private static string Count(int count) => count.ToString("N0", CultureInfo.InvariantCulture);

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>
/// System health warns while an automatic charge of a stay stands refused (<see cref="ChargeRefusals"/>): the day goes
/// unbilled until its cause is fixed, and the stay's final bill and discharge wait for it. A stay that ended (by
/// absconding or death, its days posted after) is named too; a cancelled one owes nothing (its refusals are cleared).
/// </summary>
internal sealed class ChargeRefusalWarnings(HospitalDbContext db) : IHealthWarningSource
{
    public async Task<IReadOnlyList<HealthWarning>> WarningsAsync(CancellationToken cancellationToken)
    {
        var refused = await (
                from r in db.Set<ChargeRefusal>().AsNoTracking()
                join a in db.Set<IpAccount>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking() on r.AdmissionId equals (Guid?)a.AdmissionId
                where a.Status != IpAccountStatus.Cancelled
                orderby r.FirstRefusedAt
                select new { Refusal = r, a.AdmissionNumber })
            .ToListAsync(cancellationToken);
        if (refused.Count == 0)
        {
            return [];
        }

        var stays = refused.Select(r => r.AdmissionNumber).Distinct().ToList();
        return
        [
            new HealthWarning(ChargeRefusals.RefusedCode, "warning",
                $"Billing refused {refused.Count.ToString("N0", CultureInfo.InvariantCulture)} automatic charge(s) of {stays.Count.ToString("N0", CultureInfo.InvariantCulture)} " +
                $"stay(s) ({string.Join(", ", stays.Take(5))}{(stays.Count > 5 ? ", …" : string.Empty)}), which go unbilled until their cause is fixed: " +
                $"{ChargeRefusals.Describe([.. refused.Select(r => r.Refusal)])}. Their final bills and discharges wait for them."),
        ];
    }
}
