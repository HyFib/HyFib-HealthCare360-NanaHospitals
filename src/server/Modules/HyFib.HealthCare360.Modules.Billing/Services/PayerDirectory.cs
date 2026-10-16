using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// The payer's name and the patient's policy with it for the patient banner (<see cref="IPayerDirectory"/>, UX-04). It
/// shows each part only to staff whose permissions would let them read it on the insurance screens: the name to
/// payer readers, the masked policy number and its validity to policy readers.
/// </summary>
internal sealed class PayerDirectory(HospitalDbContext db, ICurrentUser user, IPermissionService permissions, TimeProvider time) : IPayerDirectory
{
    /// <summary>Who may look a payer up (as <c>GET /api/v1/insurance/payers/{id}</c> allows).</summary>
    internal static readonly string[] PayerReaders =
    [
        BillingPermissions.PayersManage, BillingPermissions.PoliciesManage, BillingPermissions.PreAuthManage, BillingPermissions.ClaimsManage,
        BillingPermissions.OpCreate, BillingPermissions.IpManage, BillingPermissions.ChargesRead,
    ];

    /// <summary>Who may read a patient's policies (as <c>GET /api/v1/insurance/policies</c> allows).</summary>
    internal static readonly string[] PolicyReaders =
    [
        BillingPermissions.PoliciesManage, BillingPermissions.PreAuthManage, BillingPermissions.ClaimsManage, BillingPermissions.IpManage, BillingPermissions.OpCreate,
    ];

    public async Task<PayerCoverageSummary> GetCoverageAsync(Guid patientId, Guid payerId, CancellationToken cancellationToken)
    {
        var name = await HasAnyAsync(PayerReaders, cancellationToken)
            ? await db.Set<Payer>().AsNoTracking().Where(p => p.Id == payerId).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken)
            : null;
        if (!await HasAnyAsync(PolicyReaders, cancellationToken))
        {
            return new PayerCoverageSummary(name, null, null);
        }

        // The policy billing would price today's bill with (PayerCoverageService): active, in date, the longest valid.
        var today = BillingTime.Today(time);
        var policy = await db.Set<PatientPolicy>().AsNoTracking()
            .Where(p => p.PatientId == patientId && p.PayerId == payerId && p.IsActive && p.ValidFrom <= today && p.ValidTo >= today)
            .OrderByDescending(p => p.ValidTo)
            .Select(p => new { p.PolicyNumberMasked, p.ValidTo })
            .FirstOrDefaultAsync(cancellationToken);
        return new PayerCoverageSummary(name, policy?.PolicyNumberMasked, policy?.ValidTo);
    }

    private async Task<bool> HasAnyAsync(string[] permissionNames, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return false;
        }

        var granted = await permissions.GetPermissionsAsync(userId, ct);
        return permissionNames.Any(granted.Contains);
    }
}
