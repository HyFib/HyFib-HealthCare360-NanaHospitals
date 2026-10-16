using HyFib.HealthCare360.Modules.Billing.Documents;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Sends the patient their bill on WhatsApp (INT-WA): template <c>bill.shared</c>, approved with a document header that
/// carries the A4 bill's PDF (<see cref="BillDocumentSource"/>). It goes by itself when a payment settles the bill (the
/// counter, a POS terminal or a payment link), alongside the "bill.receipt" SMS, unless setting
/// <see cref="SendOnPaymentSetting"/> is off; and whenever the billing desk sends it from the bill.
/// <para>
/// A bill is a service message about the patient's own care, so, like a report, it needs no WhatsApp opt-in: it goes
/// to the number registered for the patient unless that number replied STOP (the delivery handler checks it when the
/// message goes out, see <see cref="WhatsAppOptOutPolicy"/>). Deceased patients' numbers are not messaged.
/// </para>
/// </summary>
public sealed class BillSharing(HospitalDbContext db, INotificationService notifications, WhatsAppConsent whatsApp, ISettingsService settings)
{
    public const string Template = "bill.shared";

    /// <summary>Send the bill on WhatsApp when a payment settles it (bool, default true).</summary>
    public const string SendOnPaymentSetting = "billing.bills.sendOnWhatsApp";

    /// <summary>An issued bill: a draft is only an estimate, and a cancelled bill is not one the patient owes.</summary>
    public static bool CanShare(BillStatus status) => status is BillStatus.Finalized or BillStatus.PartiallyPaid or BillStatus.Paid;

    /// <summary>Queues the bill when a payment has just settled it. Adds to the caller's unit of work.</summary>
    internal async Task QueueOnPaymentAsync(Bill bill, CancellationToken ct)
    {
        if (bill.Status != BillStatus.Paid || bill.BillNumber is null
            || !(await settings.GetAsync<bool?>(SendOnPaymentSetting, bill.FacilityId, ct) ?? true))
        {
            return;
        }

        if (await RecipientAsync(bill.PatientId, ct) is { } patient)
        {
            Queue(bill, patient);
        }
    }

    /// <summary>The billing desk sends the bill on WhatsApp. Saves.</summary>
    public async Task<Result> SendAsync(Guid billId, CancellationToken ct)
    {
        var bill = await db.Set<Bill>().FirstOrDefaultAsync(b => b.Id == billId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), billId);
        }

        if (!CanShare(bill.Status) || bill.BillNumber is null)
        {
            return Error.Rule("billing.bill_not_shareable", "Only a finalised bill can be sent. A draft is an estimate, and a cancelled bill is not owed.");
        }

        var patient = await RecipientAsync(bill.PatientId, ct);
        if (patient is null)
        {
            return Error.Rule("billing.no_mobile", "The patient has no mobile number to send the bill to. Add one to their registration first.");
        }

        if ((await whatsApp.StatusAsync(patient.Phone, ct)).OptedOut)
        {
            return Error.Rule("billing.whatsapp_opted_out",
                "This number replied STOP on WhatsApp, so the bill can't be sent there. Print it, or the patient can download it from the patient portal.");
        }

        Queue(bill, patient);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private void Queue(Bill bill, Recipient patient) =>
        notifications.Queue(
            Template,
            new NotificationRecipient(patient.Phone, null, bill.PatientId, patient.FullName),
            new Dictionary<string, string>
            {
                ["name"] = patient.FullName,
                ["billNumber"] = bill.BillNumber!,
                ["amount"] = Money.FormatInr(bill.PatientAmount),
            },
            [NotificationChannel.WhatsApp],
            TimeSpan.FromDays(2),
            bill.FacilityId,
            new NotificationDocument(BillDocumentSource.DocumentKind, bill.Id));

    private async Task<Recipient?> RecipientAsync(Guid patientId, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Id == patientId)
            .Select(p => new { p.Phone, p.FullName, p.IsDeceased })
            .FirstOrDefaultAsync(ct);
        return patient is { Phone: { } phone, IsDeceased: false } ? new Recipient(phone, patient.FullName) : null;
    }

    private sealed record Recipient(string Phone, string FullName);
}
