using System.Globalization;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupplyChain.Seed;

/// <summary>
/// Demonstration data (SeedDemoData only): vendors, opening stock in every store — including near-expiry and
/// expired batches so the expiry reports and write-off flow have something to show — and a small biomedical
/// fleet with PM plans, contracts, calibration and one open breakdown. Each block runs only on an empty table.
/// </summary>
internal static class DemoInventorySeed
{
    private static readonly (string Code, string Name, string Gstin, string DrugLicence, string City, string State, string Phone, int Terms, decimal Rating)[] Vendors =
    [
        ("VEN-SBPD", "Sri Balaji Pharma Distributors", "33AABCS1234F1Z5", "TN-CHN-20B-004512 / 21B-004512", "Chennai", "Tamil Nadu", "+914424000101", 30, 4.5m),
        ("VEN-DPD", "Deccan Pharma Distributors", "36AAECD4321M1Z8", "TS-HYD-20B-118734 / 21B-118734", "Hyderabad", "Telangana", "+914027000202", 45, 4.0m),
        ("VEN-CSUR", "Coromandel Surgicals", "33AAFCC5678K1Z2", "TN-CHN-20B-009921 / 21B-009921", "Chennai", "Tamil Nadu", "+914424000303", 30, 4.2m),
        ("VEN-OIM", "Orthocare Implants & Medisystems", "27AABCO2468Q1Z6", "MH-MUM-20B-220415 / 21B-220415", "Mumbai", "Maharashtra", "+912226000404", 60, 4.3m),
        ("VEN-SBS", "Southern Biomedical Services", "33AAGCS8765P1Z3", "", "Chennai", "Tamil Nadu", "+914424000505", 30, 4.6m),
    ];

    public static async Task SeedAsync(HospitalDbContext db, Facility facility, StockService stock, INumberSeries numbers, TimeProvider time, CancellationToken ct)
    {
        await SeedVendorsAsync(db, time, ct);
        await SeedStockAsync(db, facility, stock, time, ct);
        await SeedAssetsAsync(db, facility, numbers, time, ct);
    }

    private static async Task SeedVendorsAsync(HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var existing = await db.Set<Vendor>().Select(v => v.Code).ToListAsync(ct);
        var today = HospitalClock.Today(time);
        foreach (var v in Vendors.Where(v => !existing.Contains(v.Code)))
        {
            db.Add(new Vendor
            {
                Code = v.Code,
                Name = v.Name,
                Gstin = v.Gstin,
                Pan = v.Gstin.Substring(2, 10),
                DrugLicenseNumber = string.IsNullOrEmpty(v.DrugLicence) ? null : v.DrugLicence,
                DrugLicenseValidUntil = string.IsNullOrEmpty(v.DrugLicence) ? null : today.AddYears(3),
                ContactPerson = "Sales Desk",
                Phone = v.Phone,
                Address = new Address { City = v.City, State = v.State, Country = "India" },
                PaymentTermsDays = v.Terms,
                Rating = v.Rating,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedStockAsync(HospitalDbContext db, Facility facility, StockService stock, TimeProvider time, CancellationToken ct)
    {
        if (await db.Set<StockBatch>().AnyAsync(ct))
        {
            return;
        }

        var stores = await db.Set<Store>().Where(s => s.FacilityId == facility.Id).ToDictionaryAsync(s => s.Code, s => s.Id, ct);
        var items = await db.Set<Item>().ToDictionaryAsync(i => i.Code, ct);
        var vendors = await db.Set<Vendor>().ToDictionaryAsync(v => v.Code, v => v.Id, ct);
        if (!stores.ContainsKey("CS"))
        {
            return;
        }

        var today = HospitalClock.Today(time);
        var reference = new StockReference("supplychain.opening", null, "OPENING", "Demo opening stock");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var index = 0;
        foreach (var seed in ItemCatalogSeed.Items)
        {
            index++;
            if (!items.TryGetValue(seed.Code, out var item))
            {
                continue;
            }

            var vendor = seed.Type switch
            {
                ItemType.Implant => "VEN-OIM",
                ItemType.Drug => index % 3 == 0 ? "VEN-DPD" : "VEN-SBPD",
                _ => "VEN-CSUR",
            };
            var expiry = item.TracksExpiry ? EndOfMonth(today.AddMonths(9 + (index * 5 % 18))) : (DateOnly?)null;
            var batchNumber = string.Create(CultureInfo.InvariantCulture, $"{(char)('A' + (index % 26))}{today.Year % 100:00}{index * 37 % 1000:000}");

            async Task ReceiveAsync(string storeCode, decimal packs, DateOnly? batchExpiry, string batch)
            {
                if (!stores.TryGetValue(storeCode, out var storeId) || packs <= 0)
                {
                    return;
                }

                var received = await stock.ReceiveAsync(
                    new StockReceipt(storeId, item.Id, batch, batchExpiry, batchExpiry?.AddYears(-2), packs * seed.Factor, seed.PackMrp / seed.Factor,
                        seed.PackRate / seed.Factor, seed.Gst, vendors.TryGetValue(vendor, out var vendorId) ? vendorId : null, null, today.AddDays(-30)),
                    StockMovementType.Opening, reference, null, ct);
                if (received.IsFailure)
                {
                    throw new InvalidOperationException($"Demo stock for {seed.Code} failed: {received.Error!.Message}");
                }
            }

            await ReceiveAsync("CS", seed.Type == ItemType.Implant ? 2 : 20, expiry, batchNumber);
            if (seed.Type is ItemType.Drug or ItemType.Consumable)
            {
                await ReceiveAsync("OPPH", seed.Controlled ? 2 : 5, expiry, batchNumber);
                await ReceiveAsync("IPPH", seed.Controlled ? 2 : 4, expiry, batchNumber);
            }

            if (seed.Category is "IVF" || seed.Code is "CON-SYR5" or "CON-IVC20" or "CON-IVSET" or "DRG-ADR1" or "DRG-HEP5000" or "DRG-MIDAZ1")
            {
                await ReceiveAsync("ICUS", 2, expiry, batchNumber);
            }

            if (seed.Category is "SUT" or "IMPL" || seed.Code is "SUR-BLADE22" or "CON-GLOVE7" or "DRG-MORPH10")
            {
                await ReceiveAsync("OTS", seed.Type == ItemType.Implant ? 1 : 2, expiry, batchNumber);
            }

            // Older batches that expire soon (or already have) so the expiry screens are not empty in a demo.
            if (seed.Code is "DRG-PCM500" or "DRG-CEFTRI1G" or "CON-IVC22")
            {
                await ReceiveAsync("OPPH", 2, EndOfMonth(today.AddDays(40)), batchNumber + "N");
            }

            if (seed.Code is "DRG-PANTO40" or "DRG-DICLOINJ")
            {
                await ReceiveAsync("CS", 3, today.AddDays(-12), batchNumber + "X");
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task SeedAssetsAsync(HospitalDbContext db, Facility facility, INumberSeries numbers, TimeProvider time, CancellationToken ct)
    {
        if (await db.Set<Asset>().AnyAsync(ct))
        {
            return;
        }

        var today = HospitalClock.Today(time);
        var departments = await db.Set<Department>().Where(d => d.FacilityId == facility.Id).ToDictionaryAsync(d => d.Code, d => d.Id, ct);
        var vendorId = await db.Set<Vendor>().Where(v => v.Code == "VEN-SBS").Select(v => (Guid?)v.Id).FirstOrDefaultAsync(ct);

        var demo = new (string Name, AssetCategory Category, string Make, string Model, string Department, string Location, AssetCriticality Criticality,
            decimal Cost, int AgeMonths, int? WarrantyInDays, bool Calibrate, int? CalibrationDueInDays, string? PmTitle, int PmEveryDays, int? PmDueInDays,
            MaintenanceContractType? Contract, int? ContractEndsInDays)[]
        {
            ("ICU Ventilator", AssetCategory.MedicalEquipment, "Vayu Respiratory", "VR-500", "ICU", "ICU Bed 1", AssetCriticality.Critical,
                1250000m, 22, 20, true, 5, "Quarterly ventilator PM", 90, 3, MaintenanceContractType.Cmc, 400),
            ("Biphasic Defibrillator", AssetCategory.MedicalEquipment, "Aarogya Medtech", "BD-200", "EMER", "Emergency resus bay", AssetCriticality.Critical,
                385000m, 30, null, true, 40, "Half-yearly defibrillator PM", 180, -2, MaintenanceContractType.Amc, 25),
            ("Multipara Patient Monitor", AssetCategory.MedicalEquipment, "Sanjeevani Devices", "PM-12", "ICU", "ICU Bed 2", AssetCriticality.High,
                210000m, 18, 180, false, null, "Half-yearly monitor PM", 180, 60, null, null),
            ("Digital X-ray System", AssetCategory.MedicalEquipment, "Kiran Imaging", "DR-600", "RAD", "X-ray room 1", AssetCriticality.High,
                2800000m, 40, null, true, 90, "Quarterly X-ray PM", 90, 12, MaintenanceContractType.Cmc, 28),
            ("Horizontal Autoclave 300 L", AssetCategory.MedicalEquipment, "Sterilite Engineering", "HA-300", "CSSD", "CSSD sterile zone", AssetCriticality.High,
                650000m, 50, null, true, 4, "Monthly autoclave PM", 30, 6, MaintenanceContractType.Amc, 200),
            ("12-channel ECG Machine", AssetCategory.MedicalEquipment, "Sanjeevani Devices", "ECG-12", "CDX", "ECG room", AssetCriticality.Medium,
                95000m, 26, null, false, null, "Yearly ECG PM", 365, 150, null, null),
            ("Volumetric Infusion Pump", AssetCategory.MedicalEquipment, "Aarogya Medtech", "IP-3", "ICU", "ICU Bed 3", AssetCriticality.High,
                85000m, 14, 220, true, 120, "Half-yearly pump PM", 180, 30, null, null),
            ("Pharmacy Counter Desktop", AssetCategory.IT, "Generic", "i5 desktop", "PHARM", "OP Pharmacy counter 1", AssetCriticality.Medium,
                52000m, 20, 160, false, null, null, 0, null, null, null),
            ("BLS Ambulance", AssetCategory.Vehicle, "Generic", "Type B", "EMER", "Ambulance bay", AssetCriticality.High,
                1450000m, 36, null, false, null, "Monthly vehicle service", 30, 9, null, null),
            ("Motorised ICU Bed", AssetCategory.Furniture, "Generic", "5-function", "ICU", "ICU Bed 4", AssetCriticality.Medium,
                175000m, 24, null, false, null, null, 0, null, null, null),
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var serial = 1000;
        Asset? pump = null;
        foreach (var d in demo)
        {
            serial += 17;
            var purchased = today.AddMonths(-d.AgeMonths);
            var asset = new Asset
            {
                FacilityId = facility.Id,
                AssetTag = await numbers.NextAsync(AssetService.TagSeries, facility.Id, AssetService.TagFormat, NumberResetPolicy.Never, ct),
                Name = d.Name,
                Category = d.Category,
                Make = d.Make,
                Model = d.Model,
                SerialNumber = string.Create(CultureInfo.InvariantCulture, $"SN{purchased.Year}{serial}"),
                DepartmentId = departments.TryGetValue(d.Department, out var departmentId) ? departmentId : null,
                Location = d.Location,
                PurchaseDate = purchased,
                InstallationDate = purchased.AddDays(7),
                PurchaseCost = d.Cost,
                VendorId = d.Category == AssetCategory.MedicalEquipment ? vendorId : null,
                WarrantyExpiry = d.WarrantyInDays is { } warranty ? today.AddDays(warranty) : purchased.AddYears(1),
                Criticality = d.Criticality,
                DepreciationMethod = d.Category == AssetCategory.MedicalEquipment ? DepreciationMethod.WrittenDownValue : DepreciationMethod.StraightLine,
                DepreciationRatePercent = d.Category == AssetCategory.MedicalEquipment ? 15m : 0m,
                UsefulLifeYears = d.Category == AssetCategory.IT ? 3 : d.Category == AssetCategory.Vehicle ? 8 : 10,
                SalvageValue = Money.Round(d.Cost * 0.05m),
                RequiresCalibration = d.Calibrate,
                NextCalibrationDue = d.CalibrationDueInDays is { } due ? today.AddDays(due) : null,
            };
            asset.Barcode = asset.AssetTag;
            db.Add(asset);

            if (d.PmTitle is not null && d.PmDueInDays is { } pmDue)
            {
                db.Add(new PreventiveMaintenancePlan
                {
                    AssetId = asset.Id,
                    Title = d.PmTitle,
                    FrequencyDays = d.PmEveryDays,
                    NextDueDate = today.AddDays(pmDue),
                    AssignedTo = d.Category == AssetCategory.MedicalEquipment ? "Southern Biomedical Services" : "Hospital maintenance",
                    VendorId = d.Category == AssetCategory.MedicalEquipment ? vendorId : null,
                    Checklist = "Visual inspection; electrical safety test; functional test; alarm test; cleaning; update service log.",
                });
            }

            if (d.Contract is { } type && d.ContractEndsInDays is { } ends)
            {
                db.Add(new AssetContract
                {
                    AssetId = asset.Id,
                    Type = type,
                    VendorId = vendorId,
                    ContractNumber = string.Create(CultureInfo.InvariantCulture, $"{type.ToString().ToUpperInvariant()}/{today.Year}/{serial}"),
                    StartDate = today.AddDays(ends).AddYears(-1).AddDays(1),
                    EndDate = today.AddDays(ends),
                    Cost = Money.Round(d.Cost * (type == MaintenanceContractType.Cmc ? 0.09m : 0.05m)),
                    CoverageNotes = type == MaintenanceContractType.Cmc ? "Labour, spares and preventive maintenance" : "Labour and preventive maintenance; spares chargeable",
                });
            }

            if (d.Calibrate && d.CalibrationDueInDays is { } nextDue)
            {
                db.Add(new CalibrationRecord
                {
                    AssetId = asset.Id,
                    CalibratedOn = today.AddDays(nextDue).AddYears(-1),
                    Agency = "NABL-accredited calibration laboratory",
                    CertificateNumber = string.Create(CultureInfo.InvariantCulture, $"CAL-{today.Year - 1}-{serial}"),
                    NextDueDate = today.AddDays(nextDue),
                    Outcome = CalibrationResult.Pass,
                });
            }

            if (d.Name.StartsWith("Volumetric", StringComparison.Ordinal))
            {
                pump = asset;
            }
        }

        if (pump is not null)
        {
            db.Add(new MaintenanceTicket
            {
                FacilityId = facility.Id,
                Number = await numbers.NextAsync(MaintenanceService.TicketSeries, facility.Id, MaintenanceService.TicketFormat, NumberResetPolicy.Yearly, ct),
                AssetId = pump.Id,
                ReportedAt = time.GetUtcNow().AddHours(-3),
                Issue = "Occlusion alarm keeps sounding with a patent line; pump stops mid-infusion.",
                Priority = TicketPriority.High,
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static DateOnly EndOfMonth(DateOnly date) => new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
}
