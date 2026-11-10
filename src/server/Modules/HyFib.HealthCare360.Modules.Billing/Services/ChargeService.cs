using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Settings;

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
/// <item>A charge cancelled at the billing desk (<see cref="AskCancelAsync"/>) waits for a second person when it is an OT,
/// procedure, implant or bed-day charge, or worth more than the hospital's amount (<see cref="CancelApprovalAboveSetting"/>):
/// cancelling a service the patient had is a discount by another name (MAKER-CHECKER.md, slice 6). Charges a module
/// cancels itself (an order withdrawn, a stay or visit cancelled, a package changed) go through <see cref="CancelAsync"/>
/// and <see cref="CancelChargeAsync"/>, at once as before.</item>
/// </list>
/// </summary>
public sealed class ChargeService(
    HospitalDbContext db,
    TariffService tariffs,
    ChargeRefusals refusals,
    IApprovals approvals,
    ISettingsService settings,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time) : IChargePoster
{
    /// <summary>A charge cancelled at the billing desk, waiting for a second person.</summary>
    public const string CancelKind = "billing.charge_cancel";

    /// <summary>
    /// The net amount (₹) above which cancelling any other charge at the desk waits for approval; 0 makes every
    /// cancellation wait. Raising it waits for a second administrator (SettingGuard).
    /// </summary>
    public const string CancelApprovalAboveSetting = "billing.chargeCancel.approvalAbove";

    /// <summary>The amount while none is set: cancelling a charge worth more than ₹1,000 waits.</summary>
    public const decimal DefaultCancelApprovalAbove = 1000m;

    public const string ManualSource = "billing.manual";
    public const string AdhocSource = "billing.adhoc";
    public const string ConsultationSource = "billing.consultation";
    public const string RegistrationSource = "billing.registration";
    public const string PackageSource = "billing.package";

    /// <summary>Procedures priced by Billing when a clinician orders them (reference = the order item id).</summary>
    public const string OrderSource = "billing.order";

    /// <summary>
    /// The services an implant is billed under: the OT's (SpecialtyCare seeds <c>OT-IMPLANT</c>) and the IP pharmacy's
    /// (Pharmacy seeds <c>PHARM-IMPL</c>). Their records name the implant's brand, maker and batch and keep its price
    /// within its MRP and NPPA ceiling, so the desk does not charge them by hand.
    /// </summary>
    public static readonly IReadOnlySet<string> ImplantServiceCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OT-IMPLANT", "PHARM-IMPL" };

    /// <summary>
    /// Stages the charge of a module's line. An automatic charge's refusal is kept on record until the line posts
    /// (<see cref="ChargeRefusals"/>), so a day that goes unbilled is warned of and holds the stay's final bill. Does not save.
    /// </summary>
    public async Task<Result<Guid>> PostAsync(ChargeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var staged = await StageAsync(request, isFreeFollowUp: false, cancellationToken);
        if (request.Automatic && staged.IsFailure)
        {
            await refusals.RefusedAsync(request, staged.Error!, cancellationToken);
        }
        else if (request.Automatic)
        {
            await refusals.ClearedAsync(request.SourceModule, request.SourceReference, posted: true, cancellationToken);
        }

        return staged.IsSuccess ? Result<Guid>.Ok(staged.Value.Id) : Result<Guid>.Fail(staged.Error!);
    }

    /// <summary>
    /// Cancels the charge of a source line (order cancelled, dispense returned, admission cancelled). Unknown or
    /// already-cancelled sources succeed, so retries are safe; a charge already on a finalized bill is refused (billing
    /// cancels the bill or issues a credit note instead). Does not save.
    /// </summary>
    public async Task<Result> CancelAsync(string sourceModule, string sourceReference, string reason, CancellationToken cancellationToken)
    {
        await refusals.ClearedAsync(sourceModule, sourceReference, posted: false, cancellationToken);
        var charge = await FindBySourceAsync(sourceModule, sourceReference, cancellationToken);
        return charge is null ? Result.Success() : await CancelChargeAsync(charge, reason, cancellationToken);
    }

    /// <summary>Manual charge from the billing desk (source <c>billing.manual</c>); saved immediately.</summary>
    public async Task<Result<ChargeDto>> PostManualAsync(PostChargeRequest request, CancellationToken ct)
    {
        var service = await db.Set<ServiceItem>().Where(s => s.Id == request.ServiceItemId).Select(s => new { s.Code, s.IsPriceEditable }).FirstOrDefaultAsync(ct);
        if (service is not null && ImplantAtTheDesk(service.Code) is { } implant)
        {
            return implant;
        }

        if (request.UnitPrice is not null && service is { IsPriceEditable: false })
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

    /// <summary>
    /// Whether cancelling <paramref name="charge"/> at the desk waits for a second person: always for an OT, procedure,
    /// implant or bed-day charge, otherwise when its net amount is above <paramref name="approvalAbove"/>. A credit (a
    /// return) cancelled makes the patient owe more, so it never waits.
    /// </summary>
    public static bool CancelNeedsApproval(Charge charge, decimal approvalAbove)
    {
        ArgumentNullException.ThrowIfNull(charge);
        if (charge.IsCredit)
        {
            return false;
        }

        var alwaysWaits = charge.Category is ServiceCategory.Surgery or ServiceCategory.Procedure or ServiceCategory.BedCharge
                          || charge.Device is not null
                          || ImplantServiceCodes.Contains(charge.ServiceCode);
        return alwaysWaits || charge.NetAmount > Math.Max(approvalAbove, 0m);
    }

    /// <summary>The facility's amount above which a desk cancellation waits (<see cref="CancelApprovalAboveSetting"/>).</summary>
    public async Task<decimal> CancelApprovalAboveAsync(Guid? facilityId, CancellationToken ct) =>
        Math.Max(await settings.GetAsync<decimal?>(CancelApprovalAboveSetting, facilityId, ct) ?? DefaultCancelApprovalAbove, 0m);

    /// <summary>The charges among these whose desk cancellation waits for approval, and when it was asked for.</summary>
    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> CancellationsAskedAsync(IReadOnlyCollection<Guid> chargeIds, CancellationToken ct) =>
        approvals.WaitingSinceAsync(CancelKind, nameof(Charge), chargeIds, ct);

    /// <summary>
    /// Cancels an unbilled charge from the billing desk: at once when it is small and not an OT, procedure, implant or
    /// bed-day charge; otherwise it waits for someone else who approves discounts (<see cref="CancelKind"/>), and nothing
    /// changes until then.
    /// </summary>
    public async Task<Result<ChargeCancelOutcome>> AskCancelAsync(Guid chargeId, string reason, CancellationToken ct)
    {
        var charge = await db.Set<Charge>().FirstOrDefaultAsync(c => c.Id == chargeId, ct);
        if (charge is null)
        {
            return Error.NotFound(nameof(Charge), chargeId);
        }

        if (charge.Status == ChargeStatus.Billed)
        {
            return Error.Rule("billing.charge_billed", "This service is on a finalized bill. Cancel the bill or issue a credit note instead.");
        }

        if (charge.Status == ChargeStatus.Cancelled)
        {
            return new ChargeCancelOutcome(null);
        }

        var approvalAbove = await CancelApprovalAboveAsync(charge.FacilityId, ct);
        if (!CancelNeedsApproval(charge, approvalAbove))
        {
            var cancelled = await CancelChargeAsync(charge, reason, ct);
            if (cancelled.IsFailure)
            {
                return cancelled.Error!;
            }

            await db.SaveChangesAsync(ct);
            return new ChargeCancelOutcome(null);
        }

        var patient = await db.Set<Patient>().Where(p => p.Id == charge.PatientId).Select(p => new { p.Uhid, p.FullName }).FirstOrDefaultAsync(ct);
        var who = patient is null ? "the patient" : $"{patient.FullName} ({patient.Uhid})";
        var doctor = charge.PractitionerId is { } pid ? await db.Set<Practitioner>().Where(p => p.Id == pid).Select(p => p.DisplayName).FirstOrDefaultAsync(ct) : null;
        List<ApprovalDetail> details =
        [
            new("Service", $"{charge.ServiceCode} {charge.Description} × {charge.Quantity:0.##}, {DocumentFormat.Date(charge.ServiceDate)}"),
            new("Patient", who),
            new("Amount", $"₹{Money.FormatInr(charge.NetAmount)} → nothing: the patient is no longer charged for it"),
            new("Posted by", Source(charge.SourceModule) + (doctor is null ? string.Empty : $", for {doctor}")),
        ];
        if (charge.BillId is not null)
        {
            details.Add(new("Bill", "Taken off the draft bill it is on"));
        }

        details.Add(new("Why it waits", IsAlwaysAsked(charge)
            ? "An OT, procedure, implant or bed-day charge is cancelled only with a second person's approval"
            : $"Worth more than ₹{Money.FormatInr(approvalAbove)}, the most the desk cancels alone"));
        var requested = await approvals.RequestAsync(
            CancelKind,
            new ApprovalSubject(nameof(Charge), charge.Id, charge.Version, charge.FacilityId, charge.PatientId),
            new ChargeCancelPayload(charge.Id, reason.Trim(), charge.NetAmount, charge.Quantity),
            Fit($"Cancel {charge.Description} (₹{Money.FormatInr(charge.NetAmount)}) for {who}", 300),
            details,
            reason,
            ct);
        return requested.IsSuccess ? new ChargeCancelOutcome(requested.Value) : requested.Error!;
    }

    /// <summary>
    /// Cancels a charge whose cancellation was approved, in the approval's transaction (the approval saves): only while it
    /// is still unbilled and charges what was asked about (<c>approval.stale</c>).
    /// </summary>
    internal async Task<Result<string>> CancelApprovedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<ChargeCancelPayload>(request.Payload);
        var charge = await db.Set<Charge>().FirstOrDefaultAsync(c => c.Id == asked.ChargeId, ct);
        if (charge is null)
        {
            return Error.NotFound(nameof(Charge), asked.ChargeId);
        }

        if (charge.Status != ChargeStatus.Unbilled || charge.NetAmount != asked.NetAmount || charge.Quantity != asked.Quantity)
        {
            return Error.Rule("approval.stale", charge.Status switch
            {
                ChargeStatus.Billed => $"{charge.Description} was billed on a finalized bill after this was asked for, so it was not cancelled. Ask for a credit note instead.",
                ChargeStatus.Cancelled => $"{charge.Description} was cancelled already, so nothing was changed.",
                _ => $"{charge.Description} changed after this was asked for, so it was not cancelled. Ask again, for the charge as it is now.",
            });
        }

        var checker = currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";
        var cancelled = await CancelChargeAsync(charge, Fit(asked.Reason, 300), ct);
        if (cancelled.IsFailure)
        {
            return cancelled.Error!;
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "billing.charge.cancelled",
            Fit($"{charge.Description} (₹{Money.FormatInr(charge.NetAmount)}) cancelled: {asked.Reason} (asked for by {request.RequestedByName}, approved by {checker})", 1000))
        {
            PatientId = charge.PatientId,
            EntityType = nameof(Charge),
            EntityId = charge.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { charge.ServiceCode, charge.NetAmount, charge.SourceModule, approval = request.Id, requestedBy = request.RequestedBy, approvedBy = currentUser.UserId },
        });
        return $"{charge.Description} cancelled";
    }

    private static bool IsAlwaysAsked(Charge charge) => CancelNeedsApproval(charge, decimal.MaxValue);

    private static string Source(string module) => module switch
    {
        ManualSource or AdhocSource => "The billing desk",
        ConsultationSource or RegistrationSource => "The front office, at the visit",
        PackageSource => "A package",
        OrderSource => "A doctor's order",
        _ => $"Automatically ({module})",
    };

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

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

        // A device is never charged above the MRP on its pack, nor a price-controlled one above its NPPA ceiling (DPCO 2013).
        // A credit (a device returned) gives back what was charged, whatever the limit is now.
        if (request.Device is { } device && request.Quantity > 0 && device.PriceLimit() is { } limit && quote.UnitPrice > limit)
        {
            return DevicePriceError(device, item.Name, quote.UnitPrice, limit);
        }

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

            // Only a bed's day is room rent.
            Room = item.Category == ServiceCategory.BedCharge ? request.Room : null,
            Device = request.Device,
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

    /// <summary>
    /// The refusal of an implant charged at the desk, by hand or as a line added to a draft: it is billed from the surgery
    /// record or the IP pharmacy's issue, which name it and keep its price within its limits. Null for any other service.
    /// </summary>
    internal static Error? ImplantAtTheDesk(string serviceCode) => ImplantServiceCodes.Contains(serviceCode)
        ? Error.Rule(
            "billing.implant_from_record",
            "An implant is billed from the surgery record or the IP pharmacy's issue, which name its brand, maker and batch and keep its price within its MRP and NPPA ceiling. Record it there.")
        : null;

    /// <summary>The refusal of a device charged above its limit, naming the limit that applies: the ceiling or the MRP.</summary>
    internal static Error DevicePriceError(DeviceDetails device, string itemName, decimal unitPrice, decimal limit)
    {
        var name = device.Brand ?? itemName;
        return device.CeilingLimits()
            ? Error.Rule(
                "billing.device_above_ceiling",
                $"{name} is charged ₹{Money.FormatInr(unitPrice)} a unit, above its NPPA ceiling price of ₹{Money.FormatInr(limit)}.")
            : Error.Rule(
                "billing.device_above_mrp",
                $"{name} is charged ₹{Money.FormatInr(unitPrice)} a unit, above the MRP of ₹{Money.FormatInr(limit)} printed on its pack.");
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

/// <summary>A charge's cancellation as asked: the reason, and what it charged then (a charge changed meanwhile is not cancelled).</summary>
public sealed record ChargeCancelPayload(Guid ChargeId, string Reason, decimal NetAmount, decimal Quantity);

/// <summary>A charge cancelled at once (<paramref name="Asked"/> null), or its cancellation waiting for approval.</summary>
public sealed record ChargeCancelOutcome(ApprovalRequestDto? Asked);

/// <summary>
/// An OT, procedure, implant or bed-day charge, or a large one, cancelled at the billing desk: the desk asks
/// (<c>billing.charges.manage</c>), and someone else who approves discounts decides, since a cancelled charge is a discount
/// by another name.
/// </summary>
internal sealed class ChargeCancelApproval(ChargeService charges) : IApprovalAction
{
    public string Kind => ChargeService.CancelKind;

    public string Title => "Charge cancelled";

    public string ApprovePermission => BillingPermissions.DiscountsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => charges.CancelApprovedAsync(request, ct);
}
