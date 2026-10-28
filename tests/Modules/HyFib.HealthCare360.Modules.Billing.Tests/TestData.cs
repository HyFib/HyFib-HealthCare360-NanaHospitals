using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>Builders for billing aggregates in a known state (pure domain objects, no database).</summary>
internal static class TestData
{
    public static readonly Guid FacilityId = Guid.CreateVersion7();
    public static readonly Guid PatientId = Guid.CreateVersion7();

    /// <summary>25 Sep 2026, 12:00 IST.</summary>
    public static readonly DateTimeOffset Now = new(2026, 9, 25, 6, 30, 0, TimeSpan.Zero);

    public static Charge Charge(
        decimal unitPrice,
        decimal quantity = 1m,
        ServiceCategory category = ServiceCategory.Procedure,
        decimal taxRate = 0m,
        bool discountAllowed = true,
        decimal tariffDiscountPercent = 0m,
        Guid? practitionerId = null,
        Guid? patientId = null,
        Guid? serviceItemId = null,
        DateTimeOffset? serviceDate = null,
        RoomCharge? room = null)
    {
        var charge = new Charge
        {
            FacilityId = FacilityId,
            PatientId = patientId ?? PatientId,
            ServiceItemId = serviceItemId ?? Guid.CreateVersion7(),
            ServiceCode = "SVC",
            Description = "Service",
            Category = category,
            Quantity = quantity,
            IsDiscountAllowed = discountAllowed,
            SourceModule = "tests",
            SourceReference = Guid.NewGuid().ToString("N"),
            PractitionerId = practitionerId,
            ServiceDate = serviceDate ?? Now,
            Status = ChargeStatus.Unbilled,
            Room = room,
        };
        charge.ApplyPrice(new PriceQuote(unitPrice, PriceSource.TariffPlan, 1m, null), taxRate, tariffDiscountPercent, null);
        return charge;
    }

    public static Bill DraftBill(BillPayerTerms? terms, params Charge[] charges)
    {
        var bill = Bill.Draft(FacilityId, BillType.OutPatient, false, PatientId, null, null, null, terms ?? BillPayerTerms.SelfPay(PayerType.Self, null));
        foreach (var charge in charges)
        {
            bill.AddCharge(charge, isPayerPayable: true, "999312").IsSuccess.ShouldBeTrue();
        }

        bill.Recalculate();
        return bill;
    }

    public static Bill FinalizedBill(params Charge[] charges)
    {
        var bill = DraftBill(null, charges);
        bill.Finalize("NHOB260000001", null, Now).IsSuccess.ShouldBeTrue();
        return bill;
    }

    public static BillLineInput Line(decimal gross, bool discountAllowed = true, decimal taxRate = 0m, bool payable = true, decimal lineDiscount = 0m, decimal covered = 0m, decimal tariffPercent = 0m) =>
        new(gross, covered, tariffPercent, lineDiscount, discountAllowed, taxRate, payable);
}

/// <summary>A clock frozen at a given instant.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
