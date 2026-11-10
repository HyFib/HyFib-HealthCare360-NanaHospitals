using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Notifications;
using PlatformApprovalStatus = HyFib.HealthCare360.Platform.Approvals.ApprovalStatus;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// How an employee's salary is paid: the mode, and for a bank transfer the bank, the account and the IFSC. The account and
/// the IFSC are kept only encrypted (<see cref="IFieldProtector"/>), here as on the employee, with their masked forms.
/// </summary>
public sealed record PaymentDetails(
    SalaryPaymentMode Mode, string? BankName, string? AccountCipher, string? AccountMasked, string? IfscCipher, string? IfscMasked)
{
    public static PaymentDetails Of(Employee employee)
    {
        ArgumentNullException.ThrowIfNull(employee);
        return new(employee.PaymentMode, employee.BankName, employee.BankAccountCipher, employee.BankAccountMasked, employee.BankIfscCipher, employee.BankIfscMasked);
    }

    /// <summary>"bank transfer to account XXXXXXXX1234 (SBIN0XXXXXX, State Bank of India)", "cash" or "cheque": never more than the last four digits.</summary>
    public string Words => Mode switch
    {
        SalaryPaymentMode.Cash => "cash",
        SalaryPaymentMode.Cheque => "cheque",
        _ when AccountMasked is null => "bank transfer, no account on record",
        _ => $"bank transfer to account {AccountMasked} ({IfscMasked}{(BankName is { Length: > 0 } bank ? ", " + bank : string.Empty)})",
    };
}

/// <summary>A change of payment details as asked: the employee, the details it replaces and the details asked for.</summary>
/// <param name="AccountIndex">The blind index of the account asked for, when the account changes (it is kept with the account).</param>
/// <param name="ConfirmSharedAccount">The maker confirmed the account is genuinely shared with another current employee.</param>
public sealed record PaymentDetailsPayload(Guid EmployeeId, PaymentDetails Before, PaymentDetails Asked, string? AccountIndex, bool ConfirmSharedAccount);

/// <summary>Payment details waiting for a second person: the request, the details asked for (masked), who asked and when.</summary>
public sealed record PendingPaymentDetails(
    Guid ApprovalId, SalaryPaymentMode PaymentMode, string? BankName, string? BankAccountMasked, string? BankIfscMasked, string Summary, string RequestedBy,
    DateTimeOffset RequestedAt);

/// <summary>What changes in an employee's payment details, in words that show no more than an account's last four digits.</summary>
public static class PaymentDetailsChange
{
    /// <summary>"cash → bank transfer to account XXXXXXXX1234 (SBIN0XXXXXX)", or "unchanged".</summary>
    public static string Describe(PaymentDetails before, PaymentDetails after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return before.Words == after.Words && before.AccountCipher == after.AccountCipher && before.IfscCipher == after.IfscCipher
            ? "unchanged"
            : $"{before.Words} → {after.Words}";
    }

    /// <summary>
    /// The details are the same, reading the account and the IFSC through <paramref name="unprotect"/>: a value encrypted
    /// again under a new key is the same value.
    /// </summary>
    public static bool Same(PaymentDetails a, PaymentDetails b, Func<string?, string?> unprotect)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(unprotect);
        return a.Mode == b.Mode
               && string.Equals(a.BankName, b.BankName, StringComparison.Ordinal)
               && string.Equals(unprotect(a.AccountCipher), unprotect(b.AccountCipher), StringComparison.Ordinal)
               && string.Equals(unprotect(a.IfscCipher), unprotect(b.IfscCipher), StringComparison.Ordinal);
    }
}

/// <summary>
/// An employee's salary bank account, IFSC, bank and payment mode (maker-checker slice 11): the bank file pays whatever
/// account the employee has, so pointing the salary at another account, or switching it to cash, is the classic payroll
/// diversion. One HR user asks, with a reason; the change waits, and the details in force stay in force (paid, shown and
/// written to the bank file) until someone else who approves payroll (<see cref="HRPermissions.PayrollApprove"/>)
/// approves it: never the one who asked (<c>approval.self</c>) and never the employee whose pay it is
/// (<see cref="OwnCode"/>). Made only over the details as they were asked (<c>approval.stale</c>), audited with both names
/// and the account masked to its last four digits, and the employee is told.
/// </summary>
public sealed class PaymentDetailsApprovals(
    HospitalDbContext db, IFieldProtector protector, ICurrentUser current, IAuditLogger audit, INotificationService notifications, IApprovals approvals,
    TimeProvider time)
{
    public const string Kind = "hr.bank_details";

    /// <summary>Refused at approval: the employee whose salary it is does not approve it.</summary>
    public const string OwnCode = "hr.bank_details_own";

    /// <summary>The details waiting for approval for this employee, if any.</summary>
    public async Task<PendingPaymentDetails?> PendingAsync(Guid employeeId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == nameof(Employee) && r.SubjectId == employeeId && r.Status == PlatformApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new { r.Id, r.Payload, r.Summary, r.RequestedByName, r.RequestedAt })
            .FirstOrDefaultAsync(ct);
        if (request is null)
        {
            return null;
        }

        var asked = ApprovalPayload.Read<PaymentDetailsPayload>(request.Payload).Asked;
        return new PendingPaymentDetails(request.Id, asked.Mode, asked.BankName, asked.AccountMasked, asked.IfscMasked, request.Summary, request.RequestedByName,
            request.RequestedAt);
    }

    /// <summary>Asks a second person to pay <paramref name="employee"/> as <paramref name="asked"/>, in the caller's transaction.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(
        Employee employee, PaymentDetails asked, string? accountIndex, bool confirmSharedAccount, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(asked);
        var before = PaymentDetails.Of(employee);
        var words = PaymentDetailsChange.Describe(before, asked);
        List<ApprovalDetail> details =
        [
            new("Employee", $"{employee.FullName} ({employee.EmployeeCode})"),
            new("Salary paid by", words),
            new("What it reaches",
                "Where this employee's salary goes: the bank transfer file of every payroll approved after this change. Until it is approved the details in force stay in force, and the employee is told once it changes."),
        ];
        if (confirmSharedAccount)
        {
            details.Add(new("Shared account", "The person who asked confirmed this account is genuinely shared with another current employee."));
        }

        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(Employee), employee.Id, employee.Version, employee.FacilityId, null),
            new PaymentDetailsPayload(employee.Id, before, asked, accountIndex, confirmSharedAccount),
            Fit($"Salary payment details of {employee.FullName} ({employee.EmployeeCode}): {words}", 300),
            details,
            reason,
            ct);
    }

    /// <summary>
    /// Makes approved payment details, in the approval's transaction, if the employee still has the details the request
    /// replaces, and never by the employee whose salary it is.
    /// </summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var payload = ApprovalPayload.Read<PaymentDetailsPayload>(request.Payload);
        var employee = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == payload.EmployeeId, ct);
        if (employee is null)
        {
            return Error.Rule("employee.gone", "The employee no longer exists, so nothing was changed.");
        }

        if (employee.UserId is { } own && own == current.UserId)
        {
            return Error.Forbidden(OwnCode, "These are your own salary payment details, so someone else who approves payroll decides them.");
        }

        if (employee.IsSeparated)
        {
            return Error.Rule("employee.separated", $"{employee.FullName} has been separated, so nothing was changed.");
        }

        var before = PaymentDetails.Of(employee);
        if (!PaymentDetailsChange.Same(before, payload.Before, protector.Unprotect))
        {
            return Error.Rule("approval.stale",
                $"{employee.FullName}'s salary payment details changed after this was asked for (now {before.Words}), so nothing was changed. Ask again.");
        }

        var asked = payload.Asked;
        var accountChanges = !string.Equals(protector.Unprotect(before.AccountCipher), protector.Unprotect(asked.AccountCipher), StringComparison.Ordinal);
        if (accountChanges && !payload.ConfirmSharedAccount && protector.Unprotect(asked.AccountCipher) is { } account)
        {
            var indexes = protector.BlindIndexes(HrIdentifiers.Normalize(account)).ToArray();
            var holder = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(e => e.BankAccountIndex != null && indexes.Contains(e.BankAccountIndex) && e.Id != employee.Id && e.Status != EmployeeStatus.Separated)
                .Select(e => e.EmployeeCode).FirstOrDefaultAsync(ct);
            if (holder is not null)
            {
                return Error.Rule("employee.bank_account_shared",
                    $"Salary for current employee {holder} is now paid into this account, so nothing was changed. Ask again, confirming the account is shared if it genuinely is.");
            }
        }

        employee.PaymentMode = asked.Mode;
        employee.BankName = asked.BankName;
        employee.BankAccountCipher = asked.AccountCipher;
        employee.BankAccountMasked = asked.AccountMasked;
        employee.BankIfscCipher = asked.IfscCipher;
        employee.BankIfscMasked = asked.IfscMasked;
        if (accountChanges)
        {
            employee.BankAccountIndex = payload.AccountIndex;
        }

        var words = PaymentDetailsChange.Describe(before, asked);
        var approver = current.DisplayName ?? current.UserName ?? "Unknown user";
        audit.Add(new AuditEntry(AuditCategories.Security, "employee.bank_changed",
            Fit($"Salary payment details of {employee.EmployeeCode}: {words} ({request.Reason}; asked for by {request.RequestedByName}, approved by {approver})", 1000))
        {
            EntityType = nameof(Employee),
            EntityId = employee.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new
            {
                before = new { before.Mode, before.BankName, account = before.AccountMasked, ifsc = before.IfscMasked },
                after = new { asked.Mode, asked.BankName, account = asked.AccountMasked, ifsc = asked.IfscMasked },
                approval = request.Id,
                requestedBy = request.RequestedBy,
                approvedBy = current.UserId,
            },
        });

        // Payroll-diversion fraud usually starts with a quiet bank change, so the employee is always told.
        if (employee.UserId is { } userId && userId != request.RequestedBy)
        {
            await notifications.NotifyUsersAsync([userId], "Your salary payment details were changed",
                $"Your salary will now be paid by {asked.Words}. Contact HR immediately if you did not ask for this.", "/hr/me", "security", "warning", ct);
        }

        return words;
    }

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>An employee's salary payment details: one HR user asks, another who approves payroll decides (maker-checker).</summary>
internal sealed class PaymentDetailsApproval(PaymentDetailsApprovals details) : IApprovalAction
{
    public string Kind => PaymentDetailsApprovals.Kind;

    public string Title => "Salary bank account or payment mode";

    public string ApprovePermission => HRPermissions.PayrollApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => details.ApplyAsync(request, ct);
}
