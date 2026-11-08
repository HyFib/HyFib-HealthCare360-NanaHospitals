using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Billing.Documents;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Billing.Features.Bills;

/// <summary>
/// /api/v1/billing/bills — OP/IP bills: draft, services, an OP bill's package, discounts, finalize, cancel, credit notes,
/// the printed A4 bill and its PDF, and sending it to the patient on WhatsApp (OPB, IPB, MB-02, MB-05/06, INT-WA).
/// </summary>
internal static class BillEndpoints
{
    private static readonly string[] Readers =
        [BillingPermissions.ChargesRead, BillingPermissions.OpCreate, BillingPermissions.IpManage, BillingPermissions.PaymentsCollect];

    private static readonly Dictionary<string, Expression<Func<Bill, object?>>> Sort = new(StringComparer.OrdinalIgnoreCase)
    {
        ["createdAt"] = b => b.CreatedAt,
        ["finalizedAt"] = b => b.FinalizedAt,
        ["billNumber"] = b => b.BillNumber,
        ["netAmount"] = b => b.NetAmount,
        ["dueAmount"] = b => b.DueAmount,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/bills").WithTags("Billing - Bills");

        group.MapGet("/", List).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}/document", Document).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}/pdf", Pdf).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}/credit-notes/{noteId:guid}/document", CreditNoteDocumentAsync).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}/credit-notes/{noteId:guid}/pdf", CreditNotePdf).RequireAnyPermission(Readers);
        group.MapPost("/", Create).RequirePermission(BillingPermissions.OpCreate).WithValidation<CreateBillRequest>();
        group.MapPost("/{id:guid}/lines", AddLine).RequirePermission(BillingPermissions.OpCreate).WithValidation<AddBillLineRequest>();
        group.MapDelete("/{id:guid}/lines/{lineId:guid}", RemoveLine).RequireAnyPermission(BillingPermissions.OpCreate, BillingPermissions.IpManage);
        group.MapPut("/{id:guid}/package", SetPackage).RequirePermission(BillingPermissions.OpCreate).WithValidation<SetBillPackageRequest>();
        group.MapPost("/{id:guid}/refresh", Refresh).RequirePermission(BillingPermissions.OpCreate).WithValidation<BillVersionRequest>();
        group.MapPost("/{id:guid}/discount", ApplyDiscount).RequireAnyPermission(BillingPermissions.OpCreate, BillingPermissions.IpManage)
            .WithValidation<ApplyDiscountRequest>();
        group.MapPost("/{id:guid}/finalize", FinalizeBill).RequirePermission(BillingPermissions.BillsFinalize).WithValidation<BillVersionRequest>();
        group.MapPost("/{id:guid}/cancel", Cancel).RequirePermission(BillingPermissions.BillsCancel).WithValidation<CancelBillRequest>();
        group.MapDelete("/{id:guid}", Discard).RequireAnyPermission(BillingPermissions.OpCreate, BillingPermissions.IpManage);
        group.MapPost("/{id:guid}/send-whatsapp", SendOnWhatsApp)
            .RequireAnyPermission(BillingPermissions.OpCreate, BillingPermissions.IpManage, BillingPermissions.PaymentsCollect);
        // Asks for a credit note or write-off; an approver, someone else, issues it from the approvals inbox (202).
        group.MapPost("/{id:guid}/credit-notes", RequestCreditNote).RequireAnyPermission(BillingPermissions.RefundsRequest, BillingPermissions.RefundsApprove)
            .WithValidation<IssueCreditNoteRequest>();
        // Asks for a debit note (supplementary invoice); an approver, someone else, issues it (202).
        group.MapPost("/{id:guid}/debit-notes", RequestDebitNote).RequireAnyPermission(BillingPermissions.RefundsRequest, BillingPermissions.RefundsApprove)
            .WithValidation<IssueDebitNoteRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        DateOnly? from,
        DateOnly? to,
        BillType? type,
        BillStatus? status,
        Guid? patientId,
        Guid? visitId,
        Guid? admissionId,
        Guid? payerId,
        bool? withDueOnly,
        HospitalDbContext db,
        IFieldProtector protector,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var bills = db.Set<Bill>().AsNoTracking();
        if (from is not null || to is not null)
        {
            var (start, end) = BillingTime.Range(from ?? DateOnly.MinValue.AddDays(1), to ?? BillingTime.Today(time));
            bills = bills.Where(b => (b.FinalizedAt ?? b.CreatedAt) >= start && (b.FinalizedAt ?? b.CreatedAt) < end);
        }

        if (type is not null)
        {
            bills = bills.Where(b => b.Type == type);
        }

        if (status is not null)
        {
            bills = bills.Where(b => b.Status == status);
        }

        if (patientId is not null)
        {
            bills = bills.Where(b => b.PatientId == patientId);
        }

        if (visitId is not null)
        {
            bills = bills.Where(b => b.VisitId == visitId);
        }

        if (admissionId is not null)
        {
            bills = bills.Where(b => b.AdmissionId == admissionId);
        }

        if (payerId is not null)
        {
            bills = bills.Where(b => b.PayerId == payerId);
        }

        if (withDueOnly == true)
        {
            bills = bills.Where(b => b.DueAmount > 0 && (b.Status == BillStatus.Finalized || b.Status == BillStatus.PartiallyPaid));
        }

        if (page.SearchTerm is { } term)
        {
            var number = term.ToUpperInvariant();
            var patients = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            bills = bills.Where(b => b.BillNumber == number || patients.Contains(b.PatientId));
        }

        var rows = await BillQueries.Rows(bills.OrderByKey(page, Sort, b => b.CreatedAt, defaultDescending: true)).ToPagedResultAsync(page, ct);
        return TypedResults.Ok(await BillQueries.ToListAsync(db, rows, time, ct));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var (detail, problem) = await OpenAsync(id, number => $"Viewed bill {number}", db, guard, audit, time, ct);
        return problem ?? TypedResults.Ok(detail);
    }

    /// <summary>The A4 bill as the staff app prints it: the same document as the PDF the patient downloads.</summary>
    private static async Task<IResult> Document(
        Guid id, HospitalDbContext db, DocumentAssets assets, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var (detail, problem) = await OpenAsync(id, number => $"Printed bill {number}", db, guard, audit, time, ct);
        return problem ?? TypedResults.Ok((await BillDocument.BuildAsync(detail!, assets, DocumentText.English, ct)).ToDto());
    }

    private static async Task<IResult> Pdf(
        Guid id, HospitalDbContext db, DocumentAssets assets, IPdfRenderer renderer, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time,
        CancellationToken ct)
    {
        var (detail, problem) = await OpenAsync(id, number => $"Downloaded bill {number} as a PDF", db, guard, audit, time, ct);
        return problem ?? await (await BillDocument.BuildAsync(detail!, assets, DocumentText.English, ct)).ToPdfAsync(renderer, ct);
    }

    /// <summary>A credit note of the bill as the staff app prints it (rule 53(1A) particulars, its IRN once registered).</summary>
    private static async Task<IResult> CreditNoteDocumentAsync(
        Guid id, Guid noteId, HospitalDbContext db, DocumentAssets assets, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var (document, problem) = await CreditNoteAsync(id, noteId, "Printed", db, assets, guard, audit, time, ct);
        return problem ?? TypedResults.Ok(document!.ToDto());
    }

    private static async Task<IResult> CreditNotePdf(
        Guid id, Guid noteId, HospitalDbContext db, DocumentAssets assets, IPdfRenderer renderer, PatientAccessGuard guard, IAuditLogger audit,
        TimeProvider time, CancellationToken ct)
    {
        var (document, problem) = await CreditNoteAsync(id, noteId, "Downloaded", db, assets, guard, audit, time, ct);
        return problem ?? await document!.ToPdfAsync(renderer, ct);
    }

    /// <summary>
    /// The credit note <paramref name="noteId"/> of bill <paramref name="id"/>, issued to the buyer the bill's tax invoice
    /// named: a payer registered for GST, with its GSTIN and address, or else the patient. Opened as the bill is.
    /// </summary>
    private static async Task<(PrintableDocument? Document, IResult? Problem)> CreditNoteAsync(
        Guid id, Guid noteId, string verb, HospitalDbContext db, DocumentAssets assets, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time,
        CancellationToken ct)
    {
        var number = await db.Set<CreditNote>().Where(c => c.Id == noteId && c.BillId == id).Select(c => c.Number).FirstOrDefaultAsync(ct);
        if (number is null)
        {
            return (null, Error.NotFound(nameof(CreditNote), noteId).ToProblem());
        }

        var (detail, problem) = await OpenAsync(id, bill => $"{verb} credit note {number} of bill {bill}", db, guard, audit, time, ct);
        if (problem is not null)
        {
            return (null, problem);
        }

        var note = detail!.CreditNotes.Single(c => c.Id == noteId);
        var payer = detail.PayerId is { } payerId
            ? await db.Set<Payer>().AsNoTracking().Where(p => p.Id == payerId).Select(p => new { p.Name, p.Gstin, p.Address }).FirstOrDefaultAsync(ct)
            : null;
        // A payer credit note goes to the payer, registered for GST or not.
        var recipient = payer?.Gstin is not null || (payer is not null && note.Kind == CreditNoteKind.PayerShare)
            ? new CreditNoteRecipient(payer.Name, payer.Gstin, payer.Address)
            : new CreditNoteRecipient(detail.PatientName ?? DocumentFormat.Missing, null, null);
        return (await CreditNoteDocument.BuildAsync(detail, note, recipient, assets, ct), null);
    }

    // Viewing, printing and downloading a bill apply the same rules: the restricted record guard, and an entry in the PHI
    // access audit saying what was done with it.
    private static async Task<(BillDetail? Detail, IResult? Problem)> OpenAsync(
        Guid id, Func<string, string> action, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var detail = await BillQueries.DetailAsync(db, id, time, ct);
        if (detail is null)
        {
            return (null, Error.NotFound(nameof(Bill), id).ToProblem());
        }

        var access = await guard.EnsureCanOpenAsync(detail.PatientId, ct);
        if (access.IsFailure)
        {
            return (null, access.Error!.ToProblem());
        }

        await audit.PatientRecordAccessedAsync(detail.PatientId, action(detail.BillNumber ?? "(draft)"), ct);
        return (detail, null);
    }

    private static async Task<IResult> Create(CreateBillRequest request, BillService bills, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var created = await bills.CreateOpDraftAsync(request, ct);
        return created.IsSuccess
            ? TypedResults.Created($"/api/v1/billing/bills/{created.Value}", await BillQueries.DetailAsync(db, created.Value, time, ct))
            : created.Error!.ToProblem();
    }

    private static async Task<IResult> AddLine(Guid id, AddBillLineRequest request, BillService bills, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        await DetailAfterAsync(await bills.AddServiceAsync(id, request, ct), db, id, time, ct);

    private static async Task<IResult> RemoveLine(Guid id, Guid lineId, uint version, BillService bills, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        await DetailAfterAsync(await bills.RemoveLineAsync(id, lineId, version, ct), db, id, time, ct);

    private static async Task<IResult> SetPackage(Guid id, SetBillPackageRequest request, BillService bills, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        await DetailAfterAsync(await bills.SetPackageAsync(id, request, ct), db, id, time, ct);

    private static async Task<IResult> Refresh(Guid id, BillVersionRequest request, BillService bills, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var refreshed = await bills.RefreshAsync(id, request.Version, ct);
        return await DetailAfterAsync(refreshed.IsSuccess ? Result.Success() : refreshed.Error!, db, id, time, ct);
    }

    private static async Task<IResult> ApplyDiscount(Guid id, ApplyDiscountRequest request, DiscountService discounts, CancellationToken ct) =>
        (await discounts.ApplyAsync(id, request, ct)).ToOk();

    private static async Task<IResult> FinalizeBill(Guid id, BillVersionRequest request, BillService bills, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        await DetailAfterAsync(await bills.FinalizeAsync(id, request.Version, ct), db, id, time, ct);

    private static async Task<IResult> Cancel(Guid id, CancelBillRequest request, BillService bills, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        await DetailAfterAsync(await bills.CancelAsync(id, request, ct), db, id, time, ct);

    private static async Task<IResult> Discard(Guid id, uint version, BillService bills, CancellationToken ct) =>
        (await bills.DiscardDraftAsync(id, version, ct)).ToNoContent();

    /// <summary>Queues the bill's PDF to the patient's mobile number on WhatsApp; 204 once queued (the log shows delivery).</summary>
    private static async Task<IResult> SendOnWhatsApp(Guid id, BillSharing sharing, PatientAccessGuard guard, HospitalDbContext db, IAuditLogger audit, CancellationToken ct)
    {
        var patientId = await db.Set<Bill>().Where(b => b.Id == id).Select(b => (Guid?)b.PatientId).FirstOrDefaultAsync(ct);
        if (patientId is not { } patient)
        {
            return Error.NotFound(nameof(Bill), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(patient, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var sent = await sharing.SendAsync(id, ct);
        if (sent.IsSuccess)
        {
            await audit.PatientRecordAccessedAsync(patient, "Sent a bill to the patient on WhatsApp", ct);
        }

        return sent.ToNoContent();
    }

    private static async Task<IResult> RequestCreditNote(Guid id, IssueCreditNoteRequest request, RefundService refunds, CancellationToken ct) =>
        (await refunds.RequestCreditNoteAsync(id, request, ct)).ToHttpResult(approval => TypedResults.Accepted($"/api/v1/approvals/{approval.Id}", approval));

    private static async Task<IResult> RequestDebitNote(Guid id, IssueDebitNoteRequest request, DebitNoteService notes, CancellationToken ct) =>
        (await notes.RequestAsync(id, request, ct)).ToHttpResult(approval => TypedResults.Accepted($"/api/v1/approvals/{approval.Id}", approval));

    private static async Task<IResult> DetailAfterAsync(Result result, HospitalDbContext db, Guid id, TimeProvider time, CancellationToken ct)
    {
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        var detail = await BillQueries.DetailAsync(db, id, time, ct);
        return detail is null ? Error.NotFound(nameof(Bill), id).ToProblem() : TypedResults.Ok(detail);
    }
}
