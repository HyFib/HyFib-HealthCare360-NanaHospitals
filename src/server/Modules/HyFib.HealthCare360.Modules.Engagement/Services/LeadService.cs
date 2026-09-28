using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features.Leads;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// Lead management (CRM-01): capture with duplicate detection, activity logging, status changes and conversion into
/// a registered patient (through the Patients module's registration, with its own duplicate check) and/or an
/// appointment booked in Scheduling.
/// </summary>
public sealed class LeadService(
    HospitalDbContext db,
    INumberSeries numbers,
    ICurrentUser currentUser,
    PatientRegistrationService registration,
    TimeProvider time)
{
    public const string LeadSeries = "crm.lead";
    public const string LeadFormat = "{FAC}LD{YY}{SEQ:5}";

    public async Task<Result<LeadCreated>> CreateAsync(SaveLeadRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var phone = Phone.Normalize(request.Phone);
        if (phone is not null && !request.ConfirmedNotDuplicate)
        {
            var open = await db.Set<Lead>()
                .Where(l => l.Phone == phone && (l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted || l.Status == LeadStatus.Qualified))
                .Select(l => new { l.Id, l.LeadNumber, l.Name })
                .FirstOrDefaultAsync(ct);
            if (open is not null)
            {
                return Error.Conflict("lead.duplicate", $"An open lead ({open.LeadNumber}, {open.Name}) already exists for this number.", new { leadId = open.Id });
            }
        }

        var problem = await ValidateReferencesAsync(request, ct);
        if (problem is not null)
        {
            return problem;
        }

        var lead = new Lead { FacilityId = facilityId, Status = LeadStatus.New, OwnerUserId = request.OwnerUserId ?? currentUser.UserId };
        Apply(lead, request, phone);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        lead.LeadNumber = await numbers.NextAsync(LeadSeries, facilityId, LeadFormat, NumberResetPolicy.Yearly, ct);
        db.Add(lead);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var existingPatient = phone is null
            ? null
            : await db.Set<Patient>().Where(p => p.Phone == phone && p.IsActive && p.MergedIntoPatientId == null)
                .OrderBy(p => p.RegisteredAt).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        return new LeadCreated(lead.Id, lead.LeadNumber, existingPatient);
    }

    public async Task<Result> UpdateAsync(Guid id, SaveLeadRequest request, CancellationToken ct)
    {
        var lead = await db.Set<Lead>().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null)
        {
            return Error.NotFound(nameof(Lead), id);
        }

        var problem = await ValidateReferencesAsync(request, ct);
        if (problem is not null)
        {
            return problem;
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(lead, version);
        }

        Apply(lead, request, Phone.Normalize(request.Phone));
        lead.OwnerUserId = request.OwnerUserId ?? lead.OwnerUserId;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<Guid>> AddActivityAsync(Guid id, AddLeadActivityRequest request, CancellationToken ct)
    {
        var lead = await db.Set<Lead>().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null)
        {
            return Error.NotFound(nameof(Lead), id);
        }

        var now = time.GetUtcNow();
        var occurredAt = request.OccurredAt is { } at && at <= now ? at : now;
        var activity = new LeadActivity
        {
            LeadId = id,
            Type = request.Type,
            OccurredAt = occurredAt,
            Outcome = Clean(request.Outcome),
            Notes = Clean(request.Notes),
            PerformedByUserId = currentUser.UserId,
        };
        db.Add(activity);
        if (request.Type != LeadActivityType.Note)
        {
            lead.RecordContact(occurredAt, request.NextFollowUpAt);
        }
        else if (request.NextFollowUpAt is not null && lead.IsOpen)
        {
            lead.NextFollowUpAt = request.NextFollowUpAt;
        }

        await db.SaveChangesAsync(ct);
        return activity.Id;
    }

    public async Task<Result> ChangeStatusAsync(Guid id, ChangeLeadStatusRequest request, CancellationToken ct)
    {
        var lead = await db.Set<Lead>().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null)
        {
            return Error.NotFound(nameof(Lead), id);
        }

        var result = lead.MoveTo(request.Status, request.LostReason);
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    public async Task<Result<LeadConversion>> ConvertAsync(Guid id, ConvertLeadRequest request, CancellationToken ct)
    {
        var lead = await db.Set<Lead>().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null)
        {
            return Error.NotFound(nameof(Lead), id);
        }

        if (lead.Status == LeadStatus.Converted)
        {
            return Error.Rule("lead.converted", "This lead has already been converted.");
        }

        Guid? patientId = request.PatientId;
        string? uhid = null;
        if (patientId is { } existing)
        {
            uhid = await db.Set<Patient>().Where(p => p.Id == existing).Select(p => p.Uhid).FirstOrDefaultAsync(ct);
            if (uhid is null)
            {
                return Error.Field("patientId", "Choose a registered patient.");
            }
        }
        else if (request.RegisterPatient is { } details)
        {
            var registered = await RegisterAsync(lead, details, ct);
            if (registered.IsFailure)
            {
                return registered.Error!;
            }

            patientId = registered.Value.Id;
            uhid = registered.Value.Uhid;
        }

        var result = lead.Convert(patientId, request.AppointmentId, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return new LeadConversion(lead.Id, patientId, uhid, request.AppointmentId);
    }

    /// <summary>Registers the lead through the Patients module (which runs its own duplicate detection: 409 with candidates).</summary>
    private Task<Result<PatientCreated>> RegisterAsync(Lead lead, NewPatientDetails details, CancellationToken ct)
    {
        var names = lead.Name.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return registration.RegisterAsync(new RegisterPatientRequest(
            Title: null,
            FirstName: names[0],
            MiddleName: null,
            LastName: names.Length > 1 ? names[1] : null,
            Gender: details.Gender,
            DateOfBirth: details.DateOfBirth,
            AgeYears: details.DateOfBirth is null ? details.AgeYears ?? lead.AgeYears : null,
            AgeMonths: null,
            Phone: lead.Phone,
            AlternatePhone: null,
            Email: lead.Email,
            Address: string.IsNullOrWhiteSpace(lead.City) ? null : new Address { City = lead.City },
            BloodGroup: null,
            MaritalStatus: null,
            GuardianName: null,
            GuardianRelation: null,
            EmergencyContactName: null,
            EmergencyContactPhone: null,
            EmergencyContactRelation: null,
            Occupation: null,
            Religion: null,
            Nationality: null,
            PreferredLanguage: null,
            Category: null,
            AbhaNumber: null,
            AbhaAddress: null,
            AadhaarNumber: null,
            PhotoFileId: null,
            IsVip: false,
            Notes: $"Registered from CRM lead {lead.LeadNumber}.",
            ConfirmedNotDuplicate: details.ConfirmedNotDuplicate), ct);
    }

    private async Task<Error?> ValidateReferencesAsync(SaveLeadRequest request, CancellationToken ct)
    {
        if (request.InterestDepartmentId is { } departmentId && !await db.Set<Department>().AnyAsync(d => d.Id == departmentId && d.IsActive, ct))
        {
            return Error.Field("interestDepartmentId", "Choose an active department.");
        }

        if (request.CampaignId is { } campaignId && !await db.Set<Campaign>().AnyAsync(c => c.Id == campaignId, ct))
        {
            return Error.Field("campaignId", "Choose a campaign of this facility.");
        }

        if (request.ReferringDoctorId is { } doctorId && !await db.Set<ReferringDoctor>().AnyAsync(d => d.Id == doctorId, ct))
        {
            return Error.Field("referringDoctorId", "Choose a referring doctor.");
        }

        if (request.OwnerUserId is { } ownerId && !await db.Set<User>().AnyAsync(u => u.Id == ownerId && u.IsActive && u.Type == UserType.Staff, ct))
        {
            return Error.Field("ownerUserId", "Choose an active staff member.");
        }

        return null;
    }

    private static void Apply(Lead lead, SaveLeadRequest request, string? phone)
    {
        lead.Name = request.Name.Trim();
        lead.Phone = phone;
        lead.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim().ToLowerInvariant();
        lead.Gender = request.Gender;
        lead.AgeYears = request.AgeYears;
        lead.City = Clean(request.City);
        lead.Source = request.Source;
        lead.SourceDetail = Clean(request.SourceDetail);
        lead.CampaignId = request.CampaignId;
        lead.ReferringDoctorId = request.ReferringDoctorId;
        lead.InterestDepartmentId = request.InterestDepartmentId;
        lead.InterestPackage = Clean(request.InterestPackage);
        lead.Notes = Clean(request.Notes);
        if (lead.IsOpen)
        {
            lead.NextFollowUpAt = request.NextFollowUpAt;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
