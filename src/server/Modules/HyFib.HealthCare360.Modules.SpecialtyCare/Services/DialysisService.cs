using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Dialysis;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

public static class DialysisServiceCodes
{
    public const string Session = "DIA-SESSION";

    /// <summary>Charged once, when a new dialyzer is first used.</summary>
    public const string Dialyzer = "DIA-DIALYZER";
}

/// <summary>
/// Dialysis enrolment, scheduling and sessions (DIA-01..05). Sero-segregation, machine disinfection and dialyzer
/// ownership are checked by the domain on the exact machine and dialyzer being connected; double booking of a
/// machine or patient per shift is additionally prevented by filtered unique indexes.
/// </summary>
public sealed class DialysisService(
    HospitalDbContext db,
    INumberSeries numbers,
    SpecialtyChargeService charges,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string SessionSeries = "dialysis-session";
    public const string SessionFormat = "{FAC}DL{YY}{SEQ:6}";

    private static readonly DialysisSessionStatus[] ActiveStatuses =
        [DialysisSessionStatus.Scheduled, DialysisSessionStatus.InProgress, DialysisSessionStatus.Completed];

    public async Task<Result<Guid>> SaveProfileAsync(Guid patientId, SaveDialysisProfileRequest request, CancellationToken ct)
    {
        var patient = await SpecialtyLookups.EnsurePatientAsync(db, patientId, ct);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        var nephrologist = await SpecialtyLookups.EnsurePractitionersAsync(db, [("nephrologistId", request.NephrologistId)], ct);
        if (nephrologist.IsFailure)
        {
            return nephrologist.Error!;
        }

        var profile = await db.Set<DialysisPatientProfile>().FirstOrDefaultAsync(p => p.PatientId == patientId, ct);
        if (profile is null)
        {
            if (currentUser.FacilityId is not { } facilityId)
            {
                return Error.Rule("facility.required", "Select a facility first.");
            }

            profile = new DialysisPatientProfile { FacilityId = facilityId, PatientId = patientId, EnrolledOn = time.Today() };
            db.Add(profile);
        }
        else if (request.Version is { } version)
        {
            db.SetExpectedVersion(profile, version);
        }

        profile.DryWeightKg = request.DryWeightKg;
        profile.VascularAccess = request.VascularAccess;
        profile.AccessSite = Clean(request.AccessSite);
        profile.AccessCreatedOn = request.AccessCreatedOn;
        profile.ScheduleDays = request.ScheduleDays.Distinct().Order().ToList();
        profile.Shift = request.Shift;
        profile.SessionMinutes = request.SessionMinutes;
        profile.SeroStatus = request.SeroStatus;
        profile.SeroTestedOn = request.SeroTestedOn;
        profile.ReusePolicy = request.ReusePolicy;
        profile.MaxDialyzerUses = request.MaxDialyzerUses;
        profile.NephrologistId = request.NephrologistId;
        profile.PrimaryDiagnosis = Clean(request.PrimaryDiagnosis);
        profile.AnticoagulationNotes = Clean(request.AnticoagulationNotes);
        profile.IsActive = request.IsActive;

        await db.SaveChangesAsync(ct);
        await PublishAsync(profile.FacilityId, null, ct);
        return profile.Id;
    }

    public async Task<Result<Dialyzer>> RegisterDialyzerAsync(Guid patientId, RegisterDialyzerRequest request, CancellationToken ct)
    {
        var profile = await ActiveProfileAsync(patientId, ct);
        if (profile.IsFailure)
        {
            return profile.Error!;
        }

        var dialyzer = NewDialyzer(profile.Value, request);
        db.Add(dialyzer);
        await db.SaveChangesAsync(ct);
        return dialyzer;
    }

    public async Task<Result> DiscardDialyzerAsync(Guid dialyzerId, string reason, CancellationToken ct)
    {
        var dialyzer = await db.Set<Dialyzer>().FirstOrDefaultAsync(d => d.Id == dialyzerId, ct);
        if (dialyzer is null)
        {
            return Error.NotFound(nameof(Dialyzer), dialyzerId);
        }

        dialyzer.Discard(reason, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> RecordDisinfectionAsync(Guid machineId, string method, CancellationToken ct)
    {
        var machine = await db.Set<DialysisMachine>().FirstOrDefaultAsync(m => m.Id == machineId, ct);
        if (machine is null)
        {
            return Error.NotFound(nameof(DialysisMachine), machineId);
        }

        var result = machine.RecordDisinfection(method, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(machine.FacilityId, null, ct);
        return Result.Success();
    }

    public async Task<Result<Guid>> ScheduleAsync(ScheduleDialysisRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var patient = await SpecialtyLookups.EnsurePatientAsync(db, request.PatientId, ct);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        var profile = await ActiveProfileAsync(request.PatientId, ct);
        if (profile.IsFailure)
        {
            return profile.Error!;
        }

        if (request.ScheduledDate < time.Today())
        {
            return Error.Field("scheduledDate", "Sessions cannot be scheduled in the past.");
        }

        if (await db.Set<DialysisSession>().AnyAsync(s => s.PatientId == request.PatientId && s.ScheduledDate == request.ScheduledDate
                                                          && s.Shift == request.Shift && ActiveStatuses.Contains(s.Status), ct))
        {
            return Error.Conflict("dialysis.patient_already_booked", "This patient already has a session in that shift.");
        }

        var (admissionId, visitId) = await SpecialtyLookups.EncounterAsync(db, request.PatientId, request.AdmissionId, request.VisitId, ct);
        var session = new DialysisSession
        {
            FacilityId = facilityId,
            PatientId = request.PatientId,
            ProfileId = profile.Value.Id,
            AdmissionId = admissionId,
            VisitId = visitId,
            ScheduledDate = request.ScheduledDate,
            Shift = request.Shift,
            Status = DialysisSessionStatus.Scheduled,
        };

        if (request.MachineId is { } machineId)
        {
            var assigned = await AssignCoreAsync(session, machineId, profile.Value.SeroStatus, ct);
            if (assigned.IsFailure)
            {
                return assigned.Error!;
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        session.SessionNumber = await numbers.NextAsync(SessionSeries, facilityId, SessionFormat, NumberResetPolicy.Yearly, ct);
        db.Add(session);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await PublishAsync(facilityId, session.ScheduledDate, ct);
        return session.Id;
    }

    public async Task<Result> AssignMachineAsync(Guid sessionId, Guid machineId, CancellationToken ct)
    {
        var session = await db.Set<DialysisSession>().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(DialysisSession), sessionId);
        }

        var profile = await ActiveProfileAsync(session.PatientId, ct);
        if (profile.IsFailure)
        {
            return profile;
        }

        var assigned = await AssignCoreAsync(session, machineId, profile.Value.SeroStatus, ct);
        return assigned.IsFailure ? assigned : await SaveAsync(session, ct);
    }

    public async Task<Result> RecordPreAssessmentAsync(Guid sessionId, PreDialysisRequest request, CancellationToken ct)
    {
        var session = await db.Set<DialysisSession>().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(DialysisSession), sessionId);
        }

        var result = session.RecordPreAssessment(
            new PreDialysisAssessment(request.WeightKg, request.Systolic, request.Diastolic, request.Pulse, request.TemperatureC, request.TargetUfMl),
            time.GetUtcNow());
        return result.IsFailure ? result : await SaveAsync(session, ct);
    }

    /// <summary>Connects the patient: sero-matched and disinfected machine, the patient's own usable dialyzer (or a new one).</summary>
    public async Task<Result> StartAsync(Guid sessionId, StartDialysisRequest request, CancellationToken ct)
    {
        var session = await db.Set<DialysisSession>().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(DialysisSession), sessionId);
        }

        var profile = await ActiveProfileAsync(session.PatientId, ct);
        if (profile.IsFailure)
        {
            return profile;
        }

        var machine = await db.Set<DialysisMachine>().FirstOrDefaultAsync(m => m.Id == request.MachineId, ct);
        if (machine is null)
        {
            return Error.Field("machineId", "Choose a dialysis machine.");
        }

        if (await MachineBookedAsync(machine.Id, session, ct))
        {
            return Error.Conflict("dialysis.machine_booked", $"Machine {machine.Code} is booked for another patient in this shift.");
        }

        Dialyzer dialyzer;
        if (request.DialyzerId is { } dialyzerId)
        {
            var existing = await db.Set<Dialyzer>().FirstOrDefaultAsync(d => d.Id == dialyzerId, ct);
            if (existing is null)
            {
                return Error.Field("dialyzerId", "Choose one of the patient's dialyzers.");
            }

            dialyzer = existing;
        }
        else
        {
            dialyzer = NewDialyzer(profile.Value, request.NewDialyzer!);
            db.Add(dialyzer);
        }

        var result = session.Start(machine, dialyzer, profile.Value.SeroStatus, request.HeparinLoadingUnits, request.HeparinHourlyUnits,
            currentUser.Stamp(time.GetUtcNow()));
        return result.IsFailure ? result : await SaveAsync(session, ct);
    }

    public async Task<Result> AddReadingsAsync(Guid sessionId, AddDialysisReadingsRequest request, CancellationToken ct)
    {
        var session = await db.Set<DialysisSession>().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(DialysisSession), sessionId);
        }

        var result = session.AddReadings(request.Readings);
        return result.IsFailure ? result : await SaveAsync(session, ct);
    }

    /// <summary>Ends the session, frees the machine (now due for disinfection) and posts the session charge.</summary>
    public async Task<Result> CompleteAsync(Guid sessionId, CompleteDialysisRequest request, CancellationToken ct)
    {
        var session = await db.Set<DialysisSession>().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(DialysisSession), sessionId);
        }

        var machine = session.MachineId is { } machineId ? await db.Set<DialysisMachine>().FirstOrDefaultAsync(m => m.Id == machineId, ct) : null;
        if (machine is null)
        {
            return Error.Rule("dialysis.session_not_running", "Only a running session can be completed.");
        }

        var result = session.Complete(
            new PostDialysisAssessment(request.WeightKg, request.Systolic, request.Diastolic, request.Pulse, request.UfAchievedMl),
            request.Complications ?? [], request.ComplicationNotes, machine, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        var outcome = await PostChargesAsync(session, ct);
        session.RecordCharges(outcome.Status, outcome.Error);
        return await SaveAsync(session, ct);
    }

    public async Task<Result> CancelAsync(Guid sessionId, CancelDialysisRequest request, CancellationToken ct)
    {
        var session = await db.Set<DialysisSession>().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(DialysisSession), sessionId);
        }

        var result = session.Cancel(request.Reason, request.PatientDidNotCome);
        return result.IsFailure ? result : await SaveAsync(session, ct);
    }

    public async Task<Result<ChargeOutcome>> RetryChargesAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Set<DialysisSession>().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(DialysisSession), sessionId);
        }

        if (session.Status != DialysisSessionStatus.Completed)
        {
            return Error.Rule("dialysis.session_not_completed", "Charges are posted when the session is completed.");
        }

        var outcome = await PostChargesAsync(session, ct);
        session.RecordCharges(outcome.Status, outcome.Error);
        await db.SaveChangesAsync(ct);
        return outcome;
    }

    private async Task<Result> AssignCoreAsync(DialysisSession session, Guid machineId, SeroStatus seroStatus, CancellationToken ct)
    {
        var machine = await db.Set<DialysisMachine>().AsNoTracking().FirstOrDefaultAsync(m => m.Id == machineId && m.IsActive, ct);
        if (machine is null)
        {
            return Error.Field("machineId", "Choose an active dialysis machine.");
        }

        if (await MachineBookedAsync(machineId, session, ct))
        {
            return Error.Conflict("dialysis.machine_booked", $"Machine {machine.Code} is already booked in this shift.");
        }

        return session.AssignMachine(machine, seroStatus);
    }

    private Task<bool> MachineBookedAsync(Guid machineId, DialysisSession session, CancellationToken ct) =>
        db.Set<DialysisSession>().AnyAsync(s => s.Id != session.Id && s.MachineId == machineId && s.ScheduledDate == session.ScheduledDate
                                                && s.Shift == session.Shift && ActiveStatuses.Contains(s.Status), ct);

    private async Task<Result<DialysisPatientProfile>> ActiveProfileAsync(Guid patientId, CancellationToken ct)
    {
        var profile = await db.Set<DialysisPatientProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.PatientId == patientId && p.IsActive, ct);
        return profile is null
            ? Error.Rule("dialysis.not_enrolled", "Enrol the patient in the dialysis unit (with sero status) before scheduling or starting dialysis.")
            : profile;
    }

    private Dialyzer NewDialyzer(DialysisPatientProfile profile, RegisterDialyzerRequest request) => new()
    {
        FacilityId = profile.FacilityId,
        PatientId = profile.PatientId,
        SerialNumber = request.SerialNumber.Trim().ToUpperInvariant(),
        Brand = Clean(request.Brand),
        Model = Clean(request.Model),
        SurfaceAreaM2 = request.SurfaceAreaM2,
        MaxUses = profile.EffectiveMaxDialyzerUses,
    };

    private async Task<ChargeOutcome> PostChargesAsync(DialysisSession session, CancellationToken ct)
    {
        var lines = new List<ChargeLine>
        {
            new(ChargeSources.Dialysis, $"{session.Id}:session", session.PatientId, session.VisitId, session.AdmissionId, DialysisServiceCodes.Session,
                Description: $"Haemodialysis {session.SessionNumber}", ServiceDate: session.StartedAt),
        };

        if (session.DialyzerUseNumber == 1 && session.DialyzerId is { } dialyzerId)
        {
            lines.Add(new ChargeLine(ChargeSources.Dialysis, $"dialyzer:{dialyzerId}", session.PatientId, session.VisitId, session.AdmissionId,
                DialysisServiceCodes.Dialyzer, Description: "New dialyzer", ServiceDate: session.StartedAt));
        }

        return await charges.PostAsync(lines, ct);
    }

    private async Task<Result> SaveAsync(DialysisSession session, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await PublishAsync(session.FacilityId, session.ScheduledDate, ct);
        return Result.Success();
    }

    private Task PublishAsync(Guid facilityId, DateOnly? date, CancellationToken ct) =>
        realtime.FacilityChangedAsync(facilityId, SpecialtyRealtime.DialysisChanged, new { date }, ct);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
