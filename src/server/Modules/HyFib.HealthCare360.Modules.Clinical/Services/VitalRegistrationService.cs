using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.VitalRegistration;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Births and deaths in the hospital (<see cref="BirthRecord"/>, <see cref="DeathRecord"/>): recorded, certified,
/// reported to the Registrar within 21 days, and the month's registers. A certified death marks the patient deceased.
/// </summary>
internal sealed class VitalRegistrationService(
    HospitalDbContext db,
    INumberSeries numbers,
    PatientDeaths deaths,
    PatientAccessGuard guard,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public Task<BirthRecord?> FindBirthAsync(Guid id, CancellationToken ct) =>
        db.Set<BirthRecord>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);

    public Task<DeathRecord?> FindDeathAsync(Guid id, CancellationToken ct) =>
        db.Set<DeathRecord>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<Result<BirthRecordDto>> SaveBirthAsync(Guid? id, SaveBirthRequest request, CancellationToken ct)
    {
        var mother = await PatientAsync(request.MotherPatientId, ct);
        if (mother is null)
        {
            return Error.NotFound(nameof(Patient), request.MotherPatientId);
        }

        if (mother.Gender == Gender.Male)
        {
            return Error.Field("motherPatientId", "Choose the mother's record.");
        }

        if (request.BabyPatientId is { } baby && await PatientAsync(baby, ct) is null)
        {
            return Error.NotFound(nameof(Patient), baby);
        }

        BirthRecord? birth;
        if (id is { } existing)
        {
            birth = await db.Set<BirthRecord>().FirstOrDefaultAsync(b => b.Id == existing, ct);
            if (birth is null)
            {
                return Error.NotFound("Birth", existing);
            }

            if (birth.MotherPatientId != request.MotherPatientId)
            {
                return Error.Field("motherPatientId", "A birth stays with the mother it was recorded for; mark it entered in error instead.");
            }

            db.SetExpectedVersion(birth, request.Version);
        }
        else
        {
            if (currentUser.FacilityId is not { } facility)
            {
                return Error.Rule("facility.required", "Choose the facility first.");
            }

            birth = new BirthRecord
            {
                FacilityId = facility,
                MotherPatientId = request.MotherPatientId,
                SerialNumber = await numbers.NextAsync(ClinicalNumberSeries.BirthRegister, facility, ct),
            };
            db.Add(birth);
        }

        birth.BabyPatientId = request.BabyPatientId;
        birth.AdmissionId = request.AdmissionId;
        var recorded = birth.Record(
            new BirthDetails(
                request.BornAt, request.Outcome, request.Sex, request.WeightGrams, request.WeeksOfGestation, request.Method, request.Attendant,
                request.ChildName, request.FatherName, request.MotherAgeYears, request.LiveChildren, request.InformantName, request.StillBirthCause),
            time.GetUtcNow(),
            currentUser.UserId);
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await BirthDtoAsync(birth, ct);
    }

    public async Task<Result<DeathRecordDto>> SaveDeathAsync(Guid? id, SaveDeathRequest request, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PatientId, ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), request.PatientId);
        }

        DeathRecord? death;
        if (id is { } existing)
        {
            death = await db.Set<DeathRecord>().FirstOrDefaultAsync(d => d.Id == existing, ct);
            if (death is null)
            {
                return Error.NotFound("Death", existing);
            }

            if (death.PatientId != request.PatientId)
            {
                return Error.Field("patientId", "A certificate stays with the patient it was made for; mark it entered in error instead.");
            }

            db.SetExpectedVersion(death, request.Version);
        }
        else
        {
            if (await db.Set<DeathRecord>().AnyAsync(d => d.PatientId == request.PatientId && d.Status != VitalReportStatus.EnteredInError, ct))
            {
                return Error.Conflict("death.exists", "This patient's death is already certified. Correct that certificate instead.");
            }

            if (currentUser.FacilityId is not { } facility)
            {
                return Error.Rule("facility.required", "Choose the facility first.");
            }

            death = new DeathRecord
            {
                FacilityId = facility,
                PatientId = request.PatientId,
                SerialNumber = await numbers.NextAsync(ClinicalNumberSeries.DeathRegister, facility, ct),
            };
            db.Add(death);
        }

        var previous = death.DiedAt;
        death.AdmissionId = request.AdmissionId;
        var age = AgeAt(patient, request.DiedAt) ?? request.AgeYears;
        var certified = death.Certify(
            new DeathCertificate(
                request.DiedAt, request.Causes, request.OtherConditions, request.Manner, request.MedicoLegal, request.Pregnancy,
                currentUser.DisplayName ?? currentUser.UserName ?? "Doctor", request.CertifierRegistrationNumber),
            patient.Gender == Gender.Female && age is >= 15 and <= 49,
            patient.DateOfBirth is { } dob ? new DateTimeOffset(dob.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)) : null,
            time.GetUtcNow(),
            currentUser.UserId);
        if (certified.IsFailure)
        {
            return certified.Error!;
        }

        // A corrected time of death moves the deceased mark with it.
        if (id is not null && previous != death.DiedAt)
        {
            await deaths.ClearDeceasedAsync(death.PatientId, previous, $"the death certificate {death.SerialNumber} was corrected", ct);
        }

        await deaths.MarkDeceasedAsync(death.PatientId, death.DiedAt, $"the death certificate {death.SerialNumber}", ct);
        await db.SaveChangesAsync(ct);
        return await DeathDtoAsync(death, ct);
    }

    public async Task<Result<BirthRecordDto>> ReportBirthAsync(Guid id, ReportToRegistrarRequest request, CancellationToken ct)
    {
        var birth = await db.Set<BirthRecord>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (birth is null)
        {
            return Error.NotFound("Birth", id);
        }

        db.SetExpectedVersion(birth, request.Version);
        var reported = birth.Report(new RegistrarReport(request.ReportedOn, request.RegistrationNumber), time.Today());
        if (reported.IsFailure)
        {
            return reported.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await BirthDtoAsync(birth, ct);
    }

    public async Task<Result<DeathRecordDto>> ReportDeathAsync(Guid id, ReportToRegistrarRequest request, CancellationToken ct)
    {
        var death = await db.Set<DeathRecord>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (death is null)
        {
            return Error.NotFound("Death", id);
        }

        db.SetExpectedVersion(death, request.Version);
        var reported = death.Report(new RegistrarReport(request.ReportedOn, request.RegistrationNumber), time.Today());
        if (reported.IsFailure)
        {
            return reported.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DeathDtoAsync(death, ct);
    }

    public async Task<Result<BirthRecordDto>> BirthInErrorAsync(Guid id, VitalEntryErrorRequest request, CancellationToken ct)
    {
        var birth = await db.Set<BirthRecord>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (birth is null)
        {
            return Error.NotFound("Birth", id);
        }

        db.SetExpectedVersion(birth, request.Version);
        var marked = birth.MarkEnteredInError(request.Reason, currentUser.UserId);
        if (marked.IsFailure)
        {
            return marked.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await BirthDtoAsync(birth, ct);
    }

    /// <summary>A certificate made on the wrong patient: it leaves the register, and the deceased mark it made is taken back.</summary>
    public async Task<Result<DeathRecordDto>> DeathInErrorAsync(Guid id, VitalEntryErrorRequest request, CancellationToken ct)
    {
        var death = await db.Set<DeathRecord>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (death is null)
        {
            return Error.NotFound("Death", id);
        }

        db.SetExpectedVersion(death, request.Version);
        var marked = death.MarkEnteredInError(request.Reason, currentUser.UserId);
        if (marked.IsFailure)
        {
            return marked.Error!;
        }

        await deaths.ClearDeceasedAsync(death.PatientId, death.DiedAt, $"the death certificate {death.SerialNumber} was entered in error", ct);
        await db.SaveChangesAsync(ct);
        return await DeathDtoAsync(death, ct);
    }

    /// <summary>The month's births by the local date of birth, without those entered in error.</summary>
    public async Task<BirthRegister> BirthRegisterAsync(int year, int month, CancellationToken ct)
    {
        var (start, end) = MonthUtc(year, month);
        var births = await db.Set<BirthRecord>().AsNoTracking()
            .Where(b => b.Status != VitalReportStatus.EnteredInError && b.BornAt >= start && b.BornAt < end)
            .OrderBy(b => b.BornAt)
            .ToListAsync(ct);
        var mothers = await PatientsAsync(births.Select(b => b.MotherPatientId), ct);
        var hidden = await HiddenAsync(mothers.Values, ct);
        var today = time.Today();
        var rows = births.Select(b =>
        {
            var mother = mothers.GetValueOrDefault(b.MotherPatientId);
            var masked = hidden.Contains(b.MotherPatientId);
            return new BirthRegisterRow(
                b.Id, b.SerialNumber, b.BornAt, b.Outcome, b.Sex, b.WeightGrams, masked ? PatientNames.Restricted(mother!.FullName) : mother?.FullName ?? string.Empty,
                masked ? Restricted : mother?.Uhid ?? string.Empty, masked ? null : b.FatherName, masked ? null : b.ChildName, b.Status,
                VitalRegistration.DueBy(b.BirthDate), Overdue(b.Status, b.BirthDate, today), b.ReportedOn, b.RegistrationNumber);
        }).ToList();
        return new BirthRegister(year, month, rows, rows.Count(r => r.Outcome == BirthOutcome.LiveBirth), rows.Count(r => r.Outcome == BirthOutcome.StillBirth),
            rows.Count(r => r.Overdue));
    }

    public async Task<DeathRegister> DeathRegisterAsync(int year, int month, CancellationToken ct)
    {
        var (start, end) = MonthUtc(year, month);
        var records = await db.Set<DeathRecord>().AsNoTracking()
            .Where(d => d.Status != VitalReportStatus.EnteredInError && d.DiedAt >= start && d.DiedAt < end)
            .OrderBy(d => d.DiedAt)
            .ToListAsync(ct);
        var patients = await PatientsAsync(records.Select(d => d.PatientId), ct);
        var hidden = await HiddenAsync(patients.Values, ct);
        var today = time.Today();
        var rows = records.Select(d =>
        {
            var p = patients.GetValueOrDefault(d.PatientId);
            var masked = hidden.Contains(d.PatientId);
            return new DeathRegisterRow(
                d.Id, d.SerialNumber, d.DiedAt, masked ? PatientNames.Restricted(p!.FullName) : p?.FullName ?? string.Empty, masked ? Restricted : p?.Uhid ?? string.Empty,
                p?.Gender.ToString(), p is null ? null : AgeAt(p, d.DiedAt), masked ? Restricted : d.UnderlyingCause, d.Manner, d.MedicoLegal, d.CertifiedBy,
                d.Status, VitalRegistration.DueBy(d.DeathDate), Overdue(d.Status, d.DeathDate, today), d.ReportedOn, d.RegistrationNumber);
        }).ToList();
        return new DeathRegister(year, month, rows, rows.Count(r => r.MedicoLegal), rows.Count(r => r.Overdue));
    }

    public async Task<BirthRecordDto> BirthDtoAsync(BirthRecord b, CancellationToken ct)
    {
        var people = await PatientsAsync([b.MotherPatientId, .. b.BabyPatientId is { } baby ? new[] { baby } : []], ct);
        var mother = people.GetValueOrDefault(b.MotherPatientId);
        return new BirthRecordDto(
            b.Id, b.SerialNumber, b.MotherPatientId, mother?.Uhid ?? string.Empty, mother?.FullName ?? string.Empty, b.BabyPatientId,
            b.BabyPatientId is { } id ? people.GetValueOrDefault(id)?.Uhid : null, b.AdmissionId, b.Status, b.BornAt, b.Outcome, b.Sex, b.WeightGrams,
            b.WeeksOfGestation, b.Method, b.Attendant, b.ChildName, b.FatherName, b.MotherAgeYears, b.LiveChildren, b.InformantName, b.StillBirthCause,
            VitalRegistration.DueBy(b.BirthDate), b.ReportedOn, b.RegistrationNumber, b.ErrorReason,
            b.Status == VitalReportStatus.Recorded && b.IsRecordedBy(currentUser.UserId), b.Version);
    }

    public async Task<DeathRecordDto> DeathDtoAsync(DeathRecord d, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().FirstAsync(p => p.Id == d.PatientId, ct);
        return new DeathRecordDto(
            d.Id, d.SerialNumber, d.PatientId, patient.Uhid, patient.FullName, patient.Gender.ToString(), d.AdmissionId, d.Status, d.DiedAt,
            AgeAt(patient, d.DiedAt), d.Causes, d.UnderlyingCause, d.OtherConditions, d.Manner, d.MedicoLegal, d.Pregnancy, d.CertifiedBy,
            d.CertifierRegistrationNumber, d.CertifiedAt, VitalRegistration.DueBy(d.DeathDate), d.ReportedOn, d.RegistrationNumber, d.ErrorReason,
            d.Status == VitalReportStatus.Recorded && d.IsCertifiedBy(currentUser.UserId), d.Version);
    }

    private const string Restricted = "Restricted";

    /// <summary>
    /// The restricted (VIP) patients the user may not open: their rows show initials, not the name, UHID or cause (R-3),
    /// as their record itself is refused to them.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> HiddenAsync(IEnumerable<Patient> patients, CancellationToken ct)
    {
        var vip = patients.Where(p => p.IsVip).Select(p => p.Id).ToList();
        if (vip.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var openable = await guard.OpenableAsync(vip, ct);
        return vip.Where(id => !openable.Contains(id)).ToHashSet();
    }

    private static bool Overdue(VitalReportStatus status, DateOnly eventDate, DateOnly today) =>
        status == VitalReportStatus.Recorded && today > VitalRegistration.DueBy(eventDate);

    private static int? AgeAt(Patient patient, DateTimeOffset at)
    {
        if (patient.DateOfBirth is not { } dob)
        {
            return null;
        }

        var on = VitalRegistration.LocalDate(at);
        var age = on.Year - dob.Year;
        return on < dob.AddYears(age) ? age - 1 : age;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) MonthUtc(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        var india = TimeSpan.FromHours(5.5);
        return (new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), india), new DateTimeOffset(first.AddMonths(1).ToDateTime(TimeOnly.MinValue), india));
    }

    private Task<Patient?> PatientAsync(Guid id, CancellationToken ct) =>
        db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    private async Task<Dictionary<Guid, Patient>> PatientsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Set<Patient>().AsNoTracking().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
    }
}
