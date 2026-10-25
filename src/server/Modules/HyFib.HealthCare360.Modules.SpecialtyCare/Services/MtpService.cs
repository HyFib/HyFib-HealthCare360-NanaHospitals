using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Mtp;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Mtp;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Termination of pregnancy (<see cref="MtpCase"/>): the woman's consent and the doctors' opinion before it, the admission
/// register once it is done, and the month's report to the Chief Medical Officer. Opening a case puts the patient's record
/// in the MTP category, so the rest of her chart closes to staff without it (R-4).
/// </summary>
internal sealed class MtpService(
    HospitalDbContext db,
    INumberSeries numbers,
    ISettingsService settings,
    PatientAccessGuard guard,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>The place's certificate of approval for terminations (MTP Rules 2003, rule 5; Form B), printed on every form.</summary>
    public const string ApprovalSetting = "mtp.approvalNumber";

    public async Task<IReadOnlyList<MtpCaseListItem>> ListAsync(Guid? patientId, MtpCaseStatus? status, CancellationToken ct)
    {
        var query = db.Set<MtpCase>().AsNoTracking();
        if (patientId is { } patient)
        {
            query = query.Where(c => c.PatientId == patient);
        }

        if (status is { } s)
        {
            query = query.Where(c => c.Status == s);
        }

        var cases = await query.OrderByDescending(c => c.CertifiedAt).Take(200).ToListAsync(ct);
        var patients = await PatientsAsync(cases.Select(c => c.PatientId), ct);
        var hidden = await HiddenAsync(patients.Values, ct);
        return [.. cases.Select(c =>
        {
            var patient = patients.GetValueOrDefault(c.PatientId);
            var masked = hidden.Contains(c.PatientId);
            return new MtpCaseListItem(
                c.Id, c.SerialNumber, c.PatientId, masked ? Restricted : patient?.Uhid ?? string.Empty,
                masked ? PatientNames.Restricted(patient!.FullName) : patient?.FullName ?? string.Empty, c.Status, c.WeeksOfGestation, c.CertifiedAt,
                c.TerminatedOn);
        })];
    }

    public async Task<MtpCaseDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var mtp = await db.Set<MtpCase>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return mtp is null ? null : await ToDtoAsync(mtp, ct);
    }

    public Task<Guid?> PatientOfAsync(Guid id, CancellationToken ct) =>
        db.Set<MtpCase>().AsNoTracking().Where(c => c.Id == id).Select(c => (Guid?)c.PatientId).FirstOrDefaultAsync(ct);

    /// <summary>Opens a case with the consent and the opinion, or corrects them (<paramref name="id"/>) before the termination.</summary>
    public async Task<Result<MtpCaseDto>> SaveAsync(Guid? id, SaveMtpCaseRequest request, CancellationToken ct)
    {
        MtpCase? mtp;
        if (id is { } existing)
        {
            mtp = await db.Set<MtpCase>().FirstOrDefaultAsync(c => c.Id == existing, ct);
            if (mtp is null)
            {
                return Error.NotFound(nameof(MtpCase), existing);
            }

            if (mtp.PatientId != request.PatientId)
            {
                return Error.Field("patientId", "A case stays with the woman it was opened for.");
            }

            db.SetExpectedVersion(mtp, request.Version);
        }
        else
        {
            if (await SpecialtyLookups.EnsurePatientAsync(db, request.PatientId, ct) is { IsFailure: true } invalid)
            {
                return invalid.Error!;
            }

            var gender = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == request.PatientId).Select(p => p.Gender).FirstAsync(ct);
            if (gender == Gender.Male)
            {
                return Error.Rule("mtp.not_a_woman", "A termination of pregnancy is recorded for a woman.");
            }

            if (currentUser.FacilityId is not { } facility)
            {
                return Error.Rule("facility.required", "Choose the facility first.");
            }

            var approval = await ApprovalAsync(facility, ct);
            if (approval is null)
            {
                return Error.Rule("mtp.not_approved",
                    $"Enter the place's MTP certificate of approval ({ApprovalSetting}) under Administration › Settings before recording a termination.");
            }

            var (admissionId, _) = await SpecialtyLookups.EncounterAsync(db, request.PatientId, request.AdmissionId, null, ct);
            mtp = new MtpCase
            {
                FacilityId = facility,
                PatientId = request.PatientId,
                AdmissionId = admissionId,
                SerialNumber = await numbers.NextAsync(SpecialtyCareNumberSeries.MtpRegister, facility, ct),
                ApprovalNumber = approval,
            };
            db.Add(mtp);
        }

        var certified = mtp.Certify(
            new MtpCertification(
                request.AgeYears, request.MentallyIll, request.ConsentBy, request.GuardianName, request.GuardianRelation, request.ConsentOn,
                request.ConsentFileId, request.WeeksOfGestation, request.Grounds, request.Category, request.MedicalBoardReference, request.Opinions),
            time.Today(),
            currentUser.UserId,
            time.GetUtcNow());
        if (certified.IsFailure)
        {
            return certified.Error!;
        }

        await guard.FlagAsync(mtp.PatientId, SensitiveCategory.Mtp, $"an MTP case ({mtp.SerialNumber})", ct);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(mtp, ct);
    }

    public async Task<Result<MtpCaseDto>> TerminateAsync(Guid id, RecordMtpTerminationRequest request, CancellationToken ct)
    {
        var mtp = await db.Set<MtpCase>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (mtp is null)
        {
            return Error.NotFound(nameof(MtpCase), id);
        }

        db.SetExpectedVersion(mtp, request.Version);
        var done = mtp.Terminate(
            new MtpTermination(request.TerminatedOn, request.Method, request.PerformedBy, request.DischargedOn, request.Outcome, request.Remarks),
            time.Today(),
            time.GetUtcNow());
        if (done.IsFailure)
        {
            return done.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(mtp, ct);
    }

    public async Task<Result<MtpCaseDto>> CancelAsync(Guid id, CancelMtpCaseRequest request, CancellationToken ct)
    {
        var mtp = await db.Set<MtpCase>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (mtp is null)
        {
            return Error.NotFound(nameof(MtpCase), id);
        }

        db.SetExpectedVersion(mtp, request.Version);
        var cancelled = mtp.Cancel(request.Reason);
        if (cancelled.IsFailure)
        {
            return cancelled.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(mtp, ct);
    }

    /// <summary>
    /// The month's admission register (Form III): the terminations done in it, by serial number, and the cases certified in
    /// it that are not yet done.
    /// </summary>
    public async Task<MtpRegister> RegisterAsync(int year, int month, CancellationToken ct)
    {
        var (first, last) = MonthOf(year, month);
        var (start, end) = SpecialtyClock.RangeUtc(first, last);
        var cases = await db.Set<MtpCase>().AsNoTracking()
            .Where(c => (c.Status == MtpCaseStatus.Terminated && c.TerminatedOn >= first && c.TerminatedOn <= last)
                        || (c.Status == MtpCaseStatus.Certified && c.CertifiedAt >= start && c.CertifiedAt < end))
            .OrderBy(c => c.SerialNumber)
            .ToListAsync(ct);
        var patients = await PatientsAsync(cases.Select(c => c.PatientId), ct);
        var admissions = await AdmissionsAsync(cases, ct);
        var hidden = await HiddenAsync(patients.Values, ct);
        var rows = cases.Select(c =>
        {
            var patient = patients.GetValueOrDefault(c.PatientId);
            var masked = hidden.Contains(c.PatientId);
            return new MtpRegisterRow(
                c.Id, c.SerialNumber,
                admissions.TryGetValue(c.AdmissionId ?? Guid.Empty, out var admitted) ? SpecialtyClock.ToLocalDate(admitted) : SpecialtyClock.ToLocalDate(c.CertifiedAt),
                masked ? PatientNames.Restricted(patient!.FullName) : patient?.FullName ?? string.Empty, masked ? Restricted : patient?.Uhid ?? string.Empty,
                masked ? null : patient?.GuardianName, c.AgeYears, patient?.Religion, masked ? Restricted : patient?.Address.ToString() ?? string.Empty,
                c.WeeksOfGestation, c.Grounds, c.Category, c.TerminatedOn, c.Method, c.DischargedOn, c.Outcome, OpinionBy(c), c.PerformedBy, c.Remarks);
        }).ToList();
        return new MtpRegister(year, month, await CurrentApprovalAsync(ct), rows, cases.Count(c => c.Status == MtpCaseStatus.Certified));
    }

    /// <summary>The month's report to the Chief Medical Officer (Form II): the terminations done, counted as the form asks.</summary>
    public async Task<MtpMonthlyReport> MonthlyReportAsync(int year, int month, CancellationToken ct)
    {
        var (first, last) = MonthOf(year, month);
        var done = await db.Set<MtpCase>().AsNoTracking()
            .Where(c => c.Status == MtpCaseStatus.Terminated && c.TerminatedOn >= first && c.TerminatedOn <= last)
            .ToListAsync(ct);
        var patients = await PatientsAsync(done.Select(c => c.PatientId), ct);

        return new MtpMonthlyReport(
            year, month, await CurrentApprovalAsync(ct), done.Count,
            Count(done, c => GestationBand(c.WeeksOfGestation), GestationBands),
            Count(done, c => ReligionOf(patients.GetValueOrDefault(c.PatientId)?.Religion), Religions),
            Count(done.SelectMany(c => c.Grounds.Select(g => (Case: c, Ground: g))), x => MtpLabels.Ground(x.Ground),
                [.. Enum.GetValues<MtpGround>().Select(MtpLabels.Ground)]),
            Count(done, c => MtpLabels.Method(c.Method!.Value), [.. Enum.GetValues<MtpMethod>().Select(MtpLabels.Method)]),
            Count(done, c => AgeBand(c.AgeYears), AgeBands));
    }

    public static string OpinionBy(MtpCase c) => c.WeeksOfGestation > MtpCase.TwoOpinionsWeeks
        ? $"Medical Board ({c.MedicalBoardReference}); {string.Join("; ", c.Opinions.Select(o => $"{o.DoctorName} ({o.RegistrationNumber})"))}"
        : string.Join("; ", c.Opinions.Select(o => $"{o.DoctorName} ({o.RegistrationNumber})"));

    private static readonly string[] GestationBands = ["Up to 12 weeks", "13 to 20 weeks", "21 to 24 weeks", "Beyond 24 weeks"];
    private static readonly string[] Religions = ["Hindu", "Muslim", "Christian", "Others"];
    private static readonly string[] AgeBands = ["Under 15", "15 to 19", "20 to 34", "35 and above"];

    private static string GestationBand(int weeks) => weeks switch
    {
        <= 12 => GestationBands[0],
        <= MtpCase.OneOpinionWeeks => GestationBands[1],
        <= MtpCase.TwoOpinionsWeeks => GestationBands[2],
        _ => GestationBands[3],
    };

    private static string AgeBand(int age) => age switch
    {
        < 15 => AgeBands[0],
        < 20 => AgeBands[1],
        < 35 => AgeBands[2],
        _ => AgeBands[3],
    };

    private static string ReligionOf(string? religion) =>
        Religions.FirstOrDefault(r => string.Equals(r, religion?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Religions[^1];

    private static IReadOnlyList<MtpCount> Count<T>(IEnumerable<T> items, Func<T, string> label, IReadOnlyList<string> labels)
    {
        var counts = items.GroupBy(label).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return [.. labels.Select(l => new MtpCount(l, counts.GetValueOrDefault(l)))];
    }

    private static (DateOnly First, DateOnly Last) MonthOf(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        return (first, first.AddMonths(1).AddDays(-1));
    }

    private const string Restricted = "Restricted";

    /// <summary>
    /// The restricted (VIP) patients among <paramref name="patients"/> the user may not open: their rows show initials,
    /// not the name, UHID or address (R-3), as a case of theirs opens only to those who may open the record.
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

    private async Task<string> CurrentApprovalAsync(CancellationToken ct) =>
        currentUser.FacilityId is { } facility ? await ApprovalAsync(facility, ct) ?? string.Empty : string.Empty;

    private async Task<string?> ApprovalAsync(Guid facilityId, CancellationToken ct) =>
        await settings.GetAsync<string?>(ApprovalSetting, facilityId, ct) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private async Task<Dictionary<Guid, Patient>> PatientsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Set<Patient>().AsNoTracking().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
    }

    private async Task<Dictionary<Guid, DateTimeOffset>> AdmissionsAsync(IEnumerable<MtpCase> cases, CancellationToken ct)
    {
        var ids = cases.Where(c => c.AdmissionId is not null).Select(c => c.AdmissionId!.Value).Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.Set<AdmissionRecord>().AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.AdmittedAt, ct);
    }

    private async Task<MtpCaseDto> ToDtoAsync(MtpCase c, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == c.PatientId).Select(p => new { p.Uhid, p.FullName }).FirstAsync(ct);
        var certifiedBy = c.CertifiedBy is { } by
            ? await db.Set<User>().AsNoTracking().Where(u => u.Id == by).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
            : null;
        return new MtpCaseDto(
            c.Id, c.SerialNumber, c.PatientId, patient.Uhid, patient.FullName, c.AdmissionId, c.Status, c.ApprovalNumber, c.AgeYears, c.MentallyIll,
            c.ConsentBy, c.GuardianName, c.GuardianRelation, c.ConsentOn, c.ConsentFileId, c.WeeksOfGestation, c.Grounds, c.Category,
            c.MedicalBoardReference, c.Opinions, c.CertifiedAt, certifiedBy, c.TerminatedOn, c.Method, c.PerformedBy, c.DischargedOn, c.Outcome,
            c.Remarks, c.CancelReason, c.Version);
    }
}

/// <summary>The words the forms use for the grounds, categories and methods.</summary>
internal static class MtpLabels
{
    public static string Ground(MtpGround ground) => ground switch
    {
        MtpGround.RiskToLife => "Risk to the life of the pregnant woman",
        MtpGround.InjuryToPhysicalHealth => "Grave injury to her physical health",
        MtpGround.InjuryToMentalHealth => "Grave injury to her mental health",
        MtpGround.Rape => "Pregnancy caused by rape",
        MtpGround.ContraceptiveFailure => "Failure of a contraceptive device or method",
        MtpGround.FoetalAbnormality => "Substantial risk of a serious foetal abnormality",
        _ => ground.ToString(),
    };

    public static string Category(MtpRule3BCategory category) => category switch
    {
        MtpRule3BCategory.SexualAssaultRapeOrIncest => "Survivor of sexual assault, rape or incest",
        MtpRule3BCategory.Minor => "Minor",
        MtpRule3BCategory.ChangeOfMaritalStatus => "Change of marital status during the pregnancy (widowhood or divorce)",
        MtpRule3BCategory.PhysicalDisability => "Woman with a physical disability",
        MtpRule3BCategory.MentalIllness => "Mentally ill woman, including with mental retardation",
        MtpRule3BCategory.FoetalMalformation => "Foetal malformation incompatible with life or with serious handicap",
        MtpRule3BCategory.HumanitarianSettingOrEmergency => "Woman in a humanitarian setting, disaster or emergency",
        _ => category.ToString(),
    };

    public static string Method(MtpMethod method) => method switch
    {
        MtpMethod.Medical => "Medical method of abortion",
        MtpMethod.VacuumAspiration => "Vacuum aspiration",
        MtpMethod.DilatationAndEvacuation => "Dilatation and evacuation",
        MtpMethod.Other => "Other",
        _ => method.ToString(),
    };
}
