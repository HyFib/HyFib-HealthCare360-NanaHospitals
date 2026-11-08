using System.Text;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.DoctorFees;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Domain.Statutory;
using HyFib.HealthCare360.Modules.HR.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// TDS on doctors' fees (s.194J): each doctor's PAN and lower-deduction certificate, the tax on a payout (threshold for the
/// year, 10 %, 20 % without a PAN), the quarterly Form 26Q as the FVU input file, and a statement per doctor and quarter to
/// check the TRACES Form 16A against. The deductor's TAN and the challans are HR's statutory details, shared with Form 24Q.
/// </summary>
public sealed class DoctorTaxService(HospitalDbContext db, IFieldProtector protector, IAuditLogger audit, TimeProvider time)
{
    public async Task<DoctorTaxProfileDto> ProfileAsync(Guid practitionerId, CancellationToken ct)
    {
        var p = await db.Set<DoctorTaxProfile>().AsNoTracking().FirstOrDefaultAsync(x => x.PractitionerId == practitionerId, ct);
        return p is null
            ? new DoctorTaxProfileDto(practitionerId, null, null, null, null, null, null, null)
            : new DoctorTaxProfileDto(practitionerId, p.PanMasked, p.NameAsPerPan, p.CertificateNumber, p.CertificateRate, p.CertificateValidFrom, p.CertificateValidTo,
                p.Version);
    }

    public async Task<Result> SaveProfileAsync(Guid practitionerId, SaveDoctorTaxProfileRequest r, CancellationToken ct)
    {
        if (!await db.Set<Practitioner>().AnyAsync(p => p.Id == practitionerId, ct))
        {
            return Error.NotFound(nameof(Practitioner), practitionerId);
        }

        var p = await db.Set<DoctorTaxProfile>().FirstOrDefaultAsync(x => x.PractitionerId == practitionerId, ct);
        if (p is null)
        {
            p = new DoctorTaxProfile { PractitionerId = practitionerId };
            db.Add(p);
        }

        db.SetExpectedVersion(p, r.Version);
        if (r.Pan is not null)
        {
            var pan = r.Pan.Trim();
            p.PanCipher = pan.Length == 0 ? null : protector.Protect(HrIdentifiers.Normalize(pan));
            p.PanMasked = pan.Length == 0 ? null : HrIdentifiers.MaskPan(pan);
        }

        p.NameAsPerPan = string.IsNullOrWhiteSpace(r.NameAsPerPan) ? null : r.NameAsPerPan.Trim();
        p.CertificateNumber = string.IsNullOrWhiteSpace(r.CertificateNumber) ? null : r.CertificateNumber.Trim().ToUpperInvariant();
        p.CertificateRate = p.CertificateNumber is null ? null : r.CertificateRate;
        p.CertificateValidFrom = p.CertificateNumber is null ? null : r.CertificateValidFrom;
        p.CertificateValidTo = p.CertificateNumber is null ? null : r.CertificateValidTo;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// The TDS on a payout of <paramref name="gross"/> credited on <paramref name="creditedOn"/>: against the fees already
    /// credited to the doctor in the financial year and the part of them already taxed.
    /// </summary>
    /// <param name="rate">The facility's s.194J rate, if set (else the year's).</param>
    /// <param name="typedRate">A lower rate typed for this payout (a s.197 certificate not yet on the profile).</param>
    public async Task<Result<ProfessionalFeeTdsResult>> PayoutTdsAsync(Guid practitionerId, decimal gross, decimal? rate, decimal? typedRate, DateOnly creditedOn,
        CancellationToken ct)
    {
        var fy = IncomeTaxRates.FinancialYearOf(creditedOn);
        if (ProfessionalFeeTds.Find(fy) is not { } rates)
        {
            return Error.Rule("tds.year_unknown", $"TDS rates on professional fees for {fy}-{(fy + 1) % 100:00} are not set up.");
        }

        if (rate is { } facilityRate)
        {
            rates = rates with { Rate = Math.Clamp(facilityRate, 0, 100) };
        }

        var (yearStart, yearEnd) = (Start(new DateOnly(fy, 4, 1)), Start(new DateOnly(fy + 1, 4, 1)));
        var earlier = await db.Set<DoctorPayout>().AsNoTracking()
            .Where(p => p.PractitionerId == practitionerId && p.CreatedAt >= yearStart && p.CreatedAt < yearEnd)
            .Select(p => new { p.GrossAmount, p.TdsAmount, p.TdsBasis })
            .ToListAsync(ct);
        var credited = earlier.Sum(p => Math.Max(0, p.GrossAmount));

        // Payouts made before TDS was computed carry no basis: the tax on them was on the gross.
        var taxed = earlier.Sum(p => p.TdsBasis > 0 ? p.TdsBasis : p.TdsAmount > 0 ? p.GrossAmount : 0);
        var profile = await db.Set<DoctorTaxProfile>().AsNoTracking().FirstOrDefaultAsync(x => x.PractitionerId == practitionerId, ct);
        var pan = profile?.PanCipher is { } cipher ? protector.Unprotect(cipher) : null;
        return ProfessionalFeeTds.Compute(rates, credited, taxed, Math.Max(0, gross), pan, typedRate ?? profile?.CertificateRateOn(creditedOn));
    }

    public sealed record Doctor26QResult(string FileName, byte[] Content, TdsReturnResult Return);

    public async Task<Result<Doctor26QResult>> Form26QAsync(int fy, int quarter, CancellationToken ct)
    {
        var profile = await db.Set<StatutoryEmployerProfile>().AsNoTracking().FirstOrDefaultAsync(ct);
        if (profile is null)
        {
            return Error.Rule("statutory.profile_missing", "Enter the hospital's TAN, PAN and the person responsible under HR statutory details first.");
        }

        var credits = await CreditsAsync(null, fy, quarter, ct);
        var (from, to) = TdsReturnFile.QuarterDates(fy, quarter);
        var challans = await ChallansAsync(from, to, ct);
        var problems = new List<string>();
        var deposits = new List<TdsChallanDeposit>();
        foreach (var month in credits.Where(c => c.Tds > 0).GroupBy(c => (c.CreditedOn.Year, c.CreditedOn.Month)))
        {
            var monthChallans = challans.Where(c => c.Year == month.Key.Year && c.Month == month.Key.Month).ToList();
            if (monthChallans.Count == 0)
            {
                problems.Add($"No Form 26Q challan is recorded for TDS on doctors' fees of {month.Key.Month:00}/{month.Key.Year} (₹{Money.FormatInr(month.Sum(c => c.Tds))}).");
                continue;
            }

            var rows = month.Select(c => new TdsDeduction(c.Name, c.Pan, c.Gross, c.CreditedOn, c.CreditedOn, c.Tds, c.Rate, c.Section, c.Remark)).ToList();
            deposits.Add(StatutoryService.Deposit(monthChallans[0], rows));
            deposits.AddRange(monthChallans.Skip(1).Select(c => StatutoryService.Deposit(c, [])));
        }

        var result = TdsReturnFile.Build("26Q", StatutoryService.Deductor(profile), fy, quarter, deposits, BillingTime.Today(time), problems);
        await audit.WriteAsync(new AuditEntry(AuditCategories.Security, "doctorfees.26q_exported",
            $"Form 26Q {fy}-{(fy + 1) % 100:00} {TdsReturnFile.Quarter(quarter)}: {result.Deductees} deduction(s), ₹{Money.FormatInr(result.TotalDeducted)}")
        {
            EntityType = nameof(DoctorPayout),
            Severity = AuditSeverity.Warning,
        }, ct);
        return new Doctor26QResult($"26Q-{fy}-{(fy + 1) % 100:00}-{TdsReturnFile.Quarter(quarter)}.txt", Encoding.UTF8.GetBytes(result.Text), result);
    }

    public async Task<Result<PrintableDocument>> Form16AAsync(Guid practitionerId, int fy, int quarter, CancellationToken ct)
    {
        var profile = await db.Set<StatutoryEmployerProfile>().AsNoTracking().FirstOrDefaultAsync(ct);
        if (profile is null)
        {
            return Error.Rule("statutory.profile_missing", "Enter the hospital's TAN, PAN and the person responsible under HR statutory details first.");
        }

        var credits = await CreditsAsync(practitionerId, fy, quarter, ct);
        if (credits.Count == 0)
        {
            return Error.Rule("tds.no_credits", "No approved payout to this doctor in the quarter.");
        }

        var (from, to) = TdsReturnFile.QuarterDates(fy, quarter);
        var challans = await ChallansAsync(from, to, ct);
        var references = credits.Where(c => c.Tds > 0).GroupBy(c => (c.CreditedOn.Year, c.CreditedOn.Month))
            .SelectMany(m => challans.Where(c => c.Year == m.Key.Year && c.Month == m.Key.Month).Take(1)
                .Select(c => new ChallanReference(c.BsrCode, c.DepositedOn, c.ChallanSerial, m.Sum(x => x.Tds))))
            .ToList();
        var first = credits[0];
        var document = TdsCertificates.Form16AStatement(
            new CertificateParty(profile.DeductorName ?? string.Empty, profile.Pan, profile.Tan, StatutoryService.Address(profile)),
            new CertificateParty(first.Name, first.Pan, null, null), fy, quarter,
            credits.Select(c => new FeePayment(c.CreditedOn, c.Section, c.Gross, c.Tds, c.Reference)).ToList(), references,
            profile.ResponsibleName ?? string.Empty, profile.ResponsibleDesignation ?? string.Empty, BillingTime.Today(time));
        return document;
    }

    private sealed record Credit(string Reference, string Name, string? Pan, DateOnly CreditedOn, decimal Gross, decimal Tds, decimal? Rate, string Section, string? Remark);

    /// <summary>Approved and paid payouts credited (approved) in the quarter, oldest first.</summary>
    private async Task<IReadOnlyList<Credit>> CreditsAsync(Guid? practitionerId, int fy, int quarter, CancellationToken ct)
    {
        var (first, last) = TdsReturnFile.QuarterDates(fy, quarter);
        var (start, end) = (Start(first), Start(last.AddDays(1)));
        var section = ProfessionalFeeTds.Find(fy)?.SectionCode ?? "94J-B";
        var payouts = await (
                from p in db.Set<DoctorPayout>().AsNoTracking()
                join d in db.Set<Practitioner>() on p.PractitionerId equals d.Id
                where (practitionerId == null || p.PractitionerId == practitionerId) && p.Status != PayoutStatus.Draft && p.ApprovedAt != null
                      && p.ApprovedAt >= start && p.ApprovedAt < end
                orderby p.ApprovedAt
                select new { p.PayoutNumber, p.PractitionerId, d.DisplayName, p.ApprovedAt, p.GrossAmount, p.TdsAmount, p.TdsPercent, p.TdsRemark })
            .ToListAsync(ct);
        var ids = payouts.Select(p => p.PractitionerId).Distinct().ToList();
        var profiles = await db.Set<DoctorTaxProfile>().AsNoTracking().Where(x => ids.Contains(x.PractitionerId)).ToDictionaryAsync(x => x.PractitionerId, ct);
        return payouts.Select(p =>
        {
            var profile = profiles.GetValueOrDefault(p.PractitionerId);
            return new Credit(p.PayoutNumber, profile?.NameAsPerPan ?? p.DisplayName, profile?.PanCipher is { } c ? protector.Unprotect(c) : null,
                BillingTime.LocalDate(p.ApprovedAt!.Value), p.GrossAmount, p.TdsAmount, p.TdsAmount > 0 ? p.TdsPercent : null, section, p.TdsRemark);
        }).ToList();
    }

    private Task<List<TdsChallan>> ChallansAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (fromKey, toKey) = ((from.Year * 100) + from.Month, (to.Year * 100) + to.Month);
        return db.Set<TdsChallan>().AsNoTracking()
            .Where(c => c.Form == TdsForm.Form26Q && (c.Year * 100) + c.Month >= fromKey && (c.Year * 100) + c.Month <= toKey)
            .OrderBy(c => c.Year).ThenBy(c => c.Month).ThenBy(c => c.DepositedOn)
            .ToListAsync(ct);
    }

    /// <summary>The start of a hospital-local day as an instant.</summary>
    private static DateTimeOffset Start(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), HospitalTimeZone.Zone.GetUtcOffset(date.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
}
