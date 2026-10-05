using System.Net;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The Tally day book's safety rails against the real database (INT-TALLY): concurrent saves of a day conflict instead of
/// overwriting each other, "mark imported" waits for a post in progress, a day left Queued is queued again, and posting
/// a day from before per-voucher tracking posts the days of the bills cancelled on it again. Each test works on dates in
/// 2024 of its own, so no other test's activity reaches them.
/// </summary>
public sealed class TallyPostingTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_saves_of_a_day_from_the_same_reading_conflict_instead_of_overwriting()
    {
        var facilityId = await FacilityAsync();
        var day = await AddDayAsync(facilityId, new DateOnly(2024, 2, 1), TallyExportStatus.Failed);

        await using var first = server.Services.CreateAsyncScope();
        await using var second = server.Services.CreateAsyncScope();
        var a = await Db(first).Set<TallyExport>().SingleAsync(e => e.Id == day, Ct);
        var b = await Db(second).Set<TallyExport>().SingleAsync(e => e.Id == day, Ct);
        a.Message = "Saved by a post";
        await Db(first).SaveChangesAsync(Ct);
        b.Message = "Saved by a check";

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => Db(second).SaveChangesAsync(Ct));
        BillingDb.IsConflict(new DbUpdateConcurrencyException()).ShouldBeTrue();
    }

    [Fact]
    public async Task A_day_being_posted_cannot_be_marked_imported()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = await FacilityAsync(admin);
        await AddDayAsync(facilityId, new DateOnly(2024, 2, 2), TallyExportStatus.Queued, fileFingerprint: new string('0', 64));

        using var refused = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/billing/tally/days/2024-02-02/imported");

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.tally_posting");
    }

    [Fact]
    public async Task A_day_left_queued_is_queued_again_by_the_hourly_job()
    {
        var facilityId = await FacilityAsync();
        var queuedAt = DateTimeOffset.UtcNow - TallyDayBookScheduler.StuckAfter - TimeSpan.FromMinutes(5);
        var day = await AddDayAsync(facilityId, new DateOnly(2024, 2, 3), TallyExportStatus.Queued, queuedAt: queuedAt);
        var scheduler = server.Services.GetServices<IHostedService>().OfType<TallyDayBookScheduler>().Single();

        (await scheduler.RequeueStuckAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);

        await using var scope = server.Services.CreateAsyncScope();
        var export = await Db(scope).Set<TallyExport>().AsNoTracking().SingleAsync(e => e.Id == day, Ct);
        export.QueuedAt!.Value.ShouldBeGreaterThan(queuedAt + TallyDayBookScheduler.StuckAfter);
        export.RunId.ShouldNotBeNull();
    }

    [Fact]
    public async Task Posting_a_day_from_before_tracking_posts_the_days_of_the_bills_cancelled_on_it_again()
    {
        var facilityId = await FacilityAsync();
        var billed = new DateOnly(2024, 2, 10);
        var cancelled = new DateOnly(2024, 2, 12);
        var number = await AddCancelledBillAsync(facilityId, billed, cancelled);
        var started = DateTimeOffset.UtcNow;
        await AddDayAsync(facilityId, billed, TallyExportStatus.Posted, legacy: true, queuedAt: started.AddDays(-30));
        var cancellationDay = await AddDayAsync(facilityId, cancelled, TallyExportStatus.Posted, legacy: true, queuedAt: started.AddDays(-30));
        var runId = Guid.CreateVersion7();
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var export = await Db(scope).Set<TallyExport>().SingleAsync(e => e.Id == cancellationDay, Ct);
            export.StartRun(runId, DateTimeOffset.UtcNow);
            await Db(scope).SaveChangesAsync(Ct);
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            try
            {
                await scope.ServiceProvider.GetRequiredService<TallyService>().PostAsync(new TallyPostMessage(facilityId, cancelled, runId), Ct);
            }
            catch (InvalidOperationException)
            {
                // Whether Tally is set up here does not matter: the bill's day is queued before anything is sent.
            }
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = Db(scope);
            var billDay = await db.Set<TallyExport>().AsNoTracking().SingleAsync(e => e.FacilityId == facilityId && e.Date == billed, Ct);
            billDay.QueuedAt!.Value.ShouldBeGreaterThanOrEqualTo(started);
            var messages = await db.Set<OutboxMessage>().AsNoTracking()
                .Where(m => m.Type == TallyDayBookHandler.MessageType && m.FacilityId == facilityId && m.CreatedAt >= started)
                .ToListAsync(Ct);
            messages.ShouldContain(m => m.Read<TallyPostMessage>().Date == billed);
            var cancellation = await db.Set<TallyExport>().AsNoTracking().SingleAsync(e => e.Id == cancellationDay, Ct);
            cancellation.Message!.ShouldContain("10-Feb-2024");
            var records = await db.Set<TallyVoucherRecord>().AsNoTracking().Where(r => r.ExportId == cancellationDay).Select(r => r.Key).ToListAsync(Ct);
            records.ShouldContain(TallyVoucherBuilder.CancellationNumber(number));
        }
    }

    private static HospitalDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<HospitalDbContext>();

    private async Task<Guid> FacilityAsync(ApiClient? admin = null)
    {
        admin ??= await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        return (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId ?? throw new InvalidOperationException("The administrator has no facility.");
    }

    /// <summary>The day's posting record as the test needs it, replacing one left by an earlier run of the test.</summary>
    private async Task<Guid> AddDayAsync(
        Guid facilityId, DateOnly date, TallyExportStatus status, bool legacy = false, DateTimeOffset? queuedAt = null, string? fileFingerprint = null)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = Db(scope);
        await db.Set<TallyExport>().Where(e => e.FacilityId == facilityId && e.Date == date).ExecuteDeleteAsync(Ct);
        var export = new TallyExport
        {
            FacilityId = facilityId,
            Date = date,
            Status = status,
            CreatedAt = queuedAt ?? DateTimeOffset.UtcNow,
            QueuedAt = queuedAt ?? DateTimeOffset.UtcNow,
            RunId = Guid.CreateVersion7(),
            LegacyReferences = legacy,
            FileFingerprint = fileFingerprint,
            FileDownloadedAt = fileFingerprint is null ? null : DateTimeOffset.UtcNow,
        };
        db.Add(export);
        await db.SaveChangesAsync(Ct);
        return export.Id;
    }

    /// <summary>An OP bill finalised at noon on one day and cancelled at noon on a later one; returns its number.</summary>
    private async Task<string> AddCancelledBillAsync(Guid facilityId, DateOnly billed, DateOnly cancelled)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = Db(scope);
        var patientId = await db.Set<Patient>().IgnoreQueryFilters().Select(p => p.Id).FirstAsync(Ct);
        var number = "ITOB" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.Add(new Bill
        {
            FacilityId = facilityId,
            BillNumber = number,
            Type = BillType.OutPatient,
            Status = BillStatus.Cancelled,
            PatientId = patientId,
            GrossAmount = 500m,
            NetAmount = 500m,
            PatientAmount = 500m,
            FinalizedAt = BillingTime.StartOfDay(billed).AddHours(12),
            CancelledAt = BillingTime.StartOfDay(cancelled).AddHours(12),
        });
        await db.SaveChangesAsync(Ct);
        return number;
    }
}
