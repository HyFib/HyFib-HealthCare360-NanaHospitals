using HyFib.HealthCare360.Modules.AI.Services.Privacy;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.AI.Services;

/// <summary>
/// Patient look-ups for the assistant: resolve a spoken UHID to the patient id, and gather every name and identifier on
/// file for the patient in context so the redactor can remove them before text is sent to an AI provider.
/// </summary>
internal sealed class PatientDirectory(HospitalDbContext db) : IPatientDirectory
{
    public async Task<Guid?> FindByUhidAsync(string uhid, CancellationToken cancellationToken)
    {
        var normalized = new string(uhid.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length is < 4 or > 20)
        {
            return null;
        }

        return await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Uhid == normalized && p.IsActive && p.MergedIntoPatientId == null)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<RedactionContext> RedactionContextAsync(Guid? patientId, CancellationToken cancellationToken)
    {
        if (patientId is not { } id)
        {
            return RedactionContext.Empty;
        }

        var patient = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new
            {
                p.FullName, p.FirstName, p.MiddleName, p.LastName, p.GuardianName, p.EmergencyContactName,
                p.Uhid, p.Phone, p.AlternatePhone, p.EmergencyContactPhone, p.Email, p.AbhaAddress,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (patient is null)
        {
            return RedactionContext.Empty;
        }

        string?[] names = [patient.FullName, patient.FirstName, patient.MiddleName, patient.LastName, patient.GuardianName, patient.EmergencyContactName];
        string?[] phones = [patient.Phone, patient.AlternatePhone, patient.EmergencyContactPhone];
        var identifiers = new List<string?> { patient.Uhid, patient.Email, patient.AbhaAddress };
        foreach (var phone in phones.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            // Stored as E.164 (+919876543210) but usually spoken or typed as the national ten digits.
            identifiers.Add(phone);
            identifiers.Add(phone!.Length > 10 ? phone[^10..] : phone);
        }

        return new RedactionContext(
            names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList(),
            identifiers.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i!).ToList());
    }
}
