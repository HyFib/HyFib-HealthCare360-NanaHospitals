using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Seed;

/// <summary>
/// Service catalogue entries billed by OT, dialysis, physiotherapy and blood bank (prices in INR; Billing tariffs
/// may override them), plus demonstration theatres and sero-segregated dialysis machines when
/// <c>HealthCare360:SeedDemoData</c> is enabled. Idempotent: rows are matched by code.
/// </summary>
internal sealed class SpecialtyCareSeeder(IOptions<PlatformOptions> options) : ISeeder
{
    public int Order => 170;

    private static readonly (string Code, string Name, ServiceCategory Category, decimal Price, string? Department, bool Editable)[] Services =
    [
        (OtServiceCodes.MajorTheatreHour, "OT charges — major theatre (per hour)", ServiceCategory.Surgery, 4000m, "OT", false),
        (OtServiceCodes.MinorTheatreHour, "OT charges — minor theatre (per hour)", ServiceCategory.Surgery, 1500m, "OT", false),
        (OtServiceCodes.SurgeonFee, "Surgeon fee", ServiceCategory.Surgery, 10000m, "OT", true),
        (OtServiceCodes.AnaesthetistFee, "Anaesthetist fee", ServiceCategory.Surgery, 4000m, "OT", true),
        (OtServiceCodes.Implant, "Implant (at invoice price)", ServiceCategory.Consumable, 0m, "OT", true),
        (OtServiceCodes.Consumable, "OT consumables (at actuals)", ServiceCategory.Consumable, 0m, "OT", true),
        ("PROC-LSCS", "Lower segment caesarean section (LSCS)", ServiceCategory.Surgery, 35000m, "OBG", true),
        ("PROC-APPENDECTOMY", "Appendicectomy", ServiceCategory.Surgery, 30000m, "GS", true),
        ("PROC-LAP-CHOLE", "Laparoscopic cholecystectomy", ServiceCategory.Surgery, 55000m, "GS", true),
        ("PROC-TKR", "Total knee replacement — unilateral (excluding implant)", ServiceCategory.Surgery, 150000m, "ORTH", true),
        ("PROC-HERNIA", "Inguinal hernia repair with mesh", ServiceCategory.Surgery, 35000m, "GS", true),
        ("DIA-SESSION", "Haemodialysis session", ServiceCategory.Dialysis, 1800m, "DIAL", false),
        ("DIA-DIALYZER", "Dialyzer (new)", ServiceCategory.Dialysis, 1200m, "DIAL", true),
        ("PHY-IFT", "Interferential therapy (IFT)", ServiceCategory.Physiotherapy, 300m, "PHYSIO", false),
        ("PHY-TENS", "Transcutaneous electrical nerve stimulation (TENS)", ServiceCategory.Physiotherapy, 250m, "PHYSIO", false),
        ("PHY-UST", "Therapeutic ultrasound (UST)", ServiceCategory.Physiotherapy, 300m, "PHYSIO", false),
        ("PHY-EXERCISE", "Exercise therapy", ServiceCategory.Physiotherapy, 350m, "PHYSIO", false),
        ("PHY-SESSION", "Physiotherapy session (general)", ServiceCategory.Physiotherapy, 400m, "PHYSIO", false),
        ("BB-WB", "Whole blood (per unit, processing charge)", ServiceCategory.BloodBank, 1450m, "BB", false),
        ("BB-PRBC", "Packed red blood cells (per unit, processing charge)", ServiceCategory.BloodBank, 1450m, "BB", false),
        ("BB-FFP", "Fresh frozen plasma (per unit, processing charge)", ServiceCategory.BloodBank, 400m, "BB", false),
        ("BB-PLT", "Platelet concentrate (per unit, processing charge)", ServiceCategory.BloodBank, 400m, "BB", false),
        ("BB-CRYO", "Cryoprecipitate (per unit, processing charge)", ServiceCategory.BloodBank, 250m, "BB", false),
        ("BB-CROSSMATCH", "Blood grouping and cross-match (per unit)", ServiceCategory.BloodBank, 250m, "BB", false),
    ];

    private static readonly (string Code, string Name, TheatreType Type, string[] Equipment)[] DemoTheatres =
    [
        ("OT-1", "Main OT 1 (General surgery)", TheatreType.Major, ["Laparoscopy tower", "Electrosurgical unit", "Anaesthesia workstation"]),
        ("OT-2", "Main OT 2 (Orthopaedics)", TheatreType.Ortho, ["Laminar airflow", "C-arm", "Pneumatic tourniquet"]),
        ("OT-3", "Minor OT", TheatreType.Minor, ["Electrosurgical unit", "Procedure light"]),
    ];

    private static readonly (string Code, string Model, string Station, SeroStatus Sero)[] DemoMachines =
    [
        ("HD-01", "Fresenius 4008S", "Station 1", SeroStatus.Negative),
        ("HD-02", "Fresenius 4008S", "Station 2", SeroStatus.Negative),
        ("HD-03", "Nipro Surdial X", "Station 3", SeroStatus.Negative),
        ("HD-04", "Nipro Surdial X", "Isolation room A", SeroStatus.HBsAg),
        ("HD-05", "Fresenius 4008S", "Isolation room B", SeroStatus.HCV),
        ("HD-06", "B. Braun Dialog+", "Isolation room C", SeroStatus.HIV),
    ];

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        var facility = await db.Set<Facility>().OrderBy(f => f.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (facility is null)
        {
            return;
        }

        var departments = await db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(d => d.FacilityId == facility.Id)
            .ToDictionaryAsync(d => d.Code, d => d.Id, cancellationToken);
        var existingCodes = await db.Set<ServiceItem>().IgnoreQueryFilters().Select(s => s.Code).ToListAsync(cancellationToken);
        foreach (var (code, name, category, price, department, editable) in Services.Where(s => !existingCodes.Contains(s.Code)))
        {
            db.Add(new ServiceItem
            {
                Code = code,
                Name = name,
                Category = category,
                BasePrice = price,
                DepartmentId = department is not null && departments.TryGetValue(department, out var departmentId) ? departmentId : null,
                IsPriceEditable = editable,
                SacCode = "999312",
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        if (options.Value.SeedDemoData)
        {
            await SeedDemoAsync(db, facility.Id, cancellationToken);
        }
    }

    private static async Task SeedDemoAsync(HospitalDbContext db, Guid facilityId, CancellationToken ct)
    {
        var theatreCodes = await db.Set<OperationTheatre>().IgnoreQueryFilters().Where(t => t.FacilityId == facilityId).Select(t => t.Code).ToListAsync(ct);
        var order = 0;
        foreach (var (code, name, type, equipment) in DemoTheatres)
        {
            order += 10;
            if (!theatreCodes.Contains(code))
            {
                db.Add(new OperationTheatre { FacilityId = facilityId, Code = code, Name = name, Type = type, Equipment = [.. equipment], DisplayOrder = order });
            }
        }

        var machineCodes = await db.Set<DialysisMachine>().IgnoreQueryFilters().Where(m => m.FacilityId == facilityId).Select(m => m.Code).ToListAsync(ct);
        foreach (var (code, model, station, sero) in DemoMachines.Where(m => !machineCodes.Contains(m.Code)))
        {
            db.Add(new DialysisMachine
            {
                FacilityId = facilityId,
                Code = code,
                Model = model,
                Station = station,
                SeroDedication = sero,
                Status = DialysisMachineStatus.Available,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
