using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.Inpatient;

/// <summary>/api/v1/billing/ip-accounts — in-patient billing (IPB-01..08): running bill, packages, interim/final bills, settlement, clearance.</summary>
internal static class IpEndpoints
{
    private static readonly string[] Readers = [BillingPermissions.IpManage, BillingPermissions.ChargesRead];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/ip-accounts").WithTags("Billing - In-patient");
        group.MapGet("/", List).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(Readers);
        group.MapGet("/by-admission/{admissionId:guid}", GetByAdmission).RequireAnyPermission(Readers);
        group.MapPut("/{id:guid}", Update).RequirePermission(BillingPermissions.IpManage).WithValidation<UpdateIpAccountRequest>();
        group.MapGet("/{id:guid}/running-bill", RunningBill).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}/package-status", PackageStatus).RequireAnyPermission(Readers);
        group.MapPut("/{id:guid}/package", AssignPackage).RequirePermission(BillingPermissions.IpManage).WithValidation<AssignPackageRequest>();
        group.MapPost("/{id:guid}/interim-bills", CreateInterimBill).RequirePermission(BillingPermissions.IpManage).WithValidation<CreateInterimBillRequest>();
        group.MapPost("/{id:guid}/final-bill", PrepareFinalBill).RequirePermission(BillingPermissions.IpManage);
        group.MapPost("/{id:guid}/settle", Settle).RequirePermission(BillingPermissions.IpManage);
        group.MapGet("/{id:guid}/clearance", Clearance).RequireAnyPermission(Readers);
        group.MapPost("/{id:guid}/discharge-approval", ApproveDischargeWithDues)
            .RequirePermission(BillingPermissions.DiscountsApprove)
            .WithValidation<ApproveDischargeWithDuesRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query, IpAccountStatus? status, HospitalDbContext db, IFieldProtector protector, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var accounts = db.Set<IpAccount>().AsNoTracking();
        accounts = status is null
            ? accounts.Where(a => a.Status == IpAccountStatus.Open || a.Status == IpAccountStatus.DischargeInitiated || a.Status == IpAccountStatus.Discharged)
            : accounts.Where(a => a.Status == status);
        if (page.SearchTerm is { } term)
        {
            var number = term.ToUpperInvariant();
            var patients = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            accounts = accounts.Where(a => a.AdmissionNumber == number || patients.Contains(a.PatientId));
        }

        var result = await accounts
            .OrderByDescending(a => a.AdmittedAt)
            .Select(a => new
            {
                a.Id,
                a.AdmissionId,
                a.AdmissionNumber,
                a.PatientId,
                a.PayerType,
                a.Status,
                a.AdmittedAt,
                a.DischargedAt,
                a.EstimatedAmount,
                DepositBalance = a.DepositReceived - a.DepositAdjusted - a.DepositRefunded,
                HasPackage = a.PackageId != null,
                PayerName = db.Set<Payer>().Where(p => p.Id == a.PayerId).Select(p => p.Name).FirstOrDefault(),
            })
            .ToPagedResultAsync(page, ct);
        var summaries = await PatientQueries.SummariesAsync(db, result.Items.Select(a => a.PatientId), BillingTime.Today(time), fullContacts: false, ct);
        return TypedResults.Ok(result.Map(a =>
        {
            var patient = summaries.GetValueOrDefault(a.PatientId);
            return new IpAccountListItem(
                a.Id, a.AdmissionId, a.AdmissionNumber, a.PatientId, patient?.Uhid, patient?.FullName, a.PayerType, a.PayerName, a.Status, a.AdmittedAt,
                a.DischargedAt, a.EstimatedAmount, a.DepositBalance, a.HasPackage);
        }));
    }

    private static async Task<IResult> Get(Guid id, IpBillingService ip, CancellationToken ct) => (await ip.DetailAsync(id, ct)).ToOk();

    private static async Task<IResult> GetByAdmission(Guid admissionId, HospitalDbContext db, IpBillingService ip, CancellationToken ct)
    {
        var id = await db.Set<IpAccount>().Where(a => a.AdmissionId == admissionId).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
        return id is null ? Error.NotFound(nameof(IpAccount), admissionId).ToProblem() : (await ip.DetailAsync(id.Value, ct)).ToOk();
    }

    private static async Task<IResult> Update(Guid id, UpdateIpAccountRequest request, IpBillingService ip, CancellationToken ct) =>
        (await ip.UpdateAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> RunningBill(Guid id, IpBillingService ip, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var patientId = await db.Set<IpAccount>().Where(a => a.Id == id).Select(a => (Guid?)a.PatientId).FirstOrDefaultAsync(ct);
        if (patientId is null)
        {
            return Error.NotFound(nameof(IpAccount), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(patientId.Value, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var bill = await ip.RunningBillAsync(id, ct);
        if (bill.IsSuccess)
        {
            await audit.PatientRecordAccessedAsync(patientId.Value, "Viewed IP running bill", ct);
        }

        return bill.ToOk();
    }

    private static async Task<IResult> PackageStatus(Guid id, IpBillingService ip, CancellationToken ct) => (await ip.PackageStatusAsync(id, ct)).ToOk();

    private static async Task<IResult> AssignPackage(Guid id, AssignPackageRequest request, IpBillingService ip, CancellationToken ct) =>
        (await ip.AssignPackageAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> CreateInterimBill(Guid id, CreateInterimBillRequest request, IpBillingService ip, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        await CreatedBillAsync(await ip.CreateInterimBillAsync(id, request, ct), db, time, ct);

    private static async Task<IResult> PrepareFinalBill(Guid id, IpBillingService ip, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        await CreatedBillAsync(await ip.PrepareFinalBillAsync(id, ct), db, time, ct);

    private static async Task<IResult> Settle(Guid id, IpBillingService ip, CancellationToken ct) => (await ip.SettleAsync(id, ct)).ToOk();

    private static async Task<IResult> Clearance(Guid id, IpBillingService ip, CancellationToken ct) => (await ip.ClearanceAsync(id, ct)).ToOk();

    private static async Task<IResult> ApproveDischargeWithDues(Guid id, ApproveDischargeWithDuesRequest request, IpBillingService ip, CancellationToken ct) =>
        (await ip.ApproveDischargeWithDuesAsync(id, request.Reason, request.Version, ct)).ToNoContent();

    private static async Task<IResult> CreatedBillAsync(Result<Guid> created, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        created.IsSuccess
            ? TypedResults.Created($"/api/v1/billing/bills/{created.Value}", await BillQueries.DetailAsync(db, created.Value, time, ct))
            : created.Error!.ToProblem();
}
