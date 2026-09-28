using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Physician-desktop encounters (EMR-02..07): start from a visit or admission (optionally from a speciality template),
/// edit the SOAP note, sign it, and amend signed notes with an attested reason. Internal referrals notify the
/// referred doctor in-app when the note is signed.
/// </summary>
public sealed class EncounterService(
    HospitalDbContext db,
    ClinicalAccess access,
    ICurrentUser currentUser,
    INotificationService notifications,
    TimeProvider time)
{
    public async Task<Result<EncounterDetail>> StartAsync(StartEncounterRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var allowed = await access.EnsureCanRecordAsync(request.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        if ((request.PractitionerId ?? currentUser.PractitionerId) is not { } practitionerId)
        {
            return Error.Rule("practitioner.required", "Your user account is not linked to a doctor. Choose the treating practitioner.");
        }

        var type = request.Type ?? EncounterType.OutPatient;
        var departmentId = request.DepartmentId;
        string? complaint = request.ChiefComplaint;

        if (request.VisitId is { } visitId)
        {
            var visit = await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == visitId, ct);
            if (visit is null || visit.PatientId != request.PatientId)
            {
                return Error.Field("visitId", "This visit does not belong to the patient.");
            }

            if (visit.Status == VisitStatus.Cancelled)
            {
                return Error.Rule("visit.cancelled", "This visit was cancelled.");
            }

            var open = await db.Set<Encounter>()
                .Where(e => e.VisitId == visitId && e.PractitionerId == practitionerId && e.Status == EncounterStatus.InProgress)
                .Select(e => (Guid?)e.Id)
                .FirstOrDefaultAsync(ct);
            if (open is not null)
            {
                return Error.Conflict("encounter.already_open", "An unsigned encounter already exists for this visit. Continue it instead.", new { encounterId = open });
            }

            type = request.Type ?? MapVisitType(visit.Type);
            departmentId ??= visit.DepartmentId;
            complaint ??= visit.ChiefComplaint;
            visit.Start();
        }

        var encounter = new Encounter
        {
            FacilityId = facilityId,
            PatientId = request.PatientId,
            VisitId = request.VisitId,
            AdmissionId = request.AdmissionId,
            PractitionerId = practitionerId,
            DepartmentId = departmentId ?? await db.Set<Practitioner>().Where(p => p.Id == practitionerId).Select(p => p.DepartmentId).FirstOrDefaultAsync(ct),
            Type = type,
            StartedAt = time.GetUtcNow(),
            TemplateId = request.TemplateId,
        };

        var content = new EncounterContent(complaint, null, null, null, null, null, null, null, null, null, null);
        if (request.TemplateId is { } templateId)
        {
            var template = await db.Set<ClinicalTemplate>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId && t.IsActive, ct);
            if (template is null)
            {
                return Error.Field("templateId", "Choose an active template.");
            }

            content = content with
            {
                ChiefComplaint = complaint ?? template.ChiefComplaint,
                HistoryOfPresentIllness = template.HistoryOfPresentIllness,
                Examination = template.Examination,
                Assessment = template.Assessment,
                Plan = template.Plan,
                FollowUpAdvice = template.FollowUpAdvice,
                FollowUpDate = template.FollowUpDays is > 0 ? time.Today().AddDays(template.FollowUpDays.Value) : null,
            };
        }

        encounter.Update(content);
        db.Add(encounter);
        await db.SaveChangesAsync(ct);
        return await DetailAsync(encounter, ct);
    }

    public Task<Result<EncounterDetail>> UpdateAsync(Guid id, UpdateEncounterRequest request, CancellationToken ct) =>
        ChangeAsync(id, request.Version, e => Task.FromResult(e.Update(request.ToContent())), ct);

    public Task<Result<EncounterDetail>> SignAsync(Guid id, uint version, CancellationToken ct) =>
        ChangeAsync(id, version, async e =>
        {
            var result = e.Sign(currentUser.UserId, currentUser.PractitionerId, time.GetUtcNow());
            if (result.IsSuccess)
            {
                await NotifyReferralAsync(e, ct);
            }

            return result;
        }, ct);

    public Task<Result<EncounterDetail>> AmendAsync(Guid id, AmendEncounterRequest request, CancellationToken ct) =>
        ChangeAsync(id, request.Version, e => Task.FromResult(e.Amend(request.ToContent(), request.Reason, currentUser.UserId, time.GetUtcNow())), ct);

    public async Task<Result<EncounterDetail>> DetailAsync(Encounter encounter, CancellationToken ct)
    {
        var practitioners = await PrintQueries.PractitionerNamesAsync(db, [encounter.PractitionerId], ct);
        var departments = await PrintQueries.DepartmentNamesAsync(db, [encounter.DepartmentId], ct);
        return EncounterMapping.ToDetail(encounter, practitioners.GetValueOrDefault(encounter.PractitionerId),
            encounter.DepartmentId is { } d ? departments.GetValueOrDefault(d) : null);
    }

    private async Task<Result<EncounterDetail>> ChangeAsync(Guid id, uint version, Func<Encounter, Task<Result>> change, CancellationToken ct)
    {
        var encounter = await db.Set<Encounter>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Include(e => e.Amendments)
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (encounter is null)
        {
            return Error.NotFound(nameof(Encounter), id);
        }

        var allowed = await access.EnsureCanOpenAsync(encounter.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        db.SetExpectedVersion(encounter, version);
        var result = await change(encounter);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DetailAsync(encounter, ct);
    }

    /// <summary>In-app notice to the referred doctor (users linked to that practitioner) once the note is signed.</summary>
    private async Task NotifyReferralAsync(Encounter encounter, CancellationToken ct)
    {
        if (encounter.ReferralPractitionerId is not { } referredTo)
        {
            return;
        }

        var users = await db.Set<User>().Where(u => u.PractitionerId == referredTo && u.IsActive).Select(u => u.Id).ToListAsync(ct);
        var uhid = await db.Set<Patient>().Where(p => p.Id == encounter.PatientId).Select(p => p.Uhid).FirstOrDefaultAsync(ct);
        await notifications.NotifyUsersAsync(users, "New referral", $"Patient {uhid} has been referred to you: {encounter.ReferralReason}",
            $"/emr/patients/{encounter.PatientId}", "clinical", "info", ct);
    }

    private static EncounterType MapVisitType(VisitType type) => type switch
    {
        VisitType.InPatient => EncounterType.InPatient,
        VisitType.Emergency => EncounterType.Emergency,
        VisitType.Telehealth => EncounterType.Telehealth,
        VisitType.HealthCheckup => EncounterType.HealthCheckup,
        _ => EncounterType.OutPatient,
    };
}
