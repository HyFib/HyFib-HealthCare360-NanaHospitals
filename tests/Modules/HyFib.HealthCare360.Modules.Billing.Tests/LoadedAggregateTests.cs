using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Scheduling;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// Rows that a domain method adds to an aggregate loaded from the database (a shift's closing totals, a claim's settlement
/// and deductions) must be inserted. EF took them for existing rows and updated them, so closing a shift that had taken
/// money, or settling a claim, failed as a concurrency conflict. Checked on the real EF model without a database: the
/// aggregate is attached as if it had been loaded, and the change tracker tells what the save would do.
/// </summary>
public sealed class LoadedAggregateTests
{
    [Fact]
    public void Closing_a_loaded_shift_inserts_its_totals()
    {
        using var db = ModelOnlyContext();
        var shift = CashierShift.Open(TestData.FacilityId, Guid.CreateVersion7(), "Cashier", "OP-1", 500m, TestData.Now);
        db.Attach(shift);

        var reconciliation = ShiftCalculator.Reconcile(
            500m, [new ShiftMovement(PaymentMode.Cash, 1_200m, false), new ShiftMovement(PaymentMode.Upi, 800m, false)], 1_700m);
        shift.Close(reconciliation, null, TestData.Now).IsSuccess.ShouldBeTrue();
        db.ChangeTracker.DetectChanges();

        db.Entry(shift).State.ShouldBe(EntityState.Modified);
        shift.Totals.Count.ShouldBe(2);
        shift.Totals.ShouldAllBe(t => db.Entry(t).State == EntityState.Added);
    }

    [Fact]
    public void Settling_a_loaded_claim_inserts_the_settlement_and_its_deductions()
    {
        using var db = ModelOnlyContext();
        var claim = new InsuranceClaim
        {
            FacilityId = TestData.FacilityId,
            ClaimNumber = "NHCL26000001",
            ClaimedAmount = 10_000m,
            Status = ClaimStatus.Approved,
            SubmittedAt = TestData.Now,
        };
        db.Attach(claim);

        claim.Settle(9_500m, [new DeductionInput("Non-medical items", 500m)], "UTR0001", null, new DateOnly(2026, 9, 25), TestData.Now)
            .IsSuccess.ShouldBeTrue();
        db.ChangeTracker.DetectChanges();

        db.Entry(claim).State.ShouldBe(EntityState.Modified);
        db.Entry(claim.Settlements.ShouldHaveSingleItem()).State.ShouldBe(EntityState.Added);
        db.Entry(claim.Deductions.ShouldHaveSingleItem()).State.ShouldBe(EntityState.Added);
    }

    [Fact]
    public void Entity_ids_are_assigned_by_the_domain_never_generated_by_ef()
    {
        using var db = ModelOnlyContext();

        var ids = db.Model.GetEntityTypes()
            .Where(t => typeof(Entity).IsAssignableFrom(t.ClrType) && !t.IsOwned())
            .Select(t => t.FindPrimaryKey()!.Properties)
            .Where(key => key is [{ Name: nameof(Entity.Id) }])
            .Select(key => key[0])
            .ToList();

        ids.Count.ShouldBeGreaterThan(40);
        ids.ShouldAllBe(id => id.ValueGenerated == ValueGenerated.Never);
    }

    /// <summary>
    /// The real model (Billing, the modules it references and the platform) on the PostgreSQL provider, without a
    /// database. EF caches one model per context type, so every test in this assembly that needs a context uses this one.
    /// </summary>
    private static HospitalDbContext ModelOnlyContext() => new(
        new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options,
        new ModuleCatalog([new PatientsModule(), new SchedulingModule(), new BillingModule()]));
}
