using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// PCPNDT Form F for ultrasounds in pregnancy: the woman's details and declaration before the scan, the result and the
/// doctor's declaration after it, and the month's register for the Appropriate Authority (<see cref="FormF"/>).
/// </summary>
internal sealed class FormFService(
    HospitalDbContext db,
    INumberSeries numbers,
    ISettingsService settings,
    DiagnosticsLookups lookups,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>The facility's registration under the PCPNDT Act (s.18; Form B), printed on every Form F.</summary>
    public const string RegistrationSetting = "pcpndt.registrationNumber";

    public async Task<FormFDto?> GetAsync(Guid studyId, CancellationToken ct)
    {
        var form = await db.Set<FormF>().AsNoTracking().FirstOrDefaultAsync(f => f.ImagingStudyId == studyId, ct);
        return form is null ? null : await ToDtoAsync(form, ct);
    }

    public async Task<Result<FormFDto>> DeclareAsync(Guid studyId, SaveFormFDeclarationRequest request, CancellationToken ct)
    {
        var study = await db.Set<ImagingStudy>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == studyId, ct);
        if (study is null)
        {
            return Error.NotFound(nameof(ImagingStudy), studyId);
        }

        if (study.Modality != Modality.US)
        {
            return Error.Rule("form_f.not_ultrasound", "Form F is recorded for ultrasounds only.");
        }

        if (study.Status == ImagingStudyStatus.Cancelled)
        {
            return Error.Rule("imaging.cancelled", "The study was cancelled.");
        }

        var patient = await lookups.PatientAsync(study.PatientId, ct);
        if (patient is { Gender: Gender.Male })
        {
            return Error.Rule("form_f.not_a_woman", "Form F is recorded for an ultrasound of a pregnant woman.");
        }

        var form = await db.Set<FormF>().FirstOrDefaultAsync(f => f.ImagingStudyId == studyId, ct);
        if (form is null && study.AcquiredAt is not null)
        {
            // The declaration comes before the scan: one recorded after it would not be the woman's declaration.
            return Error.Rule("form_f.after_scan",
                "This ultrasound was done without a Form F declaration. Report it to the clinic's PCPNDT in-charge; a declaration cannot be recorded afterwards.");
        }

        if (form is null)
        {
            var registration = await RegistrationAsync(study.FacilityId, ct);
            if (registration is null)
            {
                return Error.Rule("form_f.not_registered",
                    $"Enter the clinic's PCPNDT registration number ({RegistrationSetting}) under Administration › Settings before recording a Form F.");
            }

            form = new FormF
            {
                FacilityId = study.FacilityId,
                ImagingStudyId = study.Id,
                PatientId = study.PatientId,
                FormNumber = await numbers.NextAsync(DiagnosticsNumberSeries.FormF, study.FacilityId, ct),
                RegistrationNumber = registration,
            };
            db.Add(form);
        }
        else
        {
            // The first save creates the form; a later one names the version it read (REL-04), or is refused as stale.
            db.SetExpectedVersion(form, request.Version);
        }

        var declared = form.Declare(
            new FormFDeclaration(
                request.AgeYears, request.HusbandOrFatherName, request.Address, request.Phone, request.LivingSons, request.LivingDaughters,
                request.LastMenstrualPeriod, request.WeeksOfPregnancy, request.Referral, request.ReferringDoctorName, request.ReferringDoctorAddress,
                request.Indications, request.OtherIndication, request.SignedFormFileId),
            currentUser.UserId,
            time.GetUtcNow());
        if (declared.IsFailure)
        {
            return declared.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(form, ct);
    }

    public async Task<Result<FormFDto>> CompleteAsync(Guid studyId, CompleteFormFRequest request, CancellationToken ct)
    {
        var form = await db.Set<FormF>().FirstOrDefaultAsync(f => f.ImagingStudyId == studyId, ct);
        if (form is null)
        {
            return Error.Rule("form_f.required", "Record the woman's Form F details and declaration first.");
        }

        var study = await db.Set<ImagingStudy>().AsNoTracking().FirstAsync(s => s.Id == studyId, ct);
        if (study.AcquiredAt is not { } acquiredAt)
        {
            return Error.Rule("form_f.not_scanned", "Complete the Form F once the ultrasound has been done.");
        }

        if (currentUser.UserId is not { } doctor)
        {
            return Error.Forbidden("auth.required", "Sign in first.");
        }

        db.SetExpectedVersion(form, request.Version);
        var completed = form.Complete(
            new FormFResult(request.Result, request.ConveyedTo, request.ConveyedOn, request.MtpIndicated, request.MtpIndication),
            doctor,
            currentUser.DisplayName ?? currentUser.UserName ?? "Doctor",
            DiagnosticsTime.LocalDate(acquiredAt),
            DiagnosticsTime.Today(time),
            time.GetUtcNow());
        if (completed.IsFailure)
        {
            return completed.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(form, ct);
    }

    /// <summary>The facility's forms of a month: completed ones by the date of the ultrasound, and those still open.</summary>
    public async Task<FormFRegister> RegisterAsync(int year, int month, CancellationToken ct)
    {
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var (start, end) = (DiagnosticsTime.StartOfDayUtc(first), DiagnosticsTime.StartOfDayUtc(last.AddDays(1)));
        var forms = await db.Set<FormF>().AsNoTracking()
            .Where(f => (f.Status == FormFStatus.Completed && f.ProcedureDate >= first && f.ProcedureDate <= last)
                        || (f.Status != FormFStatus.Completed && f.DeclaredAt >= start && f.DeclaredAt < end))
            .OrderBy(f => f.FormNumber)
            .ToListAsync(ct);
        var studies = await StudiesAsync(forms, ct);
        var patients = await lookups.PatientsAsync([.. forms.Select(f => f.PatientId).Distinct()], ct);
        var rows = forms.Select(f => new FormFRegisterRow(
            f.Id, f.FormNumber, f.ImagingStudyId, studies.GetValueOrDefault(f.ImagingStudyId) ?? string.Empty,
            patients.GetValueOrDefault(f.PatientId)?.Uhid ?? string.Empty, patients.GetValueOrDefault(f.PatientId)?.FullName ?? string.Empty,
            f.AgeYears, f.HusbandOrFatherName, f.LivingSons, f.LivingDaughters, f.WeeksOfPregnancy, f.LastMenstrualPeriod, ReferredBy(f),
            f.Indications, f.ProcedureDate, f.ResultText, f.MtpIndicated, f.DoctorName, f.Status)).ToList();
        var registration = currentUser.FacilityId is { } facility ? await RegistrationAsync(facility, ct) : null;
        return new FormFRegister(year, month, registration ?? string.Empty, rows, rows.Count(r => r.Status == FormFStatus.Declared));
    }

    public static string ReferredBy(FormF form) => form.Referral == FormFReferral.Self
        ? $"Self-referral ({form.DoctorName ?? "performing doctor"})"
        : $"{form.ReferringDoctorName}, {form.ReferringDoctorAddress}";

    private async Task<string?> RegistrationAsync(Guid facilityId, CancellationToken ct) =>
        await settings.GetAsync<string?>(RegistrationSetting, facilityId, ct) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private async Task<Dictionary<Guid, string>> StudiesAsync(IEnumerable<FormF> forms, CancellationToken ct)
    {
        var ids = forms.Select(f => f.ImagingStudyId).ToList();
        return await db.Set<ImagingStudy>().AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.AccessionNumber, ct);
    }

    private async Task<FormFDto> ToDtoAsync(FormF f, CancellationToken ct)
    {
        var patient = await lookups.PatientAsync(f.PatientId, ct);
        var accession = await db.Set<ImagingStudy>().AsNoTracking().Where(s => s.Id == f.ImagingStudyId).Select(s => s.AccessionNumber).FirstAsync(ct);
        var users = await lookups.UserNamesAsync([f.DeclarationRecordedBy], ct);
        return new FormFDto(
            f.Id, f.FormNumber, f.ImagingStudyId, accession, f.PatientId, patient?.Uhid ?? string.Empty, patient?.FullName ?? string.Empty, f.Status,
            f.RegistrationNumber, f.AgeYears, f.HusbandOrFatherName, f.Address, f.Phone, f.LivingSons, f.LivingDaughters, f.LastMenstrualPeriod,
            f.WeeksOfPregnancy, f.Referral, f.ReferringDoctorName, f.ReferringDoctorAddress, f.Indications, f.OtherIndication, f.SignedFormFileId,
            f.DeclaredAt, f.DeclarationRecordedBy is { } by ? users.GetValueOrDefault(by) : null, f.ProcedureDate, f.ResultText, f.ResultConveyedTo,
            f.ResultConveyedOn, f.MtpIndicated, f.MtpIndication, f.DoctorName, f.CompletedAt, f.Version, f.CancelReason);
    }
}
