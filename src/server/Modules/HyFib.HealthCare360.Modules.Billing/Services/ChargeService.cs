using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Charge capture (MB-01) and the <see cref="IChargePoster"/> contract every clinical and support module uses.
/// <list type="bullet">
/// <item><see cref="PostAsync"/> stages the charge in the caller's unit of work, so it is saved atomically with the order,
/// dispense or bed day that caused it when the caller calls SaveChangesAsync (it also works from a domain-event handler,
/// which must not save).</item>
/// <item>Posting is idempotent on (SourceModule, SourceReference) — against the database, in every facility, and against
/// charges already staged in the same unit of work — and the unique index settles concurrent retries.</item>
/// <item>It works without a signed-in user (the Inpatient bed-day and SupportServices meal-plan jobs): the facility and
/// tariff come from the admission's IP account or the visit; only a charge with neither falls back to the user's
/// facility, then to the patient's registration facility, and is billed on a patient-level OP bill.</item>
/// <item>A negative quantity is a credit — a return such as ward stock sent back to the IP pharmacy
/// (<c>pharmacy.ip.return</c>). It must name its admission (or visit) and can never credit more than was charged for the
/// same service item there, net of earlier returns; bills show it as a return line.</item>
/// <item><see cref="CancelAsync"/> also only stages: it joins the caller's transaction, so a module reversing several
/// charges (admission cancelled) gets all-or-nothing, and it fails with <c>billing.charge_billed</c> when the charge is
/// already on a finalized bill.</item>
/// </list>
/// </summary>
public sealed class ChargeService(HospitalDbContext db, TariffService tariffs, ICurrentUser currentUser, TimeProvider time) : IChargePoster
{
    public const string ManualSource = "billing.manual";
    public const string AdhocSource = "billing.adhoc";
    public const string ConsultationSource = "billing.consultation";
    public const string RegistrationSource = "billing.registration";
    public const string PackageSource = "billing.package";

    /// <summary>Procedures priced by Billing when a clinician orders them (reference = the order item id).</summary>
    public const string OrderSource = "billing.order";

    public async Task<Result<Guid>> PostAsync(ChargeRequest request, CancellationToken cancellationToken)
    {
        var staged = await StageAsync(request, isFreeFollowUp: false, cancellationToken);
        return staged.IsSuccess ? Result<Guid>.Ok(staged.Value.Id) : Result<Guid>.Fail(staged.Error!);
    }

    /// <summary>
    /// Cancels the charge of a source line (order cancelled, dispense returned, admission cancelled). Unknown or
    /// already-cancelled sources succeed, so retries are safe; a charge already on a finalized bill is refused (billing
    /// cancels the bill or issues a credit note instead). Does not save.
    /// </summary>
    public async Task<Result> CancelAsync(string sourceModule, string sourceReference, string reason, CancellationToken cancellationToken)
    {
        var charge = await FindBySourceAsync(sourceModule, sourceReference, cancellationToken);
        return charge is null ? Result.Success() : await CancelChargeAsync(charge, reason, cancellationToken);
    }

    /// <summary>Manual charge from the billing desk (source <c>billing.manual</c>); saved immediately.</summary>
    public async Task<Result<ChargeDto>> PostManualAsync(PostChargeRequest request, CancellationToken ct)
    {
        if (request.UnitPrice is not null
            && await db.Set<ServiceItem>().Where(s => s.Id == request.ServiceItemId).Select(s => (bool?)s.IsPriceEditable).FirstOrDefaultAsync(ct) == false)
        {
            return Error.Field("unitPrice", "The price of this service is fixed by the tariff.");
        }

        var staged = await StageAsync(new ChargeRequest
        {
            PatientId = request.PatientId,
            VisitId = request.VisitId,
            AdmissionId = request.AdmissionId,
            ServiceItemId = request.ServiceItemId,
            Quantity = request.Quantity,
            UnitPriceOverride = request.UnitPrice,
            Description = request.Description,
            SourceModule = ManualSource,
            SourceReference = Guid.CreateVersion7().ToString("N"),
            PractitionerId = request.PractitionerId,
            DepartmentId = request.DepartmentId,
            ServiceDate = request.ServiceDate,
        }, isFreeFollowUp: false, ct);
        if (staged.IsFailure)
        {
            return staged.Error!;
        }

        await db.SaveChangesAsync(ct);
        return ToDto(staged.Value, null);
    }

    /// <summary>Cancels a charge; one reserved by a draft bill is first taken off that draft. Does not save.</summary>
    internal async Task<Result> CancelChargeAsync(Charge charge, string reason, CancellationToken ct)
    {
        if (charge.Status == ChargeStatus.Cancelled)
        {
            return Result.Success();
        }

        var released = await ReleaseFromDraftAsync(charge, ct);
        return released.IsFailure ? released : charge.Cancel(reason.Trim(), time.GetUtcNow());
    }

    /// <summary>
    /// Takes an unbilled charge off the draft bill that reserved it (the draft is recalculated). Fails if the charge is
    /// on a finalized bill. Does not save.
    /// </summary>
    internal async Task<Result> ReleaseFromDraftAsync(Charge charge, CancellationToken ct)
    {
        if (charge.Status == ChargeStatus.Billed)
        {
            return Error.Rule("billing.charge_billed", "This service is on a finalized bill. Cancel the bill or issue a credit note instead.");
        }

        if (charge.Status != ChargeStatus.Unbilled || charge.BillId is not { } billId)
        {
            return Result.Success();
        }

        var bill = db.Set<Bill>().Local.FirstOrDefault(b => b.Id == billId)
            ?? await db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(b => b.Id == billId, ct);
        if (bill is not null && bill.Status != BillStatus.Draft)
        {
            return Error.Rule("billing.charge_billed", "This service is on a finalized bill. Cancel the bill or issue a credit note instead.");
        }

        if (bill is not null)
        {
            var lines = db.Entry(bill).Collection(b => b.Lines);
            if (!lines.IsLoaded && db.Entry(bill).State != EntityState.Added)
            {
                await lines.LoadAsync(ct);
            }

            if (bill.Lines.FirstOrDefault(l => l.ChargeId == charge.Id) is { } line)
            {
                bill.RemoveLine(line.Id);
                if (charge.SourceModule == PackageSource && bill.Type == BillType.OutPatient)
                {
                    // The OP package's own line went, so the bill is no longer billed at the package.
                    bill.UsePackage(null);
                }

                // What the package absorbs depends on every line, so it is worked out again.
                await OpPackageCover.ApplyAsync(db, bill, ct);
            }
        }

        charge.Release();
        return Result.Success();
    }

    /// <summary>
    /// Validates, prices and stages a charge without saving. Returns the existing charge when the source line was
    /// already posted (idempotency). <paramref name="facilityHint"/> is the facility an event reported, used when the
    /// charge has no admission or visit to take it from.
    /// </summary>
    internal async Task<Result<Charge>> StageAsync(ChargeRequest request, bool isFreeFollowUp, CancellationToken ct, Guid? facilityHint = null)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var existing = await FindBySourceAsync(request.SourceModule, request.SourceReference, ct);
        if (existing is not null)
        {
            return existing;
        }

        var patient = await PatientAsync(request.PatientId, ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), request.PatientId);
        }

        if (patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.merged", "This record was merged into another UHID. Charge the surviving record.");
        }

        var item = await db.Set<ServiceItem>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.ServiceItemId, ct);
        if (item is null)
        {
            return Error.NotFound(nameof(ServiceItem), request.ServiceItemId);
        }

        if (!item.IsActive)
        {
            return Error.Rule("billing.service_inactive", $"{item.Name} is no longer offered.");
        }

        var context = await ResolveContextAsync(request, facilityHint ?? currentUser.FacilityId ?? patient.RegisteredFacilityId, ct);
        if (context.FacilityId == Guid.Empty)
        {
            return BillingDb.FacilityRequired;
        }

        if (request.Quantity < 0 && request.AdmissionId is null && (request.VisitId ?? context.VisitId) is null)
        {
            return Error.Rule("billing.credit_needs_encounter", "A return must name the admission or visit whose charge it reverses.");
        }

        var serviceDate = request.ServiceDate ?? time.GetUtcNow();
        var quote = await tariffs.QuoteAsync(
            item, context.Plan, BillingTime.LocalDate(serviceDate), context.Plan?.MultiplierFor(context.BedServiceItemId) ?? 1m,
            isFreeFollowUp, request.UnitPriceOverride, ct);

        var charge = new Charge
        {
            FacilityId = context.FacilityId,
            PatientId = request.PatientId,
            VisitId = request.VisitId ?? context.VisitId,
            AdmissionId = request.AdmissionId,
            ServiceItemId = item.Id,
            ServiceCode = item.Code,
            Description = string.IsNullOrWhiteSpace(request.Description) ? item.Name : request.Description.Trim(),
            Category = item.Category,
            Quantity = request.Quantity,
            IsDiscountAllowed = item.IsDiscountAllowed,
            SourceModule = request.SourceModule.Trim(),
            SourceReference = request.SourceReference.Trim(),
            PractitionerId = request.PractitionerId,
            DepartmentId = request.DepartmentId ?? item.DepartmentId,
            ServiceDate = serviceDate,
            Status = ChargeStatus.Unbilled,
        };
        charge.ApplyPrice(quote, item.TaxRate, context.Plan?.DefaultDiscountPercent ?? 0m, context.Plan?.Id);
        if (charge.IsCredit)
        {
            var creditable = await CreditableAsync(charge, ct);
            if (-charge.GrossAmount > creditable)
            {
                return Error.Rule(
                    "billing.credit_exceeds_charge",
                    $"The return of ₹{Money.FormatInr(-charge.GrossAmount)} is more than the ₹{Money.FormatInr(Math.Max(creditable, 0m))} charged for {item.Name} on this {(charge.AdmissionId is null ? "visit" : "admission")}.");
            }
        }

        db.Add(charge);
        return charge;
    }

    /// <summary>
    /// Re-prices unbilled charges on a (new) tariff plan — used when the IP account opens after charges were staged in
    /// the same transaction, and when an admission changes payer. Explicit price overrides are kept.
    /// </summary>
    internal async Task RepriceAsync(IEnumerable<Charge> charges, TariffPlan? plan, Guid? bedServiceItemId, CancellationToken ct)
    {
        foreach (var charge in charges.Where(c => c.PriceSource != PriceSource.Override && c.Status == ChargeStatus.Unbilled))
        {
            var item = await db.Set<ServiceItem>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == charge.ServiceItemId, ct);
            if (item is null)
            {
                continue;
            }

            var multiplier = bedServiceItemId is null ? charge.BedMultiplier : plan?.MultiplierFor(bedServiceItemId) ?? 1m;
            var quote = await tariffs.QuoteAsync(
                item, plan, BillingTime.LocalDate(charge.ServiceDate), multiplier, charge.PriceSource == PriceSource.FreeFollowUp, null, ct);
            charge.ApplyPrice(quote, item.TaxRate, plan?.DefaultDiscountPercent ?? 0m, plan?.Id);
        }
    }

    /// <summary>IP account of an admission, including one opened earlier in this unit of work.</summary>
    internal async Task<IpAccount?> FindAccountAsync(Guid admissionId, CancellationToken ct) =>
        db.Set<IpAccount>().Local.FirstOrDefault(a => a.AdmissionId == admissionId)
        ?? await db.Set<IpAccount>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(a => a.AdmissionId == admissionId, ct);

    /// <summary>The charge of a source line in any facility, including one staged (or changed) in this unit of work.</summary>
    internal async Task<Charge?> FindBySourceAsync(string sourceModule, string sourceReference, CancellationToken ct)
    {
        var module = sourceModule.Trim();
        var reference = sourceReference.Trim();
        return db.Set<Charge>().Local.FirstOrDefault(c => c.SourceModule == module && c.SourceReference == reference)
               ?? await db.Set<Charge>().IgnoreQueryFilters()
                   .FirstOrDefaultAsync(c => c.SourceModule == module && c.SourceReference == reference, ct);
    }

    internal static ChargeDto ToDto(Charge c, string? practitionerName) => new(
        c.Id, c.PatientId, c.VisitId, c.AdmissionId, c.ServiceItemId, c.ServiceCode, c.Description, c.Category, c.Quantity, c.UnitPrice, c.GrossAmount,
        c.DiscountAmount, c.TaxAmount, c.NetAmount, c.PriceSource, c.SourceModule, c.SourceReference, c.PractitionerId, practitionerName, c.DepartmentId,
        c.ServiceDate, c.Status, c.BillId, c.CancelReason);

    /// <summary>
    /// What has been charged for the same service on the same admission (or, for an OP return, the same visit), net of
    /// earlier returns — the most a new return may credit. Tracked rows win over the database, so a charge cancelled or a
    /// return posted earlier in this unit of work counts.
    /// </summary>
    private async Task<decimal> CreditableAsync(Charge credit, CancellationToken ct)
    {
        var admissionId = credit.AdmissionId;
        var visitId = credit.VisitId;
        var query = db.Set<Charge>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(c => c.ServiceItemId == credit.ServiceItemId);
        query = admissionId is not null ? query.Where(c => c.AdmissionId == admissionId) : query.Where(c => c.AdmissionId == null && c.VisitId == visitId);

        var charged = await query.ToListAsync(ct);
        charged.AddRange(db.ChangeTracker.Entries<Charge>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .Where(c => c.ServiceItemId == credit.ServiceItemId
                        && (admissionId is not null ? c.AdmissionId == admissionId : c.AdmissionId == null && c.VisitId == visitId)
                        && !charged.Contains(c)));
        return charged.Where(c => c.Status != ChargeStatus.Cancelled).Sum(c => c.GrossAmount);
    }

    /// <summary>Merge state and registration facility of a patient (<c>null</c> = no such patient).</summary>
    private async Task<PatientFacts?> PatientAsync(Guid patientId, CancellationToken ct)
    {
        if (db.Set<Patient>().Local.FirstOrDefault(p => p.Id == patientId) is { } local)
        {
            return new PatientFacts(local.MergedIntoPatientId, local.RegisteredFacilityId);
        }

        return await db.Set<Patient>()
            .Where(p => p.Id == patientId)
            .Select(p => new PatientFacts(p.MergedIntoPatientId, p.RegisteredFacilityId))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Facility, tariff and bed class of a charge: from the admission's IP account, else from the visit (both looked up in
    /// every facility — a background job has no facility, and a user may serve another facility's patient), else the
    /// fallback facility with the self-pay tariff.
    /// </summary>
    private async Task<ChargeContext> ResolveContextAsync(ChargeRequest request, Guid fallbackFacilityId, CancellationToken ct)
    {
        if (request.AdmissionId is { } admissionId && await FindAccountAsync(admissionId, ct) is { } account)
        {
            return new ChargeContext(account.FacilityId, await tariffs.FindPlanAsync(account.TariffPlanId, ct), account.BedServiceItemId, account.VisitId);
        }

        if (request.VisitId is { } visitId
            && (db.Set<Visit>().Local.FirstOrDefault(v => v.Id == visitId)
                ?? await db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(v => v.Id == visitId, ct)) is { } visit)
        {
            return new ChargeContext(visit.FacilityId, await tariffs.ResolvePlanAsync(visit.PayerType, visit.PayerId, visit.FacilityId, ct), null, null);
        }

        return new ChargeContext(fallbackFacilityId, await tariffs.ResolvePlanAsync(PayerType.Self, null, fallbackFacilityId, ct), null, null);
    }

    private static Error? Validate(ChargeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceModule) || request.SourceModule.Trim().Length > 64)
        {
            return Error.Field("sourceModule", "The source module is required (at most 64 characters).");
        }

        if (string.IsNullOrWhiteSpace(request.SourceReference) || request.SourceReference.Trim().Length > 128)
        {
            return Error.Field("sourceReference", "The source reference is required (at most 128 characters).");
        }

        // Negative quantities are credits (returns); zero is never a charge.
        if (request.Quantity == 0 || Math.Abs(request.Quantity) > 10_000)
        {
            return Error.Field("quantity", "The quantity must not be zero and at most 10,000 (negative for a return).");
        }

        if (request.UnitPriceOverride is < 0)
        {
            return Error.Field("unitPriceOverride", "The price cannot be negative.");
        }

        return request.Description is { Length: > 300 } ? Error.Field("description", "The description can have at most 300 characters.") : null;
    }

    private sealed record PatientFacts(Guid? MergedIntoPatientId, Guid RegisteredFacilityId);

    private sealed record ChargeContext(Guid FacilityId, TariffPlan? Plan, Guid? BedServiceItemId, Guid? VisitId);
}
