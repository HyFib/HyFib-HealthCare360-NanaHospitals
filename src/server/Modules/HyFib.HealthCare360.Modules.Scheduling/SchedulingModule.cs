using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;
using HyFib.HealthCare360.Modules.Scheduling.Features.Availability;
using HyFib.HealthCare360.Modules.Scheduling.Features.OnlineBooking;
using HyFib.HealthCare360.Modules.Scheduling.Features.Portal;
using HyFib.HealthCare360.Modules.Scheduling.Features.Schedules;
using HyFib.HealthCare360.Modules.Scheduling.Features.Waitlist;
using HyFib.HealthCare360.Modules.Scheduling.Services;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Scheduling;

public static class SchedulingPermissions
{
    public const string SchedulesManage = "scheduling.schedules.manage";
    public const string AppointmentsRead = "scheduling.appointments.read";
    public const string AppointmentsBook = "scheduling.appointments.book";

    /// <summary>Confirm, reschedule, cancel, check in, mark no-shows and overbook a full slot.</summary>
    public const string AppointmentsManage = "scheduling.appointments.manage";

    private const string Group = "Scheduling";

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(SchedulesManage, Group, "Set up doctor OPD sessions, leave, blocks and extra sessions",
            [SystemRoles.HospitalAdmin, SystemRoles.FrontOffice]),
        new(AppointmentsRead, Group, "View appointment calendars, slots and waitlists",
            [SystemRoles.FrontOffice, SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.HospitalAdmin, SystemRoles.CrmExecutive]),
        new(AppointmentsBook, Group, "Book appointments and add patients to a doctor's waitlist",
            [SystemRoles.FrontOffice, SystemRoles.HospitalAdmin, SystemRoles.CrmExecutive, SystemRoles.Doctor, SystemRoles.Nurse]),
        new(AppointmentsManage, Group, "Confirm, reschedule, cancel, check in and mark no-shows; overbook full slots",
            [SystemRoles.FrontOffice, SystemRoles.HospitalAdmin, SystemRoles.CrmExecutive, SystemRoles.Nurse]),
    ];
}

/// <summary>
/// Practice management schedules, appointments and Online Doctor Appointment booking (PMS-01/02/03, OA-01..05,
/// TEL-01 booking side). Raises <c>AppointmentBooked</c>/<c>AppointmentCancelled</c> for other modules and follows
/// visits created by the Patients module to check appointments in.
/// </summary>
public sealed class SchedulingModule : IModule
{
    public string Name => "scheduling";

    public IEnumerable<PermissionDefinition> Permissions => SchedulingPermissions.All;

    public IEnumerable<NumberSeriesDefinition> NumberSeries => SchedulingNumberSeries.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<SchedulingSettings>();
        services.AddScoped<AvailabilityService>();
        services.AddScoped<ScheduleService>();
        services.AddScoped<AppointmentNotifier>();
        services.AddScoped<WaitlistService>();
        services.AddScoped<AppointmentBookingService>();
        services.AddScoped<AppointmentPaymentService>();
        services.AddScoped<AppointmentReminderRunner>();
        services.AddHostedService<AppointmentReminderService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ScheduleEndpoints.Map(endpoints);
        AvailabilityEndpoints.Map(endpoints);
        AppointmentEndpoints.Map(endpoints);
        WaitlistEndpoints.Map(endpoints);
        PublicDoctorEndpoints.Map(endpoints);
        PortalAppointmentEndpoints.Map(endpoints);
    }
}
