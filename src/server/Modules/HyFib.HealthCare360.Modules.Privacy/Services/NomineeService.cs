using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Privacy.Domain;

namespace HyFib.HealthCare360.Modules.Privacy.Services;

public sealed record NomineeDto(string Name, string Relationship, string? Phone, string? Email, DataRequestChannel RecordedVia, DateTimeOffset UpdatedAt, uint Version);

/// <param name="Version">The version of the nominee as loaded (REL-04); none when the patient had none.</param>
public sealed record SaveNomineeRequest(string Name, string Relationship, string? Phone, string? Email, uint? Version = null);

public sealed class SaveNomineeRequestValidator : AbstractValidator<SaveNomineeRequest>
{
    public SaveNomineeRequestValidator() => NomineeRules.Apply(this, x => x.Name, x => x.Relationship, x => x.Phone, x => x.Email);
}

/// <summary>The nominee rules, shared by the staff and the portal requests.</summary>
public static class NomineeRules
{
    public static void Apply<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, string>> name,
        System.Linq.Expressions.Expression<Func<T, string>> relationship,
        System.Linq.Expressions.Expression<Func<T, string?>> phone,
        System.Linq.Expressions.Expression<Func<T, string?>> email)
    {
        var readPhone = phone.Compile();
        var readEmail = email.Compile();
        validator.RuleFor(name).NotEmpty().WithMessage("Enter the nominee's name.").SafeText(200);
        validator.RuleFor(relationship).NotEmpty().WithMessage("Say how the nominee is related to the patient.").SafeText(64);
        validator.RuleFor(phone).IndianMobile();
        validator.RuleFor(email).MaximumLength(200).EmailAddress().When(x => !string.IsNullOrWhiteSpace(readEmail(x)));
        validator.RuleFor(phone).Must((x, _) => !string.IsNullOrWhiteSpace(readPhone(x)) || !string.IsNullOrWhiteSpace(readEmail(x)))
            .WithMessage("Enter the nominee's mobile number or e-mail address, so the hospital can reach them.");
    }
}

/// <summary>
/// The patient's nominee (DPDP Act s14): one per patient, named in the portal or recorded at the desk. Replacing or
/// removing one is recorded in the audit trail like any change to the record.
/// </summary>
public sealed class NomineeService(HospitalDbContext db, IAuditLogger audit)
{
    public async Task<NomineeDto?> GetAsync(Guid patientId, CancellationToken ct) =>
        await db.Set<Nominee>().AsNoTracking()
            .Where(n => n.PatientId == patientId)
            .Select(n => new NomineeDto(n.Name, n.Relationship, n.Phone, n.Email, n.RecordedVia, n.UpdatedAt ?? n.CreatedAt, n.Version))
            .FirstOrDefaultAsync(ct);

    public async Task<Result> SaveAsync(Guid patientId, SaveNomineeRequest request, DataRequestChannel via, CancellationToken ct)
    {
        if (!await db.Set<Patient>().AnyAsync(p => p.Id == patientId && p.MergedIntoPatientId == null, ct))
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        var nominee = await db.Set<Nominee>().FirstOrDefaultAsync(n => n.PatientId == patientId, ct);
        if (nominee is null)
        {
            nominee = new Nominee { PatientId = patientId };
            db.Add(nominee);
        }

        db.SetExpectedVersion(nominee, request.Version);

        nominee.Name = request.Name.Trim();
        nominee.Relationship = request.Relationship.Trim();
        nominee.Phone = Phone.Normalize(request.Phone);
        nominee.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        nominee.RecordedVia = via;
        audit.Add(new AuditEntry(AuditCategories.Data, "privacy.nominee.saved", "Nominee named") { PatientId = patientId, EntityType = nameof(Nominee) });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> RemoveAsync(Guid patientId, CancellationToken ct)
    {
        var nominee = await db.Set<Nominee>().FirstOrDefaultAsync(n => n.PatientId == patientId, ct);
        if (nominee is null)
        {
            return Error.NotFound(nameof(Nominee), patientId);
        }

        db.Remove(nominee);
        audit.Add(new AuditEntry(AuditCategories.Data, "privacy.nominee.removed", "Nominee removed") { PatientId = patientId, EntityType = nameof(Nominee) });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
