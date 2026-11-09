using System.Text.Json;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;
using HyFib.HealthCare360.Modules.Scheduling.Features.Availability;
using HyFib.HealthCare360.Modules.Scheduling.Services;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The five-minute reminder pass (OA-04) and the desk work the same bookings. A reminder sent must not change the
/// appointment the desk holds: the appointment's version is its row's xmin, so a pass that wrote to the row made the
/// desk's check-in or change fail with "concurrency" for nothing the desk could see. The pass keeps what it sent in its
/// own table, once per reminder, however many passes (or job nodes) run.
/// </summary>
public sealed class AppointmentReminderRaceTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reminder_sent_between_the_desk_loading_a_booking_and_saving_it_leaves_the_desk_s_save_and_check_in_working()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);

        // Only today's bookings are checked in.
        var booked = await BookTodayAsync(frontOffice, patient.Id, practitionerId);

        // The desk opens the booking: the screen holds its version, and the server holds the row as check-in loads it.
        var held = await frontOffice.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{booked.Id}");
        await using var desk = server.Services.CreateAsyncScope();
        var deskDb = desk.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var loaded = await deskDb.Set<Appointment>().SingleAsync(a => a.Id == booked.Id, Ct);

        // A reminder pass lands in between, 90 minutes before the appointment: the 24-hour reminder, now pointless, is
        // marked skipped, and the 2-hour one is sent (or skipped too, for a booking made less than 2 hours ahead).
        await RemindAsync(booked.StartsAt.AddMinutes(-90), booked.Id, AppointmentReminder.TwoHoursBefore);

        // The desk's save goes through, and the version the screen holds is still the booking's.
        loaded.Confirm(DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
        await deskDb.SaveChangesAsync(Ct);
        var after = await frontOffice.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{booked.Id}");
        after.Status.ShouldBe(AppointmentStatus.Confirmed);
        var checkIn = await frontOffice.PostAsync<CheckInResult>($"/api/v1/scheduling/appointments/{booked.Id}/check-in", new CheckInAppointmentRequest());
        checkIn.AppointmentId.ShouldBe(booked.Id);

        // What the pass sent is still shown with the booking.
        var shown = await frontOffice.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{booked.Id}");
        shown.Reminder24hSentAt.ShouldNotBeNull("the 24-hour reminder was marked skipped");
        shown.Reminder2hSentAt.ShouldNotBeNull();
        held.Version.ShouldNotBe(0u);
    }

    [Fact]
    public async Task A_reminder_pass_leaves_the_appointment_row_as_the_desk_last_read_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var slot = await TeleconsultationJourneyTests.FreeVideoSlotAsync(frontOffice, practitionerId);
        var booked = await frontOffice.PostAsync<AppointmentDetail>("/api/v1/scheduling/appointments", new BookAppointmentRequest(
            patient.Id, practitionerId, slot.StartsAt, AppointmentChannel.Phone, AppointmentVisitType.New, IsTelehealth: true, "Follow-up of a rash", Notes: null));
        var held = await frontOffice.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{booked.Id}");

        await RemindAsync(slot.StartsAt.AddMinutes(-90), booked.Id, AppointmentReminder.TwoHoursBefore);

        (await frontOffice.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{booked.Id}")).Version
            .ShouldBe(held.Version, "a reminder is not a change to the booking");
    }

    [Fact]
    public async Task Each_reminder_goes_once_however_many_passes_run()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var slot = await VideoSlotAfterAsync(frontOffice, practitionerId, DateTimeOffset.UtcNow.AddHours(26));
        var booked = await frontOffice.PostAsync<AppointmentDetail>("/api/v1/scheduling/appointments", new BookAppointmentRequest(
            patient.Id, practitionerId, slot.StartsAt, AppointmentChannel.Phone, AppointmentVisitType.New, IsTelehealth: true, "Follow-up of a rash", Notes: null));

        // Booked more than a day ahead, so the 24-hour reminder is due. A day before: it goes, once, however often the
        // pass runs (two at the same moment, as two job nodes would).
        var dayBefore = slot.StartsAt.AddHours(-20);
        await RemindAsync(dayBefore, booked.Id, AppointmentReminder.DayBefore);
        await RemindAsync(dayBefore.AddMinutes(5));
        (await RemindersSentAsync(patient.Id)).ShouldBe(1, "one 24-hour reminder");
        var first = await frontOffice.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{booked.Id}");
        (first.Reminder24hSentAt.ShouldNotBeNull(), first.Reminder2hSentAt).ShouldBe((first.Reminder24hSentAt!.Value, null));

        // Two hours before: the 2-hour reminder goes, once; the 24-hour one is not sent again.
        var twoHoursBefore = slot.StartsAt.AddMinutes(-110);
        await RemindAsync(twoHoursBefore, booked.Id, AppointmentReminder.TwoHoursBefore);
        await RemindAsync(twoHoursBefore.AddMinutes(5));
        (await RemindersSentAsync(patient.Id)).ShouldBe(2, "one 24-hour and one 2-hour reminder");
        var second = await frontOffice.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{booked.Id}");
        (second.Reminder24hSentAt, second.Reminder2hSentAt.ShouldNotBeNull()).ShouldBe((first.Reminder24hSentAt, second.Reminder2hSentAt!.Value));
    }

    /// <summary>
    /// A video consultation of the patient's that starts later today (India), as check-in needs: today's first free video
    /// slot, or, when the rest of today is booked (the full suite books the doctor's slots), the first free slot moved to
    /// later today, as tests set up state.
    /// </summary>
    private async Task<AppointmentDetail> BookTodayAsync(ApiClient frontOffice, Guid patientId, Guid practitionerId)
    {
        var india = HospitalCalendar.India;
        var now = DateTimeOffset.UtcNow;
        var today = india.DateOf(now);
        var availability = await frontOffice.GetAsync<List<PractitionerAvailability>>(
            $"/api/v1/scheduling/slots?practitionerId={practitionerId}&from={today:O}&to={today:O}&mode=Telehealth&includeFull=false");
        var todays = availability.SelectMany(a => a.Slots)
            .Where(s => s.Available > 0 && s.StartsAt > now.AddMinutes(2) && india.DateOf(s.StartsAt) == today)
            .MinBy(s => s.StartsAt);
        var slot = todays ?? await TeleconsultationJourneyTests.FreeVideoSlotAsync(frontOffice, practitionerId);
        var booked = await frontOffice.PostAsync<AppointmentDetail>("/api/v1/scheduling/appointments", new BookAppointmentRequest(
            patientId, practitionerId, slot.StartsAt, AppointmentChannel.Phone, AppointmentVisitType.New, IsTelehealth: true, "Follow-up of a rash", Notes: null));
        if (todays is not null)
        {
            return booked;
        }

        // Later today, a few minutes from now (a random second keeps it off any other booking's start), never past midnight.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var appointment = await db.Set<Appointment>().SingleAsync(a => a.Id == booked.Id, Ct);
            var length = appointment.EndsAt - appointment.StartsAt;
            var startsAt = now.AddMinutes(3).AddSeconds(Random.Shared.Next(600));
            while (india.DateOf(startsAt) != today)
            {
                startsAt = startsAt.AddMinutes(-5);
            }

            appointment.StartsAt = startsAt;
            appointment.EndsAt = startsAt + length;
            await db.SaveChangesAsync(Ct);
        }

        return await frontOffice.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{booked.Id}");
    }

    /// <summary>The doctor's first free video slot after <paramref name="after"/>, in the coming week.</summary>
    private static async Task<SlotDto> VideoSlotAfterAsync(ApiClient frontOffice, Guid practitionerId, DateTimeOffset after)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(330)).DateTime);
        var availability = await frontOffice.GetAsync<List<PractitionerAvailability>>(
            $"/api/v1/scheduling/slots?practitionerId={practitionerId}&from={today:O}&to={today.AddDays(6):O}&mode=Telehealth&includeFull=false");
        return availability.SelectMany(a => a.Slots).Where(s => s.Available > 0 && s.StartsAt > after).MinBy(s => s.StartsAt)
               ?? throw new InvalidOperationException("The doctor has no free video slot more than a day ahead in the coming week.");
    }

    /// <summary>
    /// Reminder passes at <paramref name="now"/>, two at a time as two job nodes would run them, until the appointment's
    /// <paramref name="kind"/> reminder is recorded. A pass takes the 100 bookings due soonest, and the test database keeps
    /// earlier runs' bookings, so it may take a few passes to reach this one.
    /// </summary>
    private async Task RemindAsync(DateTimeOffset now, Guid appointmentId, AppointmentReminder kind)
    {
        for (var pass = 0; pass < 100; pass++)
        {
            await Task.WhenAll(RemindAsync(now), RemindAsync(now));
            await using var scope = server.Services.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AppointmentReminderSent>().AsNoTracking()
                    .AnyAsync(r => r.AppointmentId == appointmentId && r.Kind == kind, Ct))
            {
                return;
            }
        }

        throw new InvalidOperationException($"The appointment's {kind} reminder was not recorded after 100 passes.");
    }

    /// <summary>One reminder pass, as the job runs it, at <paramref name="now"/>.</summary>
    private async Task RemindAsync(DateTimeOffset now)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var runner = new AppointmentReminderRunner(
            services.GetRequiredService<HospitalDbContext>(),
            services.GetRequiredService<AppointmentNotifier>(),
            services.GetRequiredService<WaitlistService>(),
            new FixedClock(now),
            services.GetRequiredService<ILogger<AppointmentReminderRunner>>());
        await runner.RunAsync(Ct);
    }

    /// <summary>How many reminders were queued to the patient (each goes on SMS and WhatsApp: counted once).</summary>
    private async Task<int> RemindersSentAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var logs = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<NotificationLog>().AsNoTracking()
            .Where(l => l.PatientId == patientId && l.TemplateKey == AppointmentNotifier.ReminderTemplate)
            .Select(l => l.Channel)
            .ToListAsync(Ct);
        return logs.Count(c => c == NotificationChannel.Sms);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
