using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Billing.Services.EventHandlers;

// Doctor-fee accrual follows the bill in the same transaction, so a bill and its doctors' shares always agree.

/// <summary>Accrues doctors' shares when a bill is finalized (default trigger), or when it is born fully paid (payer-covered).</summary>
internal sealed class BillFinalizedDoctorFeeHandler(HospitalDbContext db, DoctorFeeService fees) : IDomainEventHandler<BillFinalized>
{
    public async Task HandleAsync(BillFinalized domainEvent, CancellationToken cancellationToken)
    {
        var bill = db.Set<Bill>().Local.FirstOrDefault(b => b.Id == domainEvent.BillId);
        if (bill is null)
        {
            return;
        }

        if (!await fees.AccruesOnPaymentAsync(bill.FacilityId, cancellationToken) || bill.Status == BillStatus.Paid)
        {
            await fees.AccrueAsync(bill, cancellationToken);
        }
    }
}

/// <summary>
/// With the "paid" trigger, accrues when the bill is settled in full — whichever way: counter, POS, gateway, or an IP or
/// patient advance adjusted against it (adjustments raise no PaymentReceived, so this listens to BillPaid).
/// </summary>
internal sealed class BillPaidDoctorFeeHandler(HospitalDbContext db, DoctorFeeService fees) : IDomainEventHandler<BillPaid>
{
    public async Task HandleAsync(BillPaid domainEvent, CancellationToken cancellationToken)
    {
        if (!await fees.AccruesOnPaymentAsync(domainEvent.FacilityId, cancellationToken))
        {
            return;
        }

        var bill = db.Set<Bill>().Local.FirstOrDefault(b => b.Id == domainEvent.BillId)
            ?? await db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(b => b.Id == domainEvent.BillId, cancellationToken);
        if (bill is not { Status: BillStatus.Paid })
        {
            return;
        }

        var lines = db.Entry(bill).Collection(b => b.Lines);
        if (!lines.IsLoaded && db.Entry(bill).State != EntityState.Added)
        {
            await lines.LoadAsync(cancellationToken);
        }

        await fees.AccrueAsync(bill, cancellationToken);
    }
}

/// <summary>Reverses doctors' shares of a cancelled bill (unpaid ones cancel out; paid ones are recovered).</summary>
internal sealed class BillCancelledDoctorFeeHandler(DoctorFeeService fees) : IDomainEventHandler<BillCancelled>
{
    public Task HandleAsync(BillCancelled domainEvent, CancellationToken cancellationToken) => fees.ReverseAsync(domainEvent.BillId, cancellationToken);
}
