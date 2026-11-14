using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// A patient's documents go to their mobile number only once the number is known to reach them (ASVS V6.6.1,
/// <see cref="Patient.IsPhoneVerified"/>). Until then a digit typed wrong at the desk, or a disconnected number the telco
/// gave to someone new, would send the patient's report, prescription, bill or discharge summary to a stranger. So a
/// message to an unverified number still goes, as the notice alone: the patient collects the document, opens it in the
/// portal once the record is confirmed there, or has the number verified at the desk first. E-mail is not affected: the
/// address is the patient's own word, and a message to it carries its document as before.
/// <para>
/// Every sender that names a document for a patient's mobile number asks here first (Diagnostics' report delivery,
/// Clinical's prescription notice, Inpatient's discharge, Billing's bill on WhatsApp). The rule on records in a protected
/// category on a shared number comes before it and is unchanged (<see cref="SharedNumbers"/>).
/// </para>
/// </summary>
public static class VerifiedNumbers
{
    /// <summary><see cref="Patient.IsPhoneVerified"/>, for a query.</summary>
    public static Expression<Func<Patient, bool>> Verified { get; } =
        p => p.Phone != null && p.PhoneVerifiedAt != null && p.PhoneVerifiedNumber == p.Phone;

    /// <summary>Whether a message to the patient's mobile number may carry a document: the number is verified now.</summary>
    public static Task<bool> CarriesDocumentsAsync(DbContext db, Guid patientId, CancellationToken ct) =>
        db.Set<Patient>().AsNoTracking().Where(p => p.Id == patientId).Where(Verified).AnyAsync(ct);

    /// <summary>The document a message to the patient's mobile number carries: <paramref name="document"/> to a verified number, none otherwise.</summary>
    public static NotificationDocument? ForNumber(NotificationDocument? document, bool numberVerified) => numberVerified ? document : null;
}
