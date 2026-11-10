using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.EInvoices;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// The record that a GSTIN's GSTR-1 for a month was filed (<see cref="GstReturnFiling"/>), kept by the accountant:
/// marked once, corrected with a reason, never deleted, every step audited. It tells HC360 which months are closed, so a
/// paper bill entered later for one is flagged at finalisation and listed for amendment in the next GSTR-1.
/// </summary>
public sealed class GstFilingService(HospitalDbContext db, ICurrentUser currentUser, IAuditLogger audit, TimeProvider time)
{
    public async Task<Result<Gstr1Filing?>> GetAsync(int year, int month, CancellationToken ct)
    {
        var gstin = await GstinAsync(ct);
        if (gstin.IsFailure)
        {
            return gstin.Error!;
        }

        var filing = await FindAsync(gstin.Value, year, month, ct);
        return filing is null ? (Gstr1Filing?)null : Dto(filing);
    }

    public async Task<Result<Gstr1Filing>> MarkFiledAsync(MarkGstr1FiledRequest request, CancellationToken ct)
    {
        if (Period(request.Month) is not { } period)
        {
            return BadMonth;
        }

        var gstin = await GstinAsync(ct);
        if (gstin.IsFailure)
        {
            return gstin.Error!;
        }

        if (Check(period, request.FiledOn) is { } bad)
        {
            return bad;
        }

        if (await FindAsync(gstin.Value, period.Year, period.Month, ct) is { } existing)
        {
            return Error.Conflict("gst.already_filed",
                $"GSTR-1 for {Words(period)} is already recorded as filed on {existing.FiledOn:d MMM yyyy} (ARN {existing.Arn}). Correct it, with a reason, if it is wrong.");
        }

        var filing = new GstReturnFiling
        {
            Gstin = gstin.Value,
            Year = period.Year,
            Month = period.Month,
            Arn = request.Arn.ToUpperInvariant(),
            FiledOn = request.FiledOn,
            RecordedAt = time.GetUtcNow(),
            RecordedByUserId = currentUser.UserId,
            RecordedByName = currentUser.DisplayName,
        };
        db.Add(filing);
        audit.Add(new AuditEntry(AuditCategories.Data, "billing.gstr1_filed",
            $"GSTR-1 of {filing.Gstin} for {Words(period)} recorded as filed on {filing.FiledOn:d MMM yyyy}, ARN {filing.Arn}")
        {
            EntityType = nameof(GstReturnFiling),
            EntityId = filing.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return Dto(filing);
    }

    public async Task<Result<Gstr1Filing>> CorrectAsync(CorrectGstr1FilingRequest request, CancellationToken ct)
    {
        if (Period(request.Month) is not { } period)
        {
            return BadMonth;
        }

        var gstin = await GstinAsync(ct);
        if (gstin.IsFailure)
        {
            return gstin.Error!;
        }

        if (Check(period, request.FiledOn) is { } bad)
        {
            return bad;
        }

        var filing = await db.Set<GstReturnFiling>()
            .FirstOrDefaultAsync(f => f.Gstin == gstin.Value && f.ReturnType == GstReturnFiling.Gstr1 && f.Year == period.Year && f.Month == period.Month, ct);
        if (filing is null)
        {
            return Error.NotFound("gst.not_filed", $"GSTR-1 for {Words(period)} has not been recorded as filed.");
        }

        db.GuardVersion(filing, request.Version);
        var was = $"ARN {filing.Arn}, filed {filing.FiledOn:d MMM yyyy}";
        filing.Arn = request.Arn.ToUpperInvariant();
        filing.FiledOn = request.FiledOn;
        filing.CorrectedAt = time.GetUtcNow();
        filing.CorrectedByName = currentUser.DisplayName;
        filing.CorrectionReason = request.Reason;
        audit.Add(new AuditEntry(AuditCategories.Data, "billing.gstr1_filing_corrected",
            $"GSTR-1 of {filing.Gstin} for {Words(period)}: {was} corrected to ARN {filing.Arn}, filed {filing.FiledOn:d MMM yyyy} — {request.Reason}")
        {
            EntityType = nameof(GstReturnFiling),
            EntityId = filing.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return Dto(filing);
    }

    internal static Gstr1Filing Dto(GstReturnFiling f) =>
        new(f.Year, f.Month, f.Arn, f.FiledOn, f.RecordedAt, f.RecordedByName, f.CorrectedAt, f.CorrectedByName, f.CorrectionReason, f.Version);

    private static readonly Error BadMonth = Error.Field("month", "Choose the month, as 2026-10 (GST began in July 2017).");

    private Error? Check((int Year, int Month) period, DateOnly filedOn)
    {
        var monthEnd = new DateOnly(period.Year, period.Month, 1).AddMonths(1).AddDays(-1);
        if (filedOn < monthEnd)
        {
            return Error.Field("filedOn", $"GSTR-1 for {Words(period)} cannot have been filed before the month ended ({monthEnd:d MMM yyyy}).");
        }

        return filedOn > BillingTime.Today(time) ? Error.Field("filedOn", "The filing date cannot be in the future.") : null;
    }

    private Task<GstReturnFiling?> FindAsync(string gstin, int year, int month, CancellationToken ct) =>
        db.Set<GstReturnFiling>().AsNoTracking()
            .FirstOrDefaultAsync(f => f.Gstin == gstin && f.ReturnType == GstReturnFiling.Gstr1 && f.Year == year && f.Month == month, ct);

    private async Task<Result<string>> GstinAsync(CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility: GST returns are of its GSTIN.");
        }

        var own = await db.Set<Facility>().AsNoTracking().Where(f => f.Id == facilityId).Select(f => f.Gstin).FirstOrDefaultAsync(ct);
        return Gstin.Normalize(own) is { } gstin && Gstin.IsWellFormed(gstin)
            ? gstin
            : Error.Rule("gst.no_gstin", "Enter the facility's GSTIN under Hospital setup: returns are filed per GSTIN.");
    }

    internal static (int Year, int Month)? Period(string? month) =>
        DateOnly.TryParseExact($"{month}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year >= 2017
            ? (date.Year, date.Month)
            : null;

    internal static string Words((int Year, int Month) period) =>
        new DateOnly(period.Year, period.Month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
}
