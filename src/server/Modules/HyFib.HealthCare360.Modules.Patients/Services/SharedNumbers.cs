using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// A record in a protected category (HIV, mental health, MTP, sexual violence: <see cref="SensitiveCategory"/>) is reached
/// through a mobile number only when the number is the patient's alone. Families in India often share one number, and
/// whoever holds it signs in to the patient portal and reads what is sent to it. So the record is hidden from a number
/// that another patient also has, and from an account that reaches it through a family link (someone else's number):
/// left out of the portal, and not sent a report, a prescription or a bill by SMS or WhatsApp. The hospital gives these
/// in person.
/// </summary>
public static class SharedNumbers
{
    /// <summary>
    /// Whether a patient is shown to a portal account: always without a protected category; with one, only on the
    /// patient's own number and only when no other patient has that number.
    /// </summary>
    /// <param name="ownNumber">The account signed in with the patient's own number (not through a family link).</param>
    /// <param name="numberShared">Another active patient is registered with the account's number.</param>
    public static bool ShownToAccount(bool ownNumber, bool numberShared, SensitiveCategory categories) =>
        categories == SensitiveCategory.None || (ownNumber && !numberShared);

    /// <summary>
    /// Whether the patient's record is kept from messages to their mobile number: it is in a protected category and
    /// another active patient (not merged away) is registered with the same number.
    /// </summary>
    public static async Task<bool> HiddenFromNumberAsync(DbContext db, Guid patientId, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Id == patientId)
            .Select(p => new { p.Phone, p.SensitiveCategories })
            .FirstOrDefaultAsync(ct);
        if (patient?.Phone is not { } phone || patient.SensitiveCategories == SensitiveCategory.None)
        {
            return false;
        }

        return await db.Set<Patient>().AnyAsync(p => p.Id != patientId && p.Phone == phone && p.IsActive && p.MergedIntoPatientId == null, ct);
    }
}
