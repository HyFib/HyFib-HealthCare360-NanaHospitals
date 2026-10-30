using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.Pharmacy.Services;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Inpatient;

/// <summary>/api/v1/pharmacy — IP pharmacy (PH-03): ward indents, patient-wise issues charged to the IP account, ward returns.</summary>
internal static class InpatientEndpoints
{
    private static readonly string[] Readers = [PharmacyPermissions.IpIssue, PharmacyPermissions.IpIndent];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pharmacy").WithTags("Pharmacy");

        group.MapGet("/ward-indents", ListIndents).RequireAnyPermission(Readers);
        group.MapGet("/ward-indents/{id:guid}", GetIndent).RequireAnyPermission(Readers);
        group.MapPost("/ward-indents", async (RaiseWardIndentRequest r, InpatientPharmacyService ip, CancellationToken ct) =>
                (await ip.RaiseIndentAsync(r, ct)).ToCreated(d => $"/api/v1/pharmacy/ward-indents/{d.Id}"))
            .RequirePermission(PharmacyPermissions.IpIndent).WithValidation<RaiseWardIndentRequest>();
        group.MapPost("/ward-indents/{id:guid}/cancel", async (Guid id, PharmacyReasonRequest r, InpatientPharmacyService ip, CancellationToken ct) =>
                (await ip.CancelIndentAsync(id, r.Reason, ct)).ToNoContent())
            .RequireAnyPermission(Readers).WithValidation<PharmacyReasonRequest>();

        group.MapGet("/ip-issues", ListIssues).RequireAnyPermission(Readers);
        group.MapGet("/ip-issues/{id:guid}", GetIssue).RequireAnyPermission(Readers);
        group.MapPost("/ip-issues", async (IpIssueRequest r, InpatientPharmacyService ip, CancellationToken ct) =>
                (await ip.IssueAsync(r, ct)).ToCreated(d => $"/api/v1/pharmacy/ip-issues/{d.Id}"))
            .RequirePermission(PharmacyPermissions.IpIssue).WithValidation<IpIssueRequest>();
        group.MapPost("/ip-issues/{id:guid}/returns", async (Guid id, WardReturnRequest r, InpatientPharmacyService ip, CancellationToken ct) =>
                (await ip.ReturnAsync(id, r, ct)).ToCreated(d => $"/api/v1/pharmacy/ip-issues/{id}"))
            .RequirePermission(PharmacyPermissions.IpIssue).WithValidation<WardReturnRequest>();
    }

    /// <summary>Open indents first — STAT before routine, oldest first — so the IP counter works the list top-down.</summary>
    private static async Task<IResult> ListIndents(
        [AsParameters] PageQuery query, WardIndentStatus? status, Guid? admissionId, bool? includeClosed, HospitalDbContext db, IFieldProtector protector, CancellationToken ct)
    {
        var page = query.ToRequest();
        var indents = db.Set<WardIndent>().AsNoTracking();
        if (status is not null)
        {
            indents = indents.Where(i => i.Status == status);
        }
        else if (includeClosed != true)
        {
            indents = indents.Where(i => i.Status == WardIndentStatus.Pending || i.Status == WardIndentStatus.PartiallyIssued);
        }

        if (admissionId is not null)
        {
            indents = indents.Where(i => i.AdmissionId == admissionId);
        }

        if (page.SearchTerm is { } term)
        {
            var upper = term.ToUpperInvariant();
            var patients = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            indents = indents.Where(i => i.Number == upper || patients.Contains(i.PatientId));
        }

        var result = await (
                from i in indents
                join p in db.Set<Patient>() on i.PatientId equals p.Id
                // STAT first (ranked explicitly: enums are stored as text, so a plain sort would be alphabetical).
                orderby i.Urgency == IndentUrgency.Stat descending, i.RaisedAt
                select new WardIndentListItem(
                    i.Id, i.Number, i.PatientId, p.FullName, p.Uhid, i.AdmissionId, i.WardName, i.BedLabel, i.Urgency, i.Status, i.RaisedAt,
                    db.Set<User>().Where(u => u.Id == i.RaisedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                    i.Lines.Count))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> GetIndent(
        Guid id,
        Guid? storeId,
        HospitalDbContext db,
        PatientAccessGuard guard,
        PatientContactAccess contacts,
        IAuditLogger audit,
        TimeProvider time,
        CancellationToken ct)
    {
        var indent = await db.Set<WardIndent>().AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (indent is null)
        {
            return Error.NotFound(nameof(WardIndent), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(indent.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(indent.PatientId, $"Viewed ward indent {indent.Number}", ct);
        var today = time.Today();
        var summaries = await PatientQueries.SummariesAsync(db, [indent.PatientId], today, await contacts.FullAsync(ct), ct);
        if (!summaries.TryGetValue(indent.PatientId, out var patient))
        {
            return Error.NotFound(nameof(Patient), indent.PatientId).ToProblem();
        }

        var raisedBy = await db.Set<User>().Where(u => u.Id == indent.RaisedByUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        var stock = storeId is { } store
            ? await InventoryQueries.AvailabilityAsync(db, store, indent.Lines.Select(l => l.ItemId).ToList(), today, ct)
            : new Dictionary<Guid, StockAvailability>();
        return TypedResults.Ok(new WardIndentDetail(
            indent.Id, indent.Number, patient, indent.AdmissionId, indent.VisitId, indent.WardName, indent.BedLabel, indent.Urgency, indent.Status,
            indent.RaisedAt, raisedBy, indent.PrescriptionId, indent.Remarks, indent.CancelReason,
            indent.Lines.OrderBy(l => l.ItemName).Select(l => new WardIndentLineDto(
                l.Id, l.ItemId, l.ItemName, l.RequestedQuantity, l.IssuedQuantity, l.RemainingQuantity, l.Remarks, stock.GetValueOrDefault(l.ItemId)?.Available)).ToList(),
            indent.Version));
    }

    private static async Task<IResult> ListIssues(
        [AsParameters] PageQuery query, Guid? admissionId, Guid? patientId, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var issues = db.Set<IpIssue>().AsNoTracking();
        if (admissionId is not null)
        {
            issues = issues.Where(i => i.AdmissionId == admissionId);
        }

        if (patientId is not null)
        {
            issues = issues.Where(i => i.PatientId == patientId);
        }

        if (page.SearchTerm is { } term)
        {
            var upper = term.ToUpperInvariant();
            issues = issues.Where(i => i.Number == upper);
        }

        var result = await (
                from i in issues
                join p in db.Set<Patient>() on i.PatientId equals p.Id
                orderby i.IssuedAt descending
                select new IpIssueListItem(
                    i.Id, i.Number, i.PatientId, p.FullName, p.Uhid, i.AdmissionId, i.WardIndentId, i.IssuedAt, i.TotalAmount, i.Lines.Count,
                    db.Set<User>().Where(u => u.Id == i.IssuedByUserId).Select(u => u.DisplayName).FirstOrDefault()))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> GetIssue(
        Guid id, HospitalDbContext db, PatientAccessGuard guard, PatientContactAccess contacts, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var issue = await db.Set<IpIssue>().AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (issue is null)
        {
            return Error.NotFound(nameof(IpIssue), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(issue.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(issue.PatientId, $"Viewed IP pharmacy issue {issue.Number}", ct);
        var summaries = await PatientQueries.SummariesAsync(db, [issue.PatientId], time.Today(), await contacts.FullAsync(ct), ct);
        if (!summaries.TryGetValue(issue.PatientId, out var patient))
        {
            return Error.NotFound(nameof(Patient), issue.PatientId).ToProblem();
        }

        var issuedBy = await db.Set<User>().Where(u => u.Id == issue.IssuedByUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        var returns = await db.Set<WardReturn>().AsNoTracking().Include(r => r.Lines)
            .Where(r => r.IpIssueId == id)
            .OrderBy(r => r.ReturnedAt)
            .ToListAsync(ct);
        return TypedResults.Ok(new IpIssueDetail(
            issue.Id, issue.Number, issue.StoreId, patient, issue.AdmissionId, issue.WardIndentId, issue.QueueEntryId, issue.IssuedAt, issuedBy,
            issue.TotalAmount,
            issue.Lines.OrderBy(l => l.ItemName).Select(l => new IpIssueLineDto(
                l.Id, l.ItemId, l.ItemName, l.BatchNumber, l.ExpiryDate, l.Quantity, l.UnitMrp, l.Amount, l.ReturnedQuantity, l.ChargeId, l.UnitPrice)).ToList(),
            returns.Select(r => new WardReturnDto(
                r.Id, r.Number, r.ReturnedAt, r.Reason, r.CreditAmount,
                r.Lines.Select(l => new WardReturnLineDto(l.IpIssueLineId, l.ItemName, l.BatchNumber, l.Quantity, l.CreditAmount, l.Restocked, l.ChargeReversal)).ToList()))
                .ToList()));
    }
}
