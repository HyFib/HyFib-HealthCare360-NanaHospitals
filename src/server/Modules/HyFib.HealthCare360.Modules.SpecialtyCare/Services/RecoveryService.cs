using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// The recovery room (PACU) after the WHO Sign-out: the signed-in nurse receives the patient, the anaesthetist hands over
/// (from their own sign-in, or at the nurse's screen with their own credentials), the chart is kept at intervals, and the
/// patient leaves for the ward at a modified Aldrete score of 9 or more, or with an anaesthetist's override recorded from
/// their own sign-in and audited at Warning; for ICU or HDU at any score. Until then the theatre holds the stay
/// (<see cref="TheatreDischargeHolds"/>) and the ward sees the patient in recovery.
/// </summary>
public sealed class RecoveryService(
    HospitalDbContext db,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    IPermissionService permissions,
    IWitnessConfirmations witnesses,
    IAuditLogger audit,
    TimeProvider time)
{
    /// <summary>The anaesthetist hands the patient over at the recovery nurse's screen, with their own credentials.</summary>
    public static readonly WitnessPurpose HandoverWitness = new(
        "ot.recovery_handover", SpecialtyCarePermissions.OtAnaesthesia, "the anaesthetist's handover of a patient in the recovery room");

    public async Task<Result<RecoveryRecordDto>> ReceiveAsync(Guid bookingId, ReceiveInRecoveryRequest request, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), bookingId);
        }

        if (booking.SignOutCompletedAt is not { } signOut)
        {
            return Error.Rule("recovery.not_signed_out", "The patient is received in recovery after the WHO Sign-out.");
        }

        if (!booking.InRecovery)
        {
            return Error.Rule("recovery.not_in_recovery", "This patient has already left recovery.");
        }

        if (await db.Set<RecoveryRecord>().AnyAsync(r => r.SurgeryBookingId == bookingId, ct))
        {
            return Error.Rule("recovery.already_received", "The patient is already received in recovery.");
        }

        var now = time.GetUtcNow();
        var at = request.ReceivedAt ?? now;
        if (at < signOut)
        {
            return Error.Field("receivedAt", "The patient arrives in recovery after the WHO Sign-out.");
        }

        var by = currentUser.Stamp(now);
        var record = new RecoveryRecord
        {
            FacilityId = booking.FacilityId,
            SurgeryBookingId = booking.Id,
            PatientId = booking.PatientId,
            ReceivedAt = at,
            ReceivedByUserId = by.UserId,
            ReceivedByName = by.Name,
            Airway = request.Airway,
            Oxygen = request.Oxygen,
            OxygenLitresPerMinute = request.Oxygen == OxygenDelivery.RoomAir ? null : request.OxygenLitresPerMinute,
            PreAnaesthesiaSystolic = request.PreAnaesthesiaSystolic ?? await FirstSystolicAsync(bookingId, ct),
            Notes = Clean(request.Notes),
        };
        db.Add(record);
        await db.SaveChangesAsync(ct);
        await realtime.OtChangedAsync(booking.FacilityId, booking.Id, ct);
        return ToDto(record, booking, now);
    }

    public async Task<Result<RecoveryRecordDto>> SaveAsync(Guid bookingId, SaveRecoveryRecordRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(bookingId, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (booking, record) = loaded.Value;
        var open = record.EnsureOpen();
        if (open.IsFailure)
        {
            return open.Error!;
        }

        db.SetExpectedVersion(record, request.Version);
        record.Airway = request.Airway;
        record.Oxygen = request.Oxygen;
        record.OxygenLitresPerMinute = request.Oxygen == OxygenDelivery.RoomAir ? null : request.OxygenLitresPerMinute;
        record.PreAnaesthesiaSystolic = request.PreAnaesthesiaSystolic;
        record.Notes = Clean(request.Notes);
        return await SavedAsync(booking, record, ct);
    }

    /// <summary>
    /// The anaesthetist's handover: from their own sign-in (the signed-in user holds the anaesthesia permission), or
    /// confirmed at the nurse's screen with their own credentials (<see cref="HandoverWitness"/>).
    /// </summary>
    public async Task<Result<RecoveryRecordDto>> HandoverAsync(Guid bookingId, RecoveryHandoverRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(bookingId, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (booking, record) = loaded.Value;
        var now = time.GetUtcNow();
        StaffStamp from;
        if (request.WitnessConfirmationId is { } confirmationId)
        {
            var witness = await witnesses.UseAsync(confirmationId, HandoverWitness.Key, booking.PatientId, nameof(RecoveryRecord), record.Id, ct);
            if (witness.IsFailure)
            {
                return witness.Error!;
            }

            from = new StaffStamp(witness.Value.UserId, witness.Value.Name, now);
        }
        else
        {
            if (currentUser.UserId is not { } user || !await permissions.HasPermissionAsync(user, SpecialtyCarePermissions.OtAnaesthesia, ct))
            {
                return Error.Forbidden("recovery.handover_anaesthetist",
                    "The anaesthetist hands over from their own sign-in, or confirms the handover at this screen with their own user name and password.");
            }

            from = currentUser.Stamp(now);
        }

        var result = record.RecordHandover(from, request.WitnessConfirmationId is not null, Clean(request.Notes));
        return result.IsFailure ? result.Error! : await SavedAsync(booking, record, ct);
    }

    public async Task<Result<RecoveryRecordDto>> AddObservationAsync(Guid bookingId, AddRecoveryObservationRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(bookingId, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (booking, record) = loaded.Value;
        var now = time.GetUtcNow();
        var by = currentUser.Stamp(now);
        var result = record.AddObservation(new RecoveryObservation(
            Guid.CreateVersion7(), request.At ?? now, request.HeartRate, request.Systolic, request.Diastolic, request.SpO2, request.OnOxygen,
            request.RespiratoryRate, request.TemperatureC, request.Consciousness, request.Breathing, request.LimbsMoving, request.PainScore, request.Ponv,
            by.UserId, by.Name));
        return result.IsFailure ? result.Error! : await SavedAsync(booking, record, ct);
    }

    /// <summary>The nurse discharges the patient: to the ward on the score, to ICU or HDU at any score.</summary>
    public async Task<Result<RecoveryRecordDto>> DischargeAsync(Guid bookingId, DischargeFromRecoveryRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(bookingId, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (booking, record) = loaded.Value;
        var by = currentUser.Stamp(time.GetUtcNow());
        var result = record.Discharge(request.Destination, booking.Status == SurgeryStatus.Completed, booking.RequiresAnaesthetist, by);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        booking.DischargeFromRecovery(by.At, request.Destination);
        audit.Add(new AuditEntry(AuditCategories.Clinical, "ot.recovery_discharged",
            $"Surgery {booking.BookingNumber}: discharged from recovery to {Describe(request.Destination)} by {by.Name}"
            + (record.DischargeScore is { } score ? $" at a modified Aldrete score of {score}" : string.Empty))
        {
            PatientId = booking.PatientId,
            EntityType = nameof(SurgeryBooking),
            EntityId = booking.Id.ToString(),
            Details = new { destination = request.Destination, score = record.DischargeScore },
        });
        return await SavedAsync(booking, record, ct);
    }

    /// <summary>
    /// The anaesthetist sends the patient to the ward below the score, from their own sign-in (the endpoint needs the
    /// anaesthesia permission), with the reason: audited at Warning.
    /// </summary>
    public async Task<Result<RecoveryRecordDto>> OverrideAsync(Guid bookingId, OverrideRecoveryDischargeRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(bookingId, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (booking, record) = loaded.Value;
        var now = time.GetUtcNow();
        var by = currentUser.Stamp(now);
        var score = record.Score();
        var blockedBy = RecoveryRecord.WardCheck(score, now);
        var result = record.Discharge(RecoveryDestination.Ward, booking.Status == SurgeryStatus.Completed, booking.RequiresAnaesthetist, by, by, request.Reason);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        booking.DischargeFromRecovery(by.At, RecoveryDestination.Ward);
        audit.Add(new AuditEntry(AuditCategories.Clinical, "ot.recovery_override",
            Fit($"Surgery {booking.BookingNumber}: sent to the ward from recovery below the score by the anaesthetist {by.Name} ({blockedBy}): {record.OverrideReason}", 1000))
        {
            PatientId = booking.PatientId,
            EntityType = nameof(SurgeryBooking),
            EntityId = booking.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { score = score.Total, missing = score.Missing, observedAt = score.At, reason = record.OverrideReason },
        });
        return await SavedAsync(booking, record, ct);
    }

    internal static RecoveryRecordDto ToDto(RecoveryRecord r, SurgeryBooking booking, DateTimeOffset now)
    {
        var score = r.Score();
        return new RecoveryRecordDto(
            r.Id, r.SurgeryBookingId, r.PatientId, r.ReceivedAt, r.ReceivedByName, r.Airway, r.Oxygen, r.OxygenLitresPerMinute, r.PreAnaesthesiaSystolic,
            r.Notes, booking.RequiresAnaesthetist, r.HandoverAt, r.HandedOverByName, r.HandoverConfirmedAtScreen, r.HandoverNotes, r.Observations,
            new RecoveryScoreDto(score.Components, score.Total, score.At, score.Missing, score.MeetsWardDischarge, RecoveryRecord.WardCheck(score, now)),
            r.DischargedAt, r.Destination, r.DischargeScore, r.DischargedByName, r.OverrideReason, r.OverrideByName, r.Version);
    }

    public static string Describe(RecoveryDestination destination) => destination switch
    {
        RecoveryDestination.Icu => "the ICU",
        RecoveryDestination.Hdu => "the HDU",
        _ => "the ward",
    };

    private async Task<Result<(SurgeryBooking Booking, RecoveryRecord Record)>> LoadAsync(Guid bookingId, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), bookingId);
        }

        var record = await db.Set<RecoveryRecord>().FirstOrDefaultAsync(r => r.SurgeryBookingId == bookingId, ct);
        if (record is null)
        {
            return Error.Rule("recovery.not_received", "Receive the patient in recovery first.");
        }

        return (booking, record);
    }

    private async Task<Result<RecoveryRecordDto>> SavedAsync(SurgeryBooking booking, RecoveryRecord record, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await realtime.OtChangedAsync(booking.FacilityId, booking.Id, ct);
        return ToDto(record, booking, time.GetUtcNow());
    }

    /// <summary>The first systolic of the anaesthesia chart, the nearest the record has to the pressure before anaesthesia.</summary>
    private async Task<int?> FirstSystolicAsync(Guid bookingId, CancellationToken ct)
    {
        var vitals = await db.Set<AnaesthesiaRecord>().AsNoTracking().Where(r => r.SurgeryBookingId == bookingId).Select(r => r.Vitals).FirstOrDefaultAsync(ct);
        return vitals?.Where(v => v.Systolic is not null).OrderBy(v => v.At).Select(v => v.Systolic).FirstOrDefault();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
