using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// Every table of the hospital's database is in the audit trail (SRS SEC-04), or is on the reviewed list below with the
/// reason it is not, so a new table cannot stay out of the trail without anyone deciding so. A table is in the trail when
/// its entity is an <see cref="SharedKernel.AuditableEntity"/> not marked <see cref="SharedKernel.NotAuditedAttribute"/>,
/// is marked <see cref="SharedKernel.AuditedAttribute"/>, or is marked <see cref="SharedKernel.AuditedWithParentAttribute"/>
/// and belongs to a record in the trail: what <see cref="UnitOfWorkInterceptor"/> writes a data audit event for.
/// </summary>
public sealed class AuditCoverageTests
{
    private static readonly Lazy<IModel> Model = new(() =>
    {
        using var scope = EndpointAuthorizationTests.App.Value.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Model;
    });

    /// <summary>The entities whose changes the data audit does not record, each with why (one line).</summary>
    private static readonly Dictionary<string, string> NotInTheTrail = new(StringComparer.Ordinal)
    {
        // The trail itself, and the machinery that runs the servers.
        ["AuditEvent"] = "The audit trail itself: append-only, hash-chained and sealed.",
        ["ClusterEntry"] = "Short-lived keys and counters the servers share (spent challenges, rate limits); expire within minutes.",
        ["IdempotencyRecord"] = "A stored answer replayed when a retry repeats its key; the change it answered is audited.",
        ["OutboxMessage"] = "A message waiting to be sent; the change that queued it is audited, the delivery is in the message log.",
        ["ServerNode"] = "Each server's live System health report, rewritten every minute.",
        ["LabGatewayNode"] = "Each server's live analyser and worklist status, rewritten within seconds.",
        ["LabOrderDownload"] = "An order queued for an analyser until it connects (barcode and channel codes); the lab order is audited.",
        ["WifiSample"] = "A sample of the Server Node's Wi-Fi link, kept 30 days for the health dashboard.",
        ["BackupRun"] = "The backup history; each backup's outcome and every recovery key action is audited (backup.*).",
        ["SeededGrant"] = "The seeder's note of default grants it applied; the grants are audited as role.permissions_changed.",
        ["RolePermission"] = "A role's permissions: role.created and role.permissions_changed list each one given or taken back, at start-up too.",

        // Sign-in and device records, whose events are audited one by one.
        ["UserSession"] = "A sign-in session: signing in, out, locking and ending are audited (auth.*).",
        ["RefreshToken"] = "A session's rotating token, stored hashed; reuse is audited (auth.refresh_reuse_detected).",
        ["AuthChallenge"] = "A pending second sign-in step or one-time code, stored hashed and spent within minutes; sign-ins are audited (auth.*).",
        ["WebAuthnCredential"] = "A device's public unlock key; adding and removing it are audited (auth.biometric_unlock_added/removed).",
        ["DeviceEnrollmentCode"] = "A one-time enrolment code, stored hashed; making and using it are audited (device.*).",
        ["BreakGlassAccess"] = "An emergency opening's time box; the opening and its reason are audited (patient.break_glass).",
        ["PortalFamilyCode"] = "A one-time code a patient passes on to link a family member's portal account; stored hashed.",

        // Logs: each row is itself the record of what happened, written once.
        ["NotificationLog"] = "The message log: what was sent to whom (masked) and whether it was delivered.",
        ["UserNotification"] = "An in-app notification to a staff member, copied from an event that is audited where it happens.",
        ["IntegrationLog"] = "The trace of each call to an outside system, with personal data masked.",
        ["GatewayWebhookEvent"] = "The payment gateway's verified notification, kept by its id so it is handled once.",
        ["InboundWhatsAppMessage"] = "A message a patient sent to the hospital's WhatsApp number, kept as received.",
        ["AiUsage"] = "One call to an AI provider; the clinical audit entry of the same transaction is the record (ai.*).",
        ["CampaignRecipient"] = "One patient a campaign was sent to, written once with the campaign's send.",
        ["FeedbackInvitation"] = "A single-use feedback link (token hash only) sent after discharge.",
        ["TelehealthMessage"] = "In-call chat, kept with the consultation as written.",
        ["TelehealthParticipant"] = "Who joined a video consultation and when, written by the signalling hub.",
        ["AttendancePunch"] = "A biometric punch exactly as the terminal sent it; append-only and high volume.",
        ["RpmReading"] = "A remote monitoring device reading; append-only and high volume.",
        ["StockLedgerEntry"] = "The stock ledger: each movement, who made it and why, is the audit record of stock.",
        ["EncounterAmendment"] = "The amendment history of a signed encounter, with the content as it was; append-only.",
        ["DischargeSummaryAmendment"] = "The amendment history of a signed discharge summary, with the content as it was; append-only.",
        ["DataRequestEvent"] = "A line of a data principal's request history, written once; the request is audited.",
        ["RetentionRun"] = "A clean-up run; each batch it removed and the run itself are audited (privacy.retention.*).",
        ["RetentionChange"] = "A change to a retention period, also audited as privacy.retention.changed.",
        ["RecordCopyFile"] = "A document of an issued copy, with its SHA-256; the database refuses changes and the issue is audited (record.copy_issued).",
        ["ReportDelivery"] = "A generated scheduled report file (encrypted) and its e-mails; downloading it is audited.",
        ["ReportDeliveryRecipient"] = "One e-mail of a scheduled report's delivery, sent through the outbox.",
        ["TallyExport"] = "The day book posting log for Tally; posting, export and checks are audited (billing.tally.*).",
        ["TallyVoucherRecord"] = "A voucher as sent to Tally with Tally's answer; the posting is audited (billing.tally.*).",
        ["GatewaySettlement"] = "A payout as the payment gateway reported it; its receipts and exceptions are audited (billing.gateway.*).",
        ["GatewaySettlementLine"] = "One transaction of a gateway payout as reported, and what it matched.",
        ["PosSettlementImport"] = "An acquirer's settlement file as imported; the import and its review are audited (billing.pos.*).",
        ["PosSettlementLine"] = "One transaction of an acquirer's settlement file as imported, and what it matched.",

        // Derived: kept from other records, which are audited where they change.
        ["AdmissionRecord"] = "SpecialtyCare's copy of an admission, kept from Inpatient's admission and discharge events.",
        ["PatientSpendEntry"] = "The CRM's read model of a patient's spend, kept from Billing's events.",
        ["BedDayCharge"] = "Inpatient's note of the daily charges Billing accepted, which are audited as charges.",
        ["BillLine"] = "A charge as copied onto a bill, with the bill's split; the charge and the bill are audited.",
        ["PayslipLine"] = "An earning or deduction the payroll run computes from the audited salary structure and attendance.",
        ["CashierShiftTotal"] = "A shift's totals per payment mode, computed from its audited receipts when it closes.",
        ["AttendanceDay"] = "A day's attendance, computed nightly from punches, roster and leave; regularisations are audited.",
        ["StockBatch"] = "A batch's quantities, moved only with a stock ledger entry; blocking and unblocking are audited.",
        ["StockReservation"] = "Stock held for an audited document (an indent, a booked implant) until it is issued or released.",
        ["PlannedMeal"] = "A patient's meal for one round, planned from the audited diet order; high volume.",
        ["QueueToken"] = "A day's queue token at a desk, called and served; the visit it belongs to is audited.",
        ["PharmacyQueueLine"] = "A line of the pharmacy's work queue, copied from the audited prescription.",
        ["ItineraryItem"] = "A station of a check-up visit's itinerary, made from the package and ticked off as it is done.",

        // Catalogues replaced by an import, which is audited as a whole.
        ["IcdCode"] = "The ICD catalogue: an import writes tens of thousands of rows, audited as one mrd.catalogue.imported entry.",
        ["ProcedureCode"] = "The procedure catalogue, imported and audited as the ICD catalogue is.",
        ["IcdClassificationEntry"] = "A chapter or block of an imported classification, replaced by each import.",
        ["CatalogueImport"] = "The record of a catalogue import, which writes its own audit entry (mrd.catalogue.imported).",

        // Training data of the simulators that stand in for outside systems.
        ["SimulatorMessage"] = "What a simulated SMS, WhatsApp or e-mail provider delivered; training only.",
        ["SimulatorTallyVoucher"] = "A voucher in the simulated Tally company; training only.",
        ["SimulatorTransaction"] = "A simulated payment-gateway or POS transaction; training only.",

        // Lines written with their document, and the document is audited; the lines are not yet in the trail with it.
        // Reviewed: audit each with its document ([AuditedWithParent]) in a later change.
        ["ReceiptPayment"] = "Not yet audited: a payment mode of a receipt, written once with the audited receipt.",
        ["PharmacyPayment"] = "Not yet audited: a payment or refund against a pharmacy invoice, written once.",
        ["PharmacySaleItem"] = "Not yet audited: a line of a pharmacy sale, written once with the audited sale.",
        ["PharmacySaleReturnItem"] = "Not yet audited: a line of a pharmacy sale return, written once with the audited return.",
        ["CanteenSaleLine"] = "Not yet audited: a line of a canteen bill, copied from the menu when the audited bill is made.",
        ["ClaimSettlement"] = "Not yet audited: an insurance claim's settlement, written once; audited as insurance.claim.settled.",
        ["ClaimDeduction"] = "Not yet audited: a deduction the insurer made from a claim, written once with its settlement.",
        ["DoctorPayoutDeduction"] = "Not yet audited: a deduction from a doctor's payout, written once with the audited payout.",
        ["PreAuthEnhancement"] = "Not yet audited: an enhancement of a pre-authorisation, written once with the audited pre-authorisation.",
        ["GoodsReceiptLine"] = "Not yet audited: a line of a goods receipt; the stock it brings in is in the stock ledger.",
        ["PurchaseOrderLine"] = "Not yet audited: a line of a purchase order, edited while the audited order is a draft.",
        ["PurchaseRequisitionLine"] = "Not yet audited: a line of a purchase requisition, edited while the audited requisition is a draft.",
        ["IndentLine"] = "Not yet audited: a line of a store indent, edited while the audited indent is open.",
        ["IndentIssueLine"] = "Not yet audited: a batch issued against an indent; the stock it moves is in the stock ledger.",
        ["StockTransferLine"] = "Not yet audited: a batch moved between stores; the movement is in the stock ledger.",
        ["StockAdjustmentLine"] = "Not yet audited: a line of a stock adjustment; the movement is in the stock ledger.",
        ["StockCountLine"] = "Not yet audited: a counted quantity of a stock count; the adjustment it leads to is in the stock ledger.",
        ["WardIndentLine"] = "Not yet audited: a line of a ward's indent to the pharmacy, written with the audited indent.",
        ["IpIssueLine"] = "Not yet audited: a batch the pharmacy issued to a ward; the stock it moves is in the stock ledger.",
        ["WardReturnLine"] = "Not yet audited: a batch a ward returned; the stock it moves is in the stock ledger.",
        ["NarcoticCountLine"] = "Not yet audited: a line of an NDPS stock count, written once with the audited count.",
        ["CssdRequestLine"] = "Not yet audited: a line of a request to CSSD, written with the audited request.",
        ["SoiledReceiptLine"] = "Not yet audited: a tray received soiled at CSSD with its count check, written once.",
        ["WashCycleTray"] = "Not yet audited: a tray washed in a cycle, written once with the audited cycle.",
        ["SterilePack"] = "Not yet audited: a labelled pack of a sterilizer load; a recall is audited (cssd.recall).",
        ["LinenMovementLine"] = "Not yet audited: a line of a linen movement, written once with the audited movement.",
        ["ChartDeficiency"] = "Not yet audited: a deficiency medical records noted on a chart, ticked off when it is made good.",
        ["RcaFactor"] = "Not yet audited: a contributing factor of a root cause analysis, edited with the audited investigation.",
        ["BreachAffectedPatient"] = "Not yet audited: a patient a breach affected; the notices are audited (privacy.breach.notified).",
    };

    /// <summary>
    /// Entities whose class name another entity has too, reviewed: the trail names both alike (its entity type), and the ids
    /// of their records tell them apart.
    /// </summary>
    private static readonly Dictionary<string, string> SharedNames = new(StringComparer.Ordinal)
    {
        ["ControlledDrugRegisterEntry"] = "The prescriber's register (clinical) and the pharmacy's (pharmacy), both [Audited].",
    };

    [Fact]
    public void Every_table_is_in_the_audit_trail_or_on_the_reviewed_list()
    {
        var missing = EntityTypes().Where(e => !InTheTrail(e) && !NotInTheTrail.ContainsKey(e.ClrType.Name)).Select(e => e.ClrType.Name).Order().ToList();

        missing.ShouldBeEmpty(
            "Audit these (an AuditableEntity, [Audited], or [AuditedWithParent] for a record's rows), or add them to NotInTheTrail with the reason");
    }

    [Fact]
    public void The_reviewed_list_names_only_tables_that_are_not_in_the_trail()
    {
        var entities = EntityTypes().ToLookup(e => e.ClrType.Name, StringComparer.Ordinal);

        NotInTheTrail.Keys.Where(name => !entities.Contains(name)).ShouldBeEmpty("These are no longer in the model: take them off the list");
        NotInTheTrail.Keys.Where(name => entities[name].Any(InTheTrail)).ShouldBeEmpty("These are in the audit trail now: take them off the list");
    }

    [Fact]
    public void Each_table_has_a_name_of_its_own_in_the_trail()
    {
        var shared = EntityTypes().GroupBy(e => e.ClrType.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        shared.Where(name => !SharedNames.ContainsKey(name))
            .ShouldBeEmpty("The trail names a record's type by its class name, and so does the list above: give these names of their own");
        SharedNames.Keys.Where(name => !shared.Contains(name)).ShouldBeEmpty("These names are no longer shared: take them off SharedNames");
    }

    [Fact]
    public void Each_reason_is_one_line() =>
        NotInTheTrail.Concat(SharedNames).Where(r => string.IsNullOrWhiteSpace(r.Value) || r.Value.Contains('\n', StringComparison.Ordinal))
            .Select(r => r.Key).ShouldBeEmpty();

    [Fact]
    public void Rows_audited_with_their_record_belong_to_one_record_in_the_trail()
    {
        var withParent = EntityTypes().Where(e => e.ClrType.GetCustomAttributes(typeof(SharedKernel.AuditedWithParentAttribute), false).Length > 0).ToList();

        withParent.ShouldNotBeEmpty();
        withParent.Where(e => UnitOfWorkInterceptor.ParentForeignKey(e) is null).Select(e => e.ClrType.Name)
            .ShouldBeEmpty("A row audited with its record needs exactly one foreign key to it, from a collection of the record's");
        withParent.Where(e => UnitOfWorkInterceptor.ParentForeignKey(e) is { } key && !UnitOfWorkInterceptor.IsAudited(key.PrincipalEntityType.ClrType))
            .Select(e => e.ClrType.Name)
            .ShouldBeEmpty("The record these rows are audited with is not in the trail itself");
        withParent.Where(e => UnitOfWorkInterceptor.IsAudited(e.ClrType)).Select(e => e.ClrType.Name)
            .ShouldBeEmpty("Audited on their own already: the record's events would list them a second time");
    }

    [Fact]
    public void Each_entity_audited_on_its_own_has_an_id_for_its_events()
    {
        EntityTypes().Where(e => UnitOfWorkInterceptor.IsAudited(e.ClrType) && e.FindProperty(nameof(SharedKernel.Entity.Id)) is null)
            .Select(e => e.ClrType.Name)
            .ShouldBeEmpty("A data audit event names its record by Id");
    }

    private static IEnumerable<IEntityType> EntityTypes() => Model.Value.GetEntityTypes();

    private static bool InTheTrail(IEntityType entity) =>
        UnitOfWorkInterceptor.IsAudited(entity.ClrType)
        || (UnitOfWorkInterceptor.ParentForeignKey(entity) is { } parent && UnitOfWorkInterceptor.IsAudited(parent.PrincipalEntityType.ClrType));
}
