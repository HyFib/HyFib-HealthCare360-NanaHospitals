using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Follows refunds paid out through the payment gateway. The refund counts as paid in HC360 as soon as the gateway
/// accepts it; the bank takes days to return the money, so this asks the gateway (hourly from the reconciliation job,
/// and at once when a refund webhook arrives) until it is processed or failed. A failed refund means the patient has
/// not got the money back: it is listed as an exception, accountants are alerted, and it can be sent again.
/// </summary>
public sealed class GatewayRefundTracker(
    HospitalDbContext db,
    RefundService refunds,
    GatewayDiscrepancyService discrepancies,
    INotificationService notifications,
    IAuditLogger audit,
    IServiceProvider services,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<GatewayRefundTracker> logger)
{
    /// <summary>Asks the gateway where the refund stands and records what changed. Returns true when it changed. Saves.</summary>
    internal async Task<bool> FollowAsync(Refund refund, CancellationToken ct)
    {
        if (refund.GatewayRefundId is not { } gatewayRefundId || !refund.IsGatewayRefundOpen || services.GetService<IPaymentGateway>() is not { } gateway)
        {
            return false;
        }

        RefundStatusResult status;
        try
        {
            status = await gateway.GetRefundStatusAsync(gatewayRefundId, refund.FacilityId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the status of gateway refund {GatewayRefundId}", gatewayRefundId);
            return false;
        }

        if (!refund.ApplyGatewayStatus(status, time.GetUtcNow()))
        {
            return false;
        }

        var failed = refund.GatewayStatus == PaymentRefundStatus.Failed;
        if (failed)
        {
            await discrepancies.RaiseAsync(
                DiscrepancyKind.RefundMismatch, gatewayRefundId, refund.FacilityId,
                $"Refund {refund.RefundNumber} of ₹{Money.FormatInr(refund.Amount)} failed at the gateway ({gatewayRefundId}): the patient has not got the money back. Try it again or pay it another way.",
                d =>
                {
                    d.RefundId = refund.Id;
                    d.BillId = refund.BillId;
                    d.IpAccountId = refund.IpAccountId;
                    d.RecordedAmount = refund.Amount;
                    d.GatewayAmount = status.Amount ?? refund.Amount;
                    d.GatewayAt = status.CreatedAt;
                },
                ct);
        }
        else if (refund.GatewayStatus == PaymentRefundStatus.Processed)
        {
            await discrepancies.ClearAsync(DiscrepancyKind.RefundMismatch, gatewayRefundId, "The gateway returned the money.", ct);
        }

        await db.SaveChangesAsync(ct);
        if (failed)
        {
            await notifications.NotifyPermissionHoldersAsync(
                BillingPermissions.GatewayReconcile,
                refund.FacilityId,
                "Online refund failed",
                $"{refund.RefundNumber}: ₹{Money.FormatInr(refund.Amount)} did not reach the patient. Try it again or pay it another way.",
                "/billing/reconciliation",
                "billing",
                "warning",
                ct);
        }

        return true;
    }

    /// <summary>
    /// Sends a refund the gateway failed again, for the same amount and to the same online payment (the money can only
    /// go back where it came from). The refund keeps its approval; the new gateway refund is followed like the first.
    /// The retry is claimed first, in its own save under the refund's concurrency token, so of two requests at once
    /// only one reaches the gateway; the gateway also gets an idempotency key for the attempt, so a retry taken over
    /// after one that stopped part way gets the refund already made instead of a second one. Audited. Saves.
    /// </summary>
    internal async Task<Result<string>> RetryAsync(Refund refund, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (!refund.CanSendGatewayRefundAgain(now) || refund.GatewayRefundId is not { } failedId)
        {
            return refund.ClaimGatewayRetry(now).Error!;
        }

        if (services.GetService<IPaymentGateway>() is not { } gateway)
        {
            return Error.Rule("billing.gateway_not_configured", "No payment gateway is configured. Pay the refund another way and resolve the exception.");
        }

        var paymentId = await refunds.GatewayPaymentForAsync(refund, ct, alreadyPaidOut: true);
        if (paymentId is null)
        {
            return Error.Rule("billing.gateway_payment_missing", "The online payment this refund reverses was not found. Pay it another way and resolve the exception.");
        }

        var failedError = refund.GatewayError;
        refund.ClaimGatewayRetry(now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Refund.RetryInProgress;
        }

        RefundResult result;
        try
        {
            result = await gateway.RefundAsync(
                paymentId, refund.Amount, refund.Reason, refund.GatewayIdempotencyKey, refund.FacilityId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whether the gateway made the refund is unknown: the claim stays, and a retry after it times out asks
            // again with the same idempotency key.
            logger.LogWarning(
                ex, "Sending refund {RefundNumber} through the gateway again got no answer", refund.RefundNumber);
            return Error.Rule(
                "billing.gateway_unavailable", "The payment gateway did not answer. Try again in a few minutes.");
        }

        if (!result.Success || string.IsNullOrWhiteSpace(result.RefundId))
        {
            refund.GatewayRetryRefused(result.Error ?? failedError);
            await db.SaveChangesAsync(ct);
            return Error.Rule("billing.gateway_refund_failed", result.Error ?? "The payment gateway declined the refund.");
        }

        refund.StartGatewayRefund(result.RefundId, time.GetUtcNow());
        refund.Reference = result.RefundId.Length <= 64 ? result.RefundId : refund.Reference;
        if (await FindDiscrepancyAsync(failedId, ct) is { } open)
        {
            open.Clear($"Sent again through the gateway as {result.RefundId}.", time.GetUtcNow(), currentUser.UserId, currentUser.DisplayName ?? currentUser.UserName);
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "billing.refund.gateway_retried",
            $"Sent refund {refund.RefundNumber} of ₹{Money.FormatInr(refund.Amount)} through the gateway again: {failedId} failed, {result.RefundId} sent")
        {
            PatientId = refund.PatientId,
            EntityType = nameof(Refund),
            EntityId = refund.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // The gateway has already accepted the money back; make the reconciliation trail impossible to miss. The
            // claim stays, so a retry after it times out records the same gateway refund (same idempotency key).
            logger.LogCritical(ex, "Gateway refund {GatewayRefundId} for {RefundNumber} was sent again but could not be recorded", result.RefundId, refund.RefundNumber);
            throw;
        }

        return result.RefundId;
    }

    private async Task<GatewayDiscrepancy?> FindDiscrepancyAsync(string gatewayRefundId, CancellationToken ct) =>
        await db.Set<GatewayDiscrepancy>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(d => d.Kind == DiscrepancyKind.RefundMismatch && d.Key == gatewayRefundId && d.Status == DiscrepancyStatus.Open, ct);
}
