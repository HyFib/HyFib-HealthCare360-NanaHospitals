using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Services;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Pharmacy.Seed;

/// <summary>
/// Idempotent pharmacy reference data (runs after SupplyChain, Order 160): the catalogue services IP issues are
/// billed under (PHARM-MED/CONS/IMPL, priced per line at MRP), the refill notification templates, and — only with
/// SeedDemoData — two e-prescriptions waiting in the OP queue for the demo patients.
/// </summary>
internal sealed class PharmacySeeder(IOptions<PlatformOptions> options, TimeProvider time) : ISeeder
{
    public int Order => 160;

    private static readonly (string Code, string Name)[] Services =
    [
        (InpatientPharmacyService.MedicinesServiceCode, "Pharmacy — medicines (in-patient)"),
        (InpatientPharmacyService.ConsumablesServiceCode, "Pharmacy — consumables and surgicals (in-patient)"),
        (InpatientPharmacyService.ImplantsServiceCode, "Pharmacy — implants (in-patient)"),
    ];

    private static readonly (string Key, NotificationChannel Channel, string Body, string Description)[] Templates =
    [
        (RefillService.ReminderTemplate, NotificationChannel.Sms,
            "Dear {{name}}, your medicines ({{medicines}}) on prescription {{reference}} run out on {{date}}. Request a refill on the patient portal or visit the pharmacy. - {{hospital}}",
            "Chronic prescription refill reminder"),
        (RefillService.ReminderTemplate, NotificationChannel.WhatsApp,
            "Dear {{name}}, your medicines ({{medicines}}) on prescription {{reference}} run out on {{date}}. You can request a refill on the patient portal.",
            "Chronic prescription refill reminder (WhatsApp)"),
        (RefillService.ReadyTemplate, NotificationChannel.Sms,
            "Dear {{name}}, your refill for prescription {{reference}} is being prepared. Please collect it from the pharmacy on {{date}}. - {{hospital}}",
            "Refill request approved"),
        (RefillService.ReadyTemplate, NotificationChannel.WhatsApp,
            "Dear {{name}}, your refill for prescription {{reference}} is being prepared. Please collect it from the pharmacy on {{date}}.",
            "Refill request approved (WhatsApp)"),
    ];

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        await SeedServicesAsync(db, cancellationToken);
        await SeedTemplatesAsync(db, cancellationToken);
        if (options.Value.SeedDemoData)
        {
            await SeedDemoQueueAsync(db, cancellationToken);
        }
    }

    private static async Task SeedServicesAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = await db.Set<ServiceItem>().Select(s => s.Code).ToListAsync(ct);
        var pharmacy = await db.Set<Department>().Where(d => d.Code == "PHARM").OrderBy(d => d.CreatedAt).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        foreach (var (code, name) in Services.Where(s => !existing.Contains(s.Code)))
        {
            db.Add(new ServiceItem
            {
                Code = code,
                Name = name,
                Category = ServiceCategory.Consumable,
                DepartmentId = pharmacy,
                BasePrice = 0,
                IsPriceEditable = true,
                IsDiscountAllowed = true,
                Description = "Charged per line at the batch MRP by the IP pharmacy (goods: GST is inside MRP; HSN on the pharmacy record).",
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedTemplatesAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = await db.Set<NotificationTemplate>()
            .Where(t => t.FacilityId == null && t.Language == "en")
            .Select(t => new { t.Key, t.Channel })
            .ToListAsync(ct);
        foreach (var template in Templates.Where(t => !existing.Any(e => e.Key == t.Key && e.Channel == t.Channel)))
        {
            db.Add(new NotificationTemplate
            {
                Key = template.Key,
                Channel = template.Channel,
                Body = template.Body,
                Description = template.Description,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedDemoQueueAsync(HospitalDbContext db, CancellationToken ct)
    {
        if (await db.Set<PharmacyQueueEntry>().AnyAsync(ct))
        {
            return;
        }

        var facility = await db.Set<Facility>().OrderBy(f => f.CreatedAt).FirstOrDefaultAsync(ct);
        var patients = await db.Set<Patient>().OrderBy(p => p.RegisteredAt).Take(2).Select(p => p.Id).ToListAsync(ct);
        var doctor = await db.Set<Practitioner>().OrderBy(p => p.Code).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (facility is null || patients.Count < 2 || doctor is null)
        {
            return;
        }

        var items = await db.Set<Item>().ToDictionaryAsync(i => i.Code, ct);
        var now = time.GetUtcNow();
        var demo = new[]
        {
            (Patient: patients[0], Number: "DEMO-RX-0001", Minutes: 25, Lines: new[]
            {
                ("DRG-PCM650", "1 tablet", "Three times a day", 5, 15m),
                ("DRG-AZI500", "1 tablet", "Once a day", 3, 3m),
                ("DRG-PANTO40", "1 tablet", "Before breakfast", 5, 5m),
            }),
            (Patient: patients[1], Number: "DEMO-RX-0002", Minutes: 10, Lines: new[]
            {
                ("DRG-MET500", "1 tablet", "Twice a day after food", 30, 60m),
                ("DRG-TELMI40", "1 tablet", "Once a day", 30, 30m),
                ("DRG-ATOR10", "1 tablet", "At bedtime", 30, 30m),
            }),
        };

        foreach (var rx in demo)
        {
            var lines = rx.Lines.Where(l => items.ContainsKey(l.Item1)).Select(l =>
            {
                var item = items[l.Item1];
                return new PrescriptionLine(Guid.CreateVersion7(), item.Id, item.Name, item.GenericName, item.Strength, item.DosageForm, "Oral",
                    l.Item2, l.Item3, l.Item4, l.Item5, null, item.RequiresRegister);
            }).ToList();
            if (lines.Count == 0)
            {
                continue;
            }

            var signed = new PrescriptionSigned(Guid.CreateVersion7(), rx.Number, rx.Patient, null, null, facility.Id, doctor.Value, false, lines,
                now.AddMinutes(-rx.Minutes));
            db.Add(PharmacyQueueEntry.FromPrescription(signed, now.AddMinutes(-rx.Minutes)));
        }

        await db.SaveChangesAsync(ct);
    }
}
