using System.Net;
using System.Net.Http.Json;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Nursing;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Downtime;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The pre-downtime pack over the real API: printed on demand with every module's part (census, medication sheets,
/// pending orders, OT list, downtime numbers, back-entry windows), kept, read back as the latest, made each night by the
/// job; and a nursing note older than the nursing chart's window refused until a second person approves a late
/// back-entry.
/// </summary>
public sealed class DowntimePackTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_pack_is_printed_with_every_part_kept_and_read_back()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var (patient, stay) = await DischargeStays.AdmitAsync(server, frontOffice, doctor);
        var block = await mrd.PostAsync<DowntimeNumberBlockDetail>("/api/v1/downtime/number-blocks",
            new IssueDowntimeNumbersRequest(DowntimeNumberKind.Receipt, "Pack test counter", 5));

        // The front office has no business with ward medication sheets.
        using (var refused = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/downtime/pack", new { }))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var pack = await nurse.PostAsync<DowntimePack>("/api/v1/downtime/pack", new { });
        pack.Sections.Select(s => s.Key).ShouldBe(["census", "mar", "orders", "ot", "numbers", "windows"]);
        (pack.WindowEndsAt - pack.GeneratedAt).ShouldBe(TimeSpan.FromHours(24));

        var census = pack.Sections.Single(s => s.Key == "census");
        var row = census.Tables.SelectMany(t => t.Rows).Single(r => r[1].Contains(patient.Uhid, StringComparison.Ordinal));
        row[3].ShouldBe("Community-acquired pneumonia");
        row[4].ShouldBe("ALLERGIES NOT RECORDED");
        pack.Sections.Single(s => s.Key == "mar").Tables.ShouldContain(t => t.Heading.Contains(patient.Uhid, StringComparison.Ordinal));
        pack.Sections.Single(s => s.Key == "numbers").Tables[0].Rows
            .ShouldContain(r => r[0] == "Pack test counter" && r[3] == block.Numbers[0].Number && r[4] == "5");
        pack.Sections.Single(s => s.Key == "windows").Tables[0].Rows.ShouldContain(r => r[0] == "Visit from paper" && r[1] == "3 days");

        // Kept: the latest (this print, or a later one) is read back for the offline copy.
        var latest = await nurse.GetAsync<DowntimePack>("/api/v1/downtime/pack/latest");
        latest.GeneratedAt.ShouldBeGreaterThanOrEqualTo(pack.GeneratedAt.AddSeconds(-1));

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<DowntimePackSnapshot>().AsNoTracking().AnyAsync(s => s.Id == pack.Id && s.Kind == DowntimePackKind.OnDemand, Ct)).ShouldBeTrue();
            (await db.Set<HyFib.HealthCare360.Platform.Auditing.AuditEvent>().AsNoTracking()
                .AnyAsync(a => a.Action == "patients.downtime_pack_printed" && a.EntityId == pack.Id.ToString(), Ct)).ShouldBeTrue();
        }

        await DischargeStays.CancelAsync(frontOffice, nurse, stay.Id);
    }

    [Fact]
    public async Task The_nightly_job_makes_a_pack_for_each_facility()
    {
        var job = server.Services.GetServices<IHostedService>().OfType<DowntimePackJob>().Single();
        var started = DateTimeOffset.UtcNow;
        (await job.RunAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var nightly = await db.Set<DowntimePackSnapshot>().AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.Kind == DowntimePackKind.Nightly && s.GeneratedAt >= started.AddSeconds(-1))
            .ToListAsync(Ct);
        nightly.ShouldNotBeEmpty();
        nightly.ShouldAllBe(s => s.GeneratedByName == null);
    }

    [Fact]
    public async Task A_nursing_note_older_than_its_window_waits_for_a_late_back_entry_approval()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var (_, stay) = await DischargeStays.AdmitAsync(server, frontOffice, doctor);
        var path = $"/api/v1/nursing/admissions/{stay.Id}/notes";
        var writtenFor = DateTimeOffset.UtcNow.AddHours(-30);

        // Within the 24-hour window, as before.
        using (var recent = await nurse.SendRawAsync(HttpMethod.Post, path, new AddNursingNoteRequest(NursingNoteCategory.General, "Comfortable overnight.", DateTimeOffset.UtcNow.AddHours(-2))))
        {
            recent.StatusCode.ShouldBe(HttpStatusCode.Created, await recent.Content.ReadAsStringAsync(Ct));
        }

        // Thirty hours back is past it: refused, saying how to ask.
        using (var late = await nurse.SendRawAsync(HttpMethod.Post, path, new AddNursingNoteRequest(NursingNoteCategory.General, "Paper note from the downtime.", writtenFor)))
        {
            var body = await late.Content.ReadAsStringAsync(Ct);
            late.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
            body.ShouldContain("late back-entry");
        }

        using var asked = await nurse.SendRawAsync(HttpMethod.Post, "/api/v1/downtime/back-entry/late",
            new RequestLateBackEntry(BackEntryRecord.NursingChart, writtenFor.AddHours(-1), writtenFor.AddHours(1), 3, "Thirty-hour outage; ward 3 paper notes"));
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted, await asked.Content.ReadAsStringAsync(Ct));
        var approval = (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
        var seen = await mrd.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approval.Id}");
        await mrd.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approval.Id}/approve", new ApproveRequest(null, seen.Version));

        using (var entered = await nurse.SendRawAsync(HttpMethod.Post, path, new AddNursingNoteRequest(NursingNoteCategory.General, "Paper note from the downtime.", writtenFor)))
        {
            entered.StatusCode.ShouldBe(HttpStatusCode.Created, await entered.Content.ReadAsStringAsync(Ct));
        }

        await DischargeStays.CancelAsync(frontOffice, nurse, stay.Id);
    }
}
