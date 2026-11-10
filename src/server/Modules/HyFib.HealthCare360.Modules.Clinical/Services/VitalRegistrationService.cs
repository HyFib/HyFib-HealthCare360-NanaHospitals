using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.VitalRegistration;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Births and deaths in the hospital (<see cref="BirthRecord"/>, <see cref="DeathRecord"/>): recorded, certified,
/// reported to the Registrar within 21 days, and the month's registers. A certified death marks the patient deceased,
/// and its certificate is confirmed by a second doctor before it is reported (<see cref="DeathCertificateApproval"/>).
/// </summary>
internal sealed class VitalRegistrationService(
    HospitalDbContext db,
    INumberSeries numbers,
    PatientDeaths deaths,
    PatientAccessGuard guard,
    ICurrentUser currentUser,
    IApprovals approvals,
    ISettingsService settings,
    IAuditLogger audit,
    TimeProvider time)
{
    /// <summary>
    /// Per facility: whether a second doctor confirms each death certificate before it is reported
    /// (<see cref="DeathCertificateApproval"/>). On unless the facility sets it to <c>false</c>, as one with a single doctor does.
    /// </summary>
    public const string ConfirmationSetting = "mrd.deathCertificateConfirmation";

    private const string VoidWaiting =
        "A second doctor is asked to agree that this confirmed certificate was entered in error. Until they decide, it stands as it is: whoever asked can withdraw the request (Approvals, Asked by me).";

    private const string ConfirmationWaiting =
        "This certificate is waiting for a second doctor's confirmation. Whoever asked can withdraw the request (Approvals, Asked by me), or the confirming doctor can return it; then change the certificate.";

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

            // A certificate waiting for a second doctor is not changed under them.
            if (await ConfirmationWaitingSinceAsync(death.Id, ct) is not null)
            {
                return Error.Conflict("death.confirmation_waiting", ConfirmationWaiting);
            }

            if (await VoidWaitingSinceAsync(death.Id, ct) is not null)
            {
                return Error.Conflict("death.void_waiting", VoidWaiting);
            }
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
        if (!await ConfirmationRequiredAsync(death.FacilityId, ct))
        {
            await db.SaveChangesAsync(ct);
            return await DeathDtoAsync(death, ct);
        }

        // The certificate, and the request for a second doctor to confirm it as saved, are kept together or not at all.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        var asked = await AskConfirmationAsync(death, ct);
        if (asked.IsFailure)
        {
            return asked.Error!;
        }

        await transaction.CommitAsync(ct);
        return await DeathDtoAsync(death, ct);
    }

    /// <summary>
    /// Confirms a death certificate by approving the request for it, in the approval's transaction (the approval saves):
    /// the certificate as it was when confirmation was asked for, or not at all. The confirming doctor must be able to
    /// open the patient's record, and is named on the certificate with their practitioner record's registration number.
    /// </summary>
    internal async Task<Result<string>> ConfirmApprovedAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<DeathConfirmationPayload>(approval.Payload);
        // The certificate as it is now, tracked: the version checked is the one the save is made against.
        var death = await db.Set<DeathRecord>().FirstOrDefaultAsync(d => d.Id == asked.DeathId, ct);
        if (death is null)
        {
            return Error.NotFound("Death", asked.DeathId);
        }

        if (death.Version != approval.SubjectVersion)
        {
            return Error.Rule("approval.stale",
                $"Death certificate {death.SerialNumber} changed after its confirmation was asked for. Ask again, for the certificate as it is now.");
        }

        var open = await guard.EnsureCanOpenChartAsync(death.PatientId, ct);
        if (open.IsFailure)
        {
            return open.Error!;
        }

        if (currentUser.UserId is not { } doctor)
        {
            return Error.Forbidden("approval.staff_only", "Only a signed-in doctor confirms a death certificate.");
        }

        var registration = currentUser.PractitionerId is { } practitioner
            ? await db.Set<Practitioner>().AsNoTracking().Where(p => p.Id == practitioner).Select(p => p.RegistrationNumber).FirstOrDefaultAsync(ct)
            : null;
        var confirmed = death.Confirm(doctor, currentUser.DisplayName ?? currentUser.UserName ?? "Doctor", registration, time.GetUtcNow());
        return confirmed.IsFailure ? confirmed.Error! : $"Death certificate {death.SerialNumber} confirmed";
    }

    /// <summary>
    /// The certifying doctor asks a second doctor to confirm the certificate as it stands: one saved before the facility
    /// asked for confirmations, or whose request was returned, withdrawn or ran out. A correction asks by itself.
    /// </summary>
    public async Task<Result<DeathRecordDto>> AskDeathConfirmationAsync(Guid id, AskDeathConfirmationRequest request, CancellationToken ct)
    {
        var death = await db.Set<DeathRecord>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (death is null)
        {
            return Error.NotFound("Death", id);
        }

        if (death.Version != request.Version)
        {
            return Error.ConcurrencyConflict;
        }

        if (death.Status != VitalReportStatus.Recorded)
        {
            return VitalRegistration.Closed(death.Status);
        }

        if (!death.IsCertifiedBy(currentUser.UserId))
        {
            return Error.Forbidden("vital.not_own_entry",
                "Only the doctor who certified this death asks for its confirmation. Another doctor who corrects the certificate becomes its certifier, and asks by correcting it.");
        }

        if (!await ConfirmationRequiredAsync(death.FacilityId, ct))
        {
            return Error.Rule("death.confirmation_off", "This facility does not ask a second doctor to confirm death certificates.");
        }

        if (death.IsConfirmed)
        {
            return Error.Rule("death.confirmed", $"This certificate is already confirmed, by {death.ConfirmedBy}.");
        }

        var asked = await AskConfirmationAsync(death, ct);
        return asked.IsFailure ? asked.Error! : await DeathDtoAsync(death, ct);
    }

    /// <summary>Asks a second doctor to confirm the certificate as saved: what they confirm, without naming the patient.</summary>
    private Task<Result<ApprovalRequestDto>> AskConfirmationAsync(DeathRecord death, CancellationToken ct) =>
        approvals.RequestAsync(
            DeathCertificateApproval.Key,
            new ApprovalSubject(nameof(DeathRecord), death.Id, death.Version, death.FacilityId, death.PatientId),
            new DeathConfirmationPayload(death.Id),
            $"Death certificate {death.SerialNumber}",
            [
                new("Death register no.", death.SerialNumber),
                new("Date and time of death", DocumentFormat.DateTime(death.DiedAt)),
                .. death.Causes.Select((c, i) => new ApprovalDetail(
                    $"Part I ({(char)('a' + i)}){(i == death.Causes.Count - 1 ? ", underlying cause" : string.Empty)}",
                    c.Interval is null ? c.Condition : $"{c.Condition} ({c.Interval})")),
                .. death.OtherConditions is { } other ? [new ApprovalDetail("Part II", other)] : Array.Empty<ApprovalDetail>(),
                new("Manner of death", VitalRegistrationDocuments.Manner(death.Manner)),
                new("Medico-legal case", death.MedicoLegal ? "Yes" : "No"),
                .. VitalRegistrationDocuments.Pregnancy(death.Pregnancy) is { } pregnancy ? [new ApprovalDetail("Pregnancy", pregnancy)] : Array.Empty<ApprovalDetail>(),
                new("Certified by", $"{death.CertifiedBy}, registration no. {death.CertifierRegistrationNumber}"),
            ],
            "A second doctor confirms the cause of death before the hospital reports it to the Registrar.",
            ct);

    private async Task<bool> ConfirmationRequiredAsync(Guid facilityId, CancellationToken ct) =>
        await settings.GetAsync<bool?>(ConfirmationSetting, facilityId, ct) != false;

    private async Task<DateTimeOffset?> ConfirmationWaitingSinceAsync(Guid deathId, CancellationToken ct) =>
        (await approvals.WaitingSinceAsync(DeathCertificateApproval.Key, nameof(DeathRecord), [deathId], ct)).TryGetValue(deathId, out var since)
            ? since
            : null;

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
        if (await VoidWaitingSinceAsync(death.Id, ct) is not null)
        {
            return Error.Conflict("death.void_waiting", VoidWaiting);
        }

        var reported = death.Report(
            new RegistrarReport(request.ReportedOn, request.RegistrationNumber), time.Today(), await ConfirmationRequiredAsync(death.FacilityId, ct));
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

    /// <summary>
    /// A certificate made on the wrong patient: it leaves the register, and the deceased mark it made is taken back. Only its
    /// certifier marks it, and only while it is not reported to the Registrar (a correction then goes through the
    /// Registrar). A certificate a second doctor has confirmed took two doctors to make, so undoing it takes two
    /// (maker-checker, slice 8): the certifier asks, with the reason, and another doctor who confirms certificates agrees
    /// (<see cref="DeathCertificateVoidApproval"/>); until then the certificate and the deceased status stand.
    /// </summary>
    public async Task<Result<DeathInErrorOutcome>> DeathInErrorAsync(Guid id, VitalEntryErrorRequest request, CancellationToken ct)
    {
        var death = await db.Set<DeathRecord>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (death is null)
        {
            return Error.NotFound("Death", id);
        }

        db.SetExpectedVersion(death, request.Version);
        if (death.IsCertifiedBy(currentUser.UserId) && await ConfirmationWaitingSinceAsync(death.Id, ct) is not null)
        {
            return Error.Conflict("death.confirmation_waiting", ConfirmationWaiting);
        }

        if (death.Status == VitalReportStatus.Recorded && death.IsConfirmed)
        {
            if (death.Version != request.Version)
            {
                return Error.ConcurrencyConflict;
            }

            if (!death.IsCertifiedBy(currentUser.UserId))
            {
                return Error.Forbidden("vital.not_own_entry", "Only the doctor who certified this death can mark it as entered in error.");
            }

            var asked = await AskVoidAsync(death, request.Reason.Trim(), ct);
            return asked.IsFailure ? asked.Error! : new DeathInErrorOutcome(await DeathDtoAsync(death, ct), asked.Value);
        }

        var marked = death.MarkEnteredInError(request.Reason, currentUser.UserId);
        if (marked.IsFailure)
        {
            return marked.Error!;
        }

        await deaths.ClearDeceasedAsync(death.PatientId, death.DiedAt, $"the death certificate {death.SerialNumber} was entered in error", ct);
        await db.SaveChangesAsync(ct);
        return new DeathInErrorOutcome(await DeathDtoAsync(death, ct), null);
    }

    /// <summary>The certifier asks another doctor to agree that a confirmed certificate was entered in error: what goes, and what stays until then.</summary>
    private Task<Result<ApprovalRequestDto>> AskVoidAsync(DeathRecord death, string reason, CancellationToken ct) =>
        approvals.RequestAsync(
            DeathCertificateVoidApproval.Key,
            new ApprovalSubject(nameof(DeathRecord), death.Id, death.Version, death.FacilityId, death.PatientId),
            new DeathVoidPayload(death.Id, death.DiedAt),
            $"Death certificate {death.SerialNumber}: confirmed → entered in error",
            [
                new("Death register no.", death.SerialNumber),
                new("Date and time of death", DocumentFormat.DateTime(death.DiedAt)),
                new("Underlying cause", death.UnderlyingCause),
                new("Certified by", $"{death.CertifiedBy}, registration no. {death.CertifierRegistrationNumber}"),
                new("Confirmed by", $"{death.ConfirmedBy}, registration no. {death.ConfirmerRegistrationNumber}"),
                new("Certificate", "confirmed, not yet reported → entered in error, out of the death register"),
                new("What it reaches",
                    "The patient is no longer recorded as deceased, unless another certificate stands for them. Until this is approved, the certificate and the deceased status stand."),
            ],
            reason,
            ct);

    /// <summary>
    /// Marks the confirmed certificate entered in error once a second doctor approves, in the approval's transaction: only
    /// the certificate as it was when asked (<c>approval.stale</c>), still not reported to the Registrar. The deceased mark it
    /// made is taken back only when no other certificate stands for the patient. Audited with both names.
    /// </summary>
    internal async Task<Result<string>> ApplyVoidAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<DeathVoidPayload>(approval.Payload);
        var death = await db.Set<DeathRecord>().FirstOrDefaultAsync(d => d.Id == asked.DeathId, ct);
        if (death is null)
        {
            return Error.NotFound("Death", asked.DeathId);
        }

        if (death.Status == VitalReportStatus.Reported)
        {
            return VitalRegistration.Closed(death.Status);
        }

        if (death.Version != approval.SubjectVersion)
        {
            return Error.Rule("approval.stale",
                $"Death certificate {death.SerialNumber} changed after this was asked for, so nothing was changed. Ask again, for the certificate as it is now.");
        }

        var open = await guard.EnsureCanOpenChartAsync(death.PatientId, ct);
        if (open.IsFailure)
        {
            return open.Error!;
        }

        // Asked by the certifier: marked in their name, approved in the second doctor's.
        var marked = death.MarkEnteredInError(approval.Reason, approval.RequestedBy);
        if (marked.IsFailure)
        {
            return marked.Error!;
        }

        var another = await db.Set<DeathRecord>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .AnyAsync(d => d.PatientId == death.PatientId && d.Id != death.Id && d.Status != VitalReportStatus.EnteredInError, ct);
        if (!another)
        {
            await deaths.ClearDeceasedAsync(death.PatientId, death.DiedAt, $"the death certificate {death.SerialNumber} was entered in error", ct);
        }

        var approver = currentUser.DisplayName ?? currentUser.UserName;
        audit.Add(new AuditEntry(AuditCategories.Clinical, "death.voided",
            Fit($"Death certificate {death.SerialNumber}: confirmed → entered in error: {approval.Reason} (asked for by {approval.RequestedByName}, approved by {approver})", 1000))
        {
            PatientId = death.PatientId,
            EntityType = nameof(DeathRecord),
            EntityId = death.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new
            {
                before = "confirmed",
                after = "entered in error",
                deceasedCleared = !another,
                confirmedBy = death.ConfirmedBy,
                approval = approval.Id,
                requestedBy = approval.RequestedBy,
                approvedBy = currentUser.UserId,
            },
        });
        return another
            ? $"Death certificate {death.SerialNumber} entered in error; another certificate stands, so the patient stays deceased"
            : $"Death certificate {death.SerialNumber} entered in error; the patient is no longer recorded as deceased";
    }

    private async Task<DateTimeOffset?> VoidWaitingSinceAsync(Guid deathId, CancellationToken ct) =>
        (await approvals.WaitingSinceAsync(DeathCertificateVoidApproval.Key, nameof(DeathRecord), [deathId], ct)).TryGetValue(deathId, out var since)
            ? since
            : null;

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

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
        var confirming = new Dictionary<Guid, bool>();
        foreach (var facility in records.Select(d => d.FacilityId).Distinct())
        {
            confirming[facility] = await ConfirmationRequiredAsync(facility, ct);
        }

        var today = time.Today();
        var rows = records.Select(d =>
        {
            var p = patients.GetValueOrDefault(d.PatientId);
            var masked = hidden.Contains(d.PatientId);
            return new DeathRegisterRow(
                d.Id, d.SerialNumber, d.DiedAt, masked ? PatientNames.Restricted(p!.FullName) : p?.FullName ?? string.Empty, masked ? Restricted : p?.Uhid ?? string.Empty,
                p?.Gender.ToString(), p is null ? null : AgeAt(p, d.DiedAt), masked ? Restricted : d.UnderlyingCause, d.Manner, d.MedicoLegal, d.CertifiedBy,
                d.Status, VitalRegistration.DueBy(d.DeathDate), Overdue(d.Status, d.DeathDate, today), d.ReportedOn, d.RegistrationNumber,
                d.Status == VitalReportStatus.Recorded && confirming[d.FacilityId] && !d.IsConfirmed);
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
            d.Status == VitalReportStatus.Recorded && d.IsCertifiedBy(currentUser.UserId), d.Version)
        {
            ConfirmationRequired = await ConfirmationRequiredAsync(d.FacilityId, ct),
            ConfirmationWaitingSince = d.Status == VitalReportStatus.Recorded ? await ConfirmationWaitingSinceAsync(d.Id, ct) : null,
            ConfirmedBy = d.ConfirmedBy,
            ConfirmerRegistrationNumber = d.ConfirmerRegistrationNumber,
            ConfirmedAt = d.ConfirmedAt,
            VoidWaitingSince = d.Status == VitalReportStatus.Recorded && d.IsConfirmed ? await VoidWaitingSinceAsync(d.Id, ct) : null,
        };
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
