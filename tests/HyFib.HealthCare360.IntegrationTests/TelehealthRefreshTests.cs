using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A video consultation's screen that stayed connected while Redis was away (several servers, SCAL-02) missed what the
/// telehealth hub sent it meanwhile, a chat message for one. Told to catch up (<c>resync</c>), it reads the consultation
/// again as joining shows it, without joining again: joining again would make it another participant.
/// </summary>
public sealed class TelehealthRefreshTests(HospitalServer server)
{
    private const string HubPath = "/hubs/telehealth";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_call_screen_that_missed_messages_reads_the_consultation_again_without_joining_again()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var practitionerId = (await doctor.GetAsync<System.Text.Json.JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var slot = await TeleconsultationJourneyTests.FreeVideoSlotAsync(frontOffice, practitionerId);
        var appointment = await frontOffice.PostAsync<AppointmentDetail>("/api/v1/scheduling/appointments", new BookAppointmentRequest(
            patient.Id, practitionerId, slot.StartsAt, AppointmentChannel.Phone, AppointmentVisitType.New, IsTelehealth: true,
            "Review over video", Notes: null));
        var day = DateOnly.FromDateTime(slot.StartsAt.ToOffset(TimeSpan.FromMinutes(330)).DateTime);
        var sessionId = (await doctor.GetAsync<List<TelehealthSessionListItem>>($"/api/v1/telehealth/sessions?date={day:O}&practitionerId={practitionerId}"))
            .Single(s => s.AppointmentId == appointment.Id).Id;

        await using var hub = await LongPollingHub.ConnectAsync(doctor, HubPath);
        (await hub.InvokeAsync("Refresh", sessionId)).ShouldNotBeNull().ShouldContain("Join the consultation first.");
        (await hub.InvokeAsync("JoinAsClinician", sessionId)).ShouldBeNull();

        // A message saved while its push to the call was lost.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            db.Add(TelehealthMessage.Create(sessionId, TelehealthRole.Patient, null, "Patient", "Can you hear me now?", DateTimeOffset.UtcNow).Value);
            await db.SaveChangesAsync(Ct);
        }

        var refreshed = await hub.InvokeForResultAsync("Refresh", sessionId);
        refreshed.GetProperty("admitted").GetBoolean().ShouldBeTrue();
        refreshed.GetProperty("recentMessages").EnumerateArray().Select(m => m.GetProperty("text").GetString())
            .ShouldContain("Can you hear me now?");

        // Still the one participant.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<TelehealthParticipant>()
                .CountAsync(p => p.SessionId == sessionId, Ct)).ShouldBe(1);
        }
    }
}
