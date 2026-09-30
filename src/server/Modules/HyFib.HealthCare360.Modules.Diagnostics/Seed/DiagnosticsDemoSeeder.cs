using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Diagnostics.Seed;

/// <summary>
/// Demonstration data (only when <c>HealthCare360:SeedDemoData</c> is true). It adds three analysers, a reference
/// laboratory and QC lots for glucose, creatinine, potassium and haemoglobin, each with three weeks of Westgard-evaluated
/// runs. Levey-Jennings charts then have history, and today's accepted QC lets analyser results auto-validate in a demo.
/// </summary>
internal sealed class DiagnosticsDemoSeeder(IOptions<PlatformOptions> options, TimeProvider time) : ISeeder
{
    /// <summary>Deterministic z-scores: one 1-2s warning and no rejection, so the demo QC state is "accepted" today.</summary>
    private static readonly decimal[] ZSequence =
        [0.4m, -0.7m, 1.2m, 0.3m, -0.2m, 1.5m, -1.1m, 0.6m, 0.1m, -0.4m, 2.2m, 0.2m, -0.9m, 0.5m, -0.3m, 0.9m, -1.3m, 0.4m, 0.0m, -0.6m, 0.3m];

    public int Order => 210;

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        if (!options.Value.SeedDemoData)
        {
            return;
        }

        var facilityId = await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => (Guid?)f.Id).FirstOrDefaultAsync(cancellationToken);
        if (facilityId is not { } facility)
        {
            return;
        }

        await SeedInstrumentsAsync(db, facility, cancellationToken);
        await SeedReferralLabAsync(db, cancellationToken);
        await SeedQualityControlAsync(db, facility, cancellationToken);
    }

    private static async Task SeedInstrumentsAsync(HospitalDbContext db, Guid facilityId, CancellationToken ct)
    {
        if (await db.Set<LabInstrument>().IgnoreQueryFilters().AnyAsync(ct))
        {
            return;
        }

        db.Add(new LabInstrument
        {
            FacilityId = facilityId,
            Code = "XN550",
            Name = "Haematology analyser",
            Manufacturer = "Sysmex",
            Model = "XN-550",
            Section = LabSection.Hematology,
            Protocol = InstrumentProtocol.Hl7,
            IsBidirectional = true,
            Location = "Haematology bench",
        });
        db.Add(new LabInstrument
        {
            FacilityId = facilityId,
            Code = "EM360",
            Name = "Clinical chemistry analyser",
            Manufacturer = "Transasia Erba",
            Model = "EM 360",
            Section = LabSection.Biochemistry,
            Protocol = InstrumentProtocol.Astm,
            IsBidirectional = true,
            Location = "Biochemistry bench",
        });
        db.Add(new LabInstrument
        {
            FacilityId = facilityId,
            Code = "CL900I",
            Name = "Immunoassay analyser",
            Manufacturer = "Mindray",
            Model = "CL-900i",
            Section = LabSection.Immunology,
            Protocol = InstrumentProtocol.Hl7,
            IsBidirectional = false,
            Location = "Immunoassay bench",
        });
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedReferralLabAsync(HospitalDbContext db, CancellationToken ct)
    {
        if (await db.Set<ReferralLab>().IgnoreQueryFilters().AnyAsync(ct))
        {
            return;
        }

        db.Add(new ReferralLab
        {
            Code = "MRL",
            Name = "Metro Reference Laboratory (demo)",
            ContactPerson = "Sample coordinator",
            Address = "Anna Salai, Chennai, Tamil Nadu",
            AccreditationNumber = "NABL (demo)",
            TatDays = 3,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedQualityControlAsync(HospitalDbContext db, Guid facilityId, CancellationToken ct)
    {
        if (await db.Set<QcLot>().IgnoreQueryFilters().AnyAsync(ct))
        {
            return;
        }

        var tests = await db.Set<LabTest>().Where(t => t.Code == "LAB-FBS" || t.Code == "CREA" || t.Code == "K" || t.Code == "HB")
            .ToDictionaryAsync(t => t.Code, t => t.Id, ct);
        var today = DiagnosticsTime.Today(time);
        var expiry = today.AddDays(300);
        const string Chemistry = "Bio-Rad Lyphochek Assayed Chemistry Control";
        const string Haematology = "Sysmex XN CHECK";

        var lots = new List<QcLot>();
        void Lot(string code, string material, int level, string lotNumber, decimal mean, decimal sd, string unit, string instrument)
        {
            if (tests.TryGetValue(code, out var testId))
            {
                lots.Add(new QcLot
                {
                    FacilityId = facilityId,
                    LabTestId = testId,
                    Material = material,
                    Level = level,
                    LotNumber = lotNumber,
                    ExpiryDate = expiry,
                    TargetMean = mean,
                    TargetSd = sd,
                    Unit = unit,
                    InstrumentCode = instrument,
                });
            }
        }

        Lot("LAB-FBS", Chemistry, 1, "26451", 95m, 3m, "mg/dL", "EM360");
        Lot("LAB-FBS", Chemistry, 2, "26452", 270m, 8m, "mg/dL", "EM360");
        Lot("CREA", Chemistry, 1, "26451", 1.10m, 0.05m, "mg/dL", "EM360");
        Lot("CREA", Chemistry, 2, "26452", 5.20m, 0.20m, "mg/dL", "EM360");
        Lot("K", Chemistry, 1, "26451", 4.0m, 0.10m, "mmol/L", "EM360");
        Lot("K", Chemistry, 2, "26452", 6.5m, 0.15m, "mmol/L", "EM360");
        Lot("HB", Haematology, 1, "XN-6012", 6.0m, 0.15m, "g/dL", "XN550");
        Lot("HB", Haematology, 2, "XN-6013", 12.5m, 0.25m, "g/dL", "XN550");
        Lot("HB", Haematology, 3, "XN-6014", 16.5m, 0.35m, "g/dL", "XN550");
        db.AddRange(lots);

        // Runs every morning for three weeks (levels a few minutes apart), evaluated exactly as live runs are.
        var now = time.GetUtcNow();
        var days = ZSequence.Length;
        foreach (var assay in lots.GroupBy(l => (l.LabTestId, l.InstrumentCode)))
        {
            var history = new List<decimal>();
            for (var day = 0; day < days; day++)
            {
                var date = today.AddDays(day - (days - 1));
                var morning = DiagnosticsTime.StartOfDayUtc(date).AddHours(7).AddMinutes(30);
                foreach (var (lot, index) in assay.Select((l, i) => (l, i)))
                {
                    var runAt = morning.AddMinutes(5 * index);
                    if (runAt > now)
                    {
                        runAt = now.AddMinutes(-(5 * (assay.Count() - index)));
                    }

                    var z = ZSequence[(day + (3 * index)) % days];
                    var value = Math.Round(lot.TargetMean + (z * lot.TargetSd), 2, MidpointRounding.AwayFromZero);
                    var zScore = lot.ZScore(value);
                    var evaluation = WestgardEvaluator.Evaluate(history, zScore);
                    if (!evaluation.IsRejected)
                    {
                        history.Add(zScore);
                    }

                    db.Add(new QcRun
                    {
                        FacilityId = facilityId,
                        QcLotId = lot.Id,
                        LabTestId = lot.LabTestId,
                        InstrumentCode = lot.InstrumentCode,
                        Value = value,
                        ZScore = zScore,
                        RunAt = runAt,
                        Outcome = evaluation.Outcome,
                        Violations = [.. evaluation.Violations],
                    });
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
