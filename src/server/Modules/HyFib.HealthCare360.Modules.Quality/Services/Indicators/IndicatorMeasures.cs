using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain.Hai;
using DiagnosticsPatientClass = HyFib.HealthCare360.Modules.Diagnostics.Domain.PatientClass;
using QualityIncident = HyFib.HealthCare360.Modules.Quality.Domain.Incident;
using QualityInvestigation = HyFib.HealthCare360.Modules.Quality.Domain.Investigation;

namespace HyFib.HealthCare360.Modules.Quality.Services.Indicators;

/// <summary>
/// The NABH key performance indicators HC360 computes from its own records: each one's definition and the queries for its
/// numerator and denominator, with the records behind them for the drill-down. Everything is read for one facility and one
/// period of local (IST) calendar days. Indicators HC360 cannot compute (hand hygiene, needle-stick injuries per worker at
/// risk, medication errors per chart reviewed, patient satisfaction) stay entered by hand.
/// </summary>
public sealed class IndicatorMeasures(HospitalDbContext db, ISurgeryAndTransfusionFacts facts, TimeProvider time)
{
    /// <summary>At most this many records are returned for a drill-down; the counts always cover every record.</summary>
    public const int MaxRecords = 2000;

    public static IReadOnlyList<MeasureDefinition> Catalogue { get; } =
    [
        new("ER-IA-TIME", "Time for initial assessment in the emergency department", "Patient care",
            "Sum of minutes from arrival in casualty to the first doctor's assessment", "Emergency patients seen by a doctor",
            IndicatorUnit.Minutes, 10, IndicatorDirection.LowerIsBetter, "AAC"),
        new("OPD-IA-TIME", "Time taken for initial assessment of OPD patients", "Patient care",
            "Sum of minutes from registration of the visit to the start of the doctor's consultation", "OPD visits with a consultation",
            IndicatorUnit.Minutes, 20, IndicatorDirection.LowerIsBetter, "AAC"),
        new("OPD-WAIT", "OPD waiting time", "Patient care",
            "Sum of minutes from the queue token's issue to the start of service", "Queue tokens served",
            IndicatorUnit.Minutes, 30, IndicatorDirection.LowerIsBetter, "AAC"),
        new("IPD-IA-TIME", "Time taken for initial assessment of in-patients", "Patient care",
            "Sum of minutes from admission to the first consultant visit", "Admissions with a consultant visit",
            IndicatorUnit.Minutes, 60, IndicatorDirection.LowerIsBetter, "AAC"),
        new("DIAG-WAIT-LAB", "Waiting time for laboratory sample collection (OPD)", "Patient care",
            "Sum of minutes from the out-patient lab order to the first sample collected", "Out-patient lab orders with a sample collected",
            IndicatorUnit.Minutes, 20, IndicatorDirection.LowerIsBetter, "AAC"),
        new("DIAG-WAIT-RAD", "Waiting time for imaging", "Patient care",
            "Sum of minutes from the patient's arrival in radiology to the start of the study", "Imaging studies started",
            IndicatorUnit.Minutes, 30, IndicatorDirection.LowerIsBetter, "AAC"),
        new("LAB-TAT", "Laboratory reports within turnaround time", "Patient care",
            "Tests released within the test's turnaround time from the order", "Tests released (with a turnaround time set)",
            IndicatorUnit.Percent, 95, IndicatorDirection.HigherIsBetter, "AAC"),
        new("MED-ERR-1000", "Medication errors per 1,000 patient days", "Medication safety",
            "Medication-error incidents involving an in-patient (reported by staff or raised from a missed dose)", "In-patient days (midnight census)",
            IndicatorUnit.PerThousand, 2, IndicatorDirection.LowerIsBetter, "MOM"),
        new("ADR-RATE", "Adverse drug reaction rate", "Medication safety",
            "ADR reports whose reaction started during an admission", "In-patients (admissions in the hospital during the period)",
            IndicatorUnit.Percent, 0.5m, IndicatorDirection.LowerIsBetter, "MOM"),
        new("SSI-RATE", "Surgical site infection rate", "Infection control",
            "Confirmed surgical site infections (date of event in the period)", "Operations completed",
            IndicatorUnit.Percent, 1, IndicatorDirection.LowerIsBetter, "HIC"),
        new("CLABSI", "Central line-associated bloodstream infection rate", "Infection control",
            "Confirmed CLABSIs (date of event in the period)", "Central-line days",
            IndicatorUnit.PerThousand, 2, IndicatorDirection.LowerIsBetter, "HIC"),
        new("VAP", "Ventilator-associated pneumonia rate", "Infection control",
            "Confirmed VAPs (date of event in the period)", "Ventilator days",
            IndicatorUnit.PerThousand, 5, IndicatorDirection.LowerIsBetter, "HIC"),
        new("CAUTI", "Catheter-associated urinary tract infection rate", "Infection control",
            "Confirmed CAUTIs (date of event in the period)", "Urinary-catheter days",
            IndicatorUnit.PerThousand, 3, IndicatorDirection.LowerIsBetter, "HIC"),
        new("FALL-RATE", "Patient fall rate", "Patient safety",
            "Falls of in-patients recorded on the nursing chart", "In-patient days (midnight census)",
            IndicatorUnit.PerThousand, 1, IndicatorDirection.LowerIsBetter, "PSQ"),
        new("PRESSURE-ULCER", "Pressure ulcer incidence", "Patient safety",
            "Pressure injuries that developed after admission (not present on admission)", "In-patient days (midnight census)",
            IndicatorUnit.PerThousand, 1, IndicatorDirection.LowerIsBetter, "COP"),
        new("SENTINEL-RCA", "Sentinel events analysed within 30 days", "Patient safety",
            "Sentinel events reported in the period whose root-cause analysis was completed within 30 days", "Sentinel events reported",
            IndicatorUnit.Percent, 100, IndicatorDirection.HigherIsBetter, "PSQ"),
        new("RTN-ICU-48", "Return to ICU within 48 hours", "Patient care",
            "Transfers out of critical care followed by a return to critical care within 48 hours", "Transfers out of critical care to a ward",
            IndicatorUnit.Percent, 3, IndicatorDirection.LowerIsBetter, "COP"),
        new("RTN-OT-48", "Unplanned return to OT within 48 hours", "Surgical care",
            "Urgent or emergency operations started within 48 hours of the same patient's previous operation in the stay", "Operations completed",
            IndicatorUnit.Percent, 1, IndicatorDirection.LowerIsBetter, "COP"),
        new("REINTUB-48", "Re-intubation rate within 48 hours", "Surgical care",
            "Extubations followed by ventilation again within 48 hours", "Extubations (planned or unplanned)",
            IndicatorUnit.Percent, 5, IndicatorDirection.LowerIsBetter, "COP"),
        new("TRANSF-RXN", "Transfusion reaction rate", "Surgical care",
            "Transfusion reactions (onset in the period)", "Units transfused (started at the bedside)",
            IndicatorUnit.Percent, 0.5m, IndicatorDirection.LowerIsBetter, "COP"),
        new("MORTALITY", "Hospital mortality rate", "Outcomes",
            "Deaths of in-patients", "Discharges of every kind, deaths included",
            IndicatorUnit.Percent, 2, IndicatorDirection.LowerIsBetter, "COP"),
        new("LAMA-DAMA", "Discharge against medical advice (LAMA/DAMA) rate", "Outcomes",
            "In-patients who left against medical advice or were discharged against it", "Discharges of every kind",
            IndicatorUnit.Percent, 2, IndicatorDirection.LowerIsBetter, "COP"),
        new("DISCH-TAT", "Discharge turnaround time", "Operations",
            "Sum of minutes from starting the discharge to the patient leaving", "Planned discharges",
            IndicatorUnit.Minutes, 180, IndicatorDirection.LowerIsBetter, "AAC"),
        new("ALOS", "Average length of stay", "Operations",
            "Days in hospital of the patients discharged (a same-day stay counts one)", "Discharges of every kind",
            IndicatorUnit.Days, 4, IndicatorDirection.LowerIsBetter, "ROM"),
        new("BED-OCC", "Bed occupancy rate", "Operations",
            "In-patient days (midnight census)", "Beds in service × days in the period",
            IndicatorUnit.Percent, 80, IndicatorDirection.HigherIsBetter, "ROM"),
    ];

    public static MeasureDefinition? Find(string code) => Catalogue.FirstOrDefault(m => m.Code == code);

    public Task<MeasureResult> ComputeAsync(string code, MeasurePeriod p, CancellationToken ct) => code switch
    {
        "ER-IA-TIME" => EmergencyAssessmentAsync(p, ct),
        "OPD-IA-TIME" => OpdAssessmentAsync(p, ct),
        "OPD-WAIT" => OpdWaitAsync(p, ct),
        "IPD-IA-TIME" => InpatientAssessmentAsync(p, ct),
        "DIAG-WAIT-LAB" => LabWaitAsync(p, ct),
        "DIAG-WAIT-RAD" => ImagingWaitAsync(p, ct),
        "LAB-TAT" => LabTatAsync(p, ct),
        "MED-ERR-1000" => MedicationErrorsAsync(p, ct),
        "ADR-RATE" => AdrRateAsync(p, ct),
        "SSI-RATE" => SsiRateAsync(p, ct),
        "CLABSI" => DeviceInfectionRateAsync(p, HaiType.Clabsi, ct),
        "VAP" => DeviceInfectionRateAsync(p, HaiType.Vap, ct),
        "CAUTI" => DeviceInfectionRateAsync(p, HaiType.Cauti, ct),
        "FALL-RATE" => NursingEventRateAsync(p, NursingSafetyEventKind.Fall, ct),
        "PRESSURE-ULCER" => NursingEventRateAsync(p, NursingSafetyEventKind.PressureInjury, ct),
        "SENTINEL-RCA" => SentinelRcaAsync(p, ct),
        "RTN-ICU-48" => IcuReturnAsync(p, ct),
        "RTN-OT-48" => OtReturnAsync(p, ct),
        "REINTUB-48" => ReintubationAsync(p, ct),
        "TRANSF-RXN" => TransfusionReactionsAsync(p, ct),
        "MORTALITY" => DischargeShareAsync(p, a => a.Status == AdmissionStatus.Expired, "Died", ct),
        "LAMA-DAMA" => DischargeShareAsync(p, a => a.Status == AdmissionStatus.Lama || a.Status == AdmissionStatus.Dama, "Left or discharged against medical advice", ct),
        "DISCH-TAT" => DischargeTatAsync(p, ct),
        "ALOS" => AverageStayAsync(p, ct),
        "BED-OCC" => OccupancyAsync(p, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "HC360 has no computation for this indicator."),
    };

    // ------------------------------------------------------------------ waits (average minutes)

    private async Task<MeasureResult> EmergencyAssessmentAsync(MeasurePeriod p, CancellationToken ct)
    {
        var rows = await (from c in Set<EmergencyCase>(p)
                          join v in db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on c.VisitId equals v.Id
                          where c.ArrivedAt >= p.Start && c.ArrivedAt < p.End && v.Status != VisitStatus.Cancelled
                          select new { c.PatientId, v.VisitNumber, c.ArrivedAt, c.DoctorSeenAt })
            .ToListAsync(ct);
        return Waits(rows.Select(r => (r.PatientId, $"Casualty visit {r.VisitNumber}", r.ArrivedAt, r.DoctorSeenAt, "Not seen by a doctor in HC360")), "Emergency visit");
    }

    private async Task<MeasureResult> OpdAssessmentAsync(MeasurePeriod p, CancellationToken ct)
    {
        var visits = await Set<Visit>(p)
            .Where(v => v.Type == VisitType.OutPatient && v.Status != VisitStatus.Cancelled && v.VisitedAt >= p.Start && v.VisitedAt < p.End)
            .Select(v => new { v.Id, v.PatientId, v.VisitNumber, v.VisitedAt })
            .ToListAsync(ct);
        var ids = visits.Select(v => v.Id).ToList();
        var seen = await db.Set<Encounter>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(e => e.VisitId != null && ids.Contains(e.VisitId.Value))
            .GroupBy(e => e.VisitId!.Value)
            .Select(g => new { VisitId = g.Key, First = g.Min(e => e.StartedAt) })
            .ToDictionaryAsync(x => x.VisitId, x => x.First, ct);
        return Waits(visits.Select(v => (v.PatientId, $"Visit {v.VisitNumber}", v.VisitedAt, seen.TryGetValue(v.Id, out var at) ? at : (DateTimeOffset?)null,
            "No consultation recorded in HC360")), "OPD visit");
    }

    private async Task<MeasureResult> OpdWaitAsync(MeasurePeriod p, CancellationToken ct)
    {
        var tokens = await Set<QueueToken>(p)
            .Where(t => t.IssuedAt >= p.Start && t.IssuedAt < p.End)
            .Select(t => new { t.PatientId, t.Label, t.TokenDate, t.IssuedAt, t.ServiceStartedAt })
            .ToListAsync(ct);
        return Waits(tokens.Select(t => (t.PatientId, $"Token {t.Label} on {t.TokenDate:dd MMM}", t.IssuedAt, t.ServiceStartedAt, "Not served")), "Queue token");
    }

    private async Task<MeasureResult> InpatientAssessmentAsync(MeasurePeriod p, CancellationToken ct)
    {
        var admissions = await Admissions(p).Where(a => a.AdmittedAt >= p.Start && a.AdmittedAt < p.End)
            .Select(a => new { a.Id, a.PatientId, a.AdmissionNumber, a.AdmittedAt })
            .ToListAsync(ct);
        var ids = admissions.Select(a => a.Id).ToList();
        var rounds = await db.Set<ConsultantVisit>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(v => ids.Contains(v.AdmissionId))
            .GroupBy(v => v.AdmissionId)
            .Select(g => new { AdmissionId = g.Key, First = g.Min(v => v.VisitedAt) })
            .ToDictionaryAsync(x => x.AdmissionId, x => x.First, ct);
        return Waits(admissions.Select(a => (a.PatientId, $"Admission {a.AdmissionNumber}", a.AdmittedAt,
            rounds.TryGetValue(a.Id, out var at) ? at : (DateTimeOffset?)null, "No consultant visit recorded")), "Admission");
    }

    private async Task<MeasureResult> LabWaitAsync(MeasurePeriod p, CancellationToken ct)
    {
        var items = db.Set<LabOrderItem>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);
        var samples = db.Set<LabSample>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);
        var orders = await Set<LabOrder>(p)
            .Where(o => o.PatientClass == DiagnosticsPatientClass.OutPatient && o.OrderedAt >= p.Start && o.OrderedAt < p.End && o.Status != LabOrderStatus.Cancelled)
            .Select(o => new
            {
                o.PatientId,
                o.AccessionNumber,
                o.OrderedAt,
                Collected = (from i in items
                             join s in samples on i.SampleId equals s.Id
                             where i.LabOrderId == o.Id && s.CollectedAt != null
                             select s.CollectedAt).Min(),
            })
            .ToListAsync(ct);
        return Waits(orders.Select(o => (o.PatientId, $"Lab order {o.AccessionNumber}", o.OrderedAt, o.Collected, "No sample collected")), "Lab order");
    }

    private async Task<MeasureResult> ImagingWaitAsync(MeasurePeriod p, CancellationToken ct)
    {
        var studies = await Set<ImagingStudy>(p)
            .Where(s => s.StartedAt >= p.Start && s.StartedAt < p.End)
            .Select(s => new { s.PatientId, s.AccessionNumber, s.StudyName, s.ArrivedAt, s.StartedAt })
            .ToListAsync(ct);
        var records = new List<MeasureRecord>();
        var spans = new List<(DateTimeOffset, DateTimeOffset)>();
        foreach (var s in studies)
        {
            if (s.ArrivedAt is not { } arrived)
            {
                records.Add(new MeasureRecord(MeasureRole.Excluded, "Imaging study", $"{s.AccessionNumber} {s.StudyName}", s.PatientId, s.StartedAt!.Value, null,
                    "The patient's arrival in radiology was not recorded"));
                continue;
            }

            spans.Add((arrived, s.StartedAt!.Value));
            records.Add(new MeasureRecord(MeasureRole.Numerator, "Imaging study", $"{s.AccessionNumber} {s.StudyName}", s.PatientId, arrived,
                Minutes(arrived, s.StartedAt.Value), null));
        }

        var (total, count) = IndicatorFormulas.Waits(spans);
        return new MeasureResult(total, count, Cap(records));
    }

    private static MeasureResult Waits(IEnumerable<(Guid PatientId, string Reference, DateTimeOffset Start, DateTimeOffset? End, string MissingEnd)> rows, string kind)
    {
        var records = new List<MeasureRecord>();
        var spans = new List<(DateTimeOffset, DateTimeOffset)>();
        foreach (var (patientId, reference, start, end, missingEnd) in rows)
        {
            if (end is not { } finished)
            {
                records.Add(new MeasureRecord(MeasureRole.Excluded, kind, reference, patientId, start, null, missingEnd));
            }
            else if (finished < start)
            {
                records.Add(new MeasureRecord(MeasureRole.Excluded, kind, reference, patientId, start, null, "Recorded as ending before it started"));
            }
            else
            {
                spans.Add((start, finished));
                records.Add(new MeasureRecord(MeasureRole.Numerator, kind, reference, patientId, start, Minutes(start, finished), null));
            }
        }

        var (total, count) = IndicatorFormulas.Waits(spans);
        return new MeasureResult(total, count, Cap(records), "The value is the average: total minutes ÷ the records timed.");
    }

    // ------------------------------------------------------------------ laboratory turnaround

    private async Task<MeasureResult> LabTatAsync(MeasurePeriod p, CancellationToken ct)
    {
        var items = await (from i in Set<LabOrderItem>(p)
                           join o in db.Set<LabOrder>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on i.LabOrderId equals o.Id
                           join t in db.Set<LabTest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on i.LabTestId equals t.Id
                           where i.Status == LabOrderItemStatus.Released && i.ReleasedAt >= p.Start && i.ReleasedAt < p.End
                           select new { o.PatientId, o.AccessionNumber, i.TestName, o.OrderedAt, ReleasedAt = i.ReleasedAt!.Value, t.TatHours })
            .ToListAsync(ct);
        var records = new List<MeasureRecord>();
        int within = 0, released = 0;
        foreach (var i in items)
        {
            var reference = $"{i.AccessionNumber} {i.TestName}";
            if (i.TatHours <= 0)
            {
                records.Add(new MeasureRecord(MeasureRole.Excluded, "Lab test", reference, i.PatientId, i.ReleasedAt, null, "The test has no turnaround time set"));
                continue;
            }

            released++;
            var minutes = Minutes(i.OrderedAt, i.ReleasedAt);
            var inTime = i.ReleasedAt - i.OrderedAt <= TimeSpan.FromHours(i.TatHours);
            within += inTime ? 1 : 0;
            records.Add(new MeasureRecord(inTime ? MeasureRole.Numerator : MeasureRole.Denominator, "Lab test", reference, i.PatientId, i.ReleasedAt, minutes,
                inTime ? $"Within {i.TatHours} h" : $"Beyond {i.TatHours} h"));
        }

        return new MeasureResult(within, released, Cap(records));
    }

    // ------------------------------------------------------------------ rates per 1,000 patient days

    private async Task<MeasureResult> MedicationErrorsAsync(MeasurePeriod p, CancellationToken ct)
    {
        var (patientDays, stays) = await PatientDaysAsync(p, ct);
        var incidents = await db.Set<QualityIncident>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(i => i.FacilityId == p.FacilityId && i.Type == IncidentType.MedicationError && i.PatientId != null && i.OccurredAt >= p.Start && i.OccurredAt < p.End)
            .Select(i => new { i.PatientId, i.IncidentNumber, i.OccurredAt, i.Severity, i.SourceModule })
            .ToListAsync(ct);
        var inpatients = await InpatientsAtAsync(p, incidents.Select(i => (i.PatientId!.Value, i.OccurredAt)), ct);
        var records = incidents.Select(i => inpatients.Contains((i.PatientId!.Value, i.OccurredAt))
                ? new MeasureRecord(MeasureRole.Numerator, "Incident", i.IncidentNumber, i.PatientId, i.OccurredAt, null,
                    $"{i.Severity}{(i.SourceModule is null ? string.Empty : $", raised from {i.SourceModule}")}")
                : new MeasureRecord(MeasureRole.Excluded, "Incident", i.IncidentNumber, i.PatientId, i.OccurredAt, null, "The patient was not an in-patient then"))
            .ToList();
        return new MeasureResult(records.Count(r => r.Role == MeasureRole.Numerator), patientDays, Cap(records), PatientDaysNote(patientDays, stays));
    }

    private async Task<MeasureResult> NursingEventRateAsync(MeasurePeriod p, NursingSafetyEventKind kind, CancellationToken ct)
    {
        var (patientDays, stays) = await PatientDaysAsync(p, ct);
        var events = await (from e in Set<NursingSafetyEvent>(p)
                            join a in db.Set<Admission>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on e.AdmissionId equals a.Id
                            where e.Kind == kind && e.OccurredAt >= p.Start && e.OccurredAt < p.End
                            select new { e.PatientId, a.AdmissionNumber, e.OccurredAt, e.PresentOnAdmission, e.FallInjury, e.Stage, e.BodySite, e.IncidentNumber })
            .ToListAsync(ct);
        var records = events.Select(e => e.PresentOnAdmission
                ? new MeasureRecord(MeasureRole.Excluded, "Pressure injury", $"Admission {e.AdmissionNumber}", e.PatientId, e.OccurredAt, null,
                    $"{e.Stage} at {e.BodySite}: present on admission")
                : new MeasureRecord(MeasureRole.Numerator, kind == NursingSafetyEventKind.Fall ? "Fall" : "Pressure injury", $"Admission {e.AdmissionNumber}",
                    e.PatientId, e.OccurredAt, null,
                    (kind == NursingSafetyEventKind.Fall ? $"Injury: {e.FallInjury}" : $"{e.Stage} at {e.BodySite}")
                    + (e.IncidentNumber is null ? string.Empty : $"; incident {e.IncidentNumber}")))
            .ToList();
        return new MeasureResult(records.Count(r => r.Role == MeasureRole.Numerator), patientDays, Cap(records), PatientDaysNote(patientDays, stays));
    }

    // ------------------------------------------------------------------ medication and transfusion

    private async Task<MeasureResult> AdrRateAsync(MeasurePeriod p, CancellationToken ct)
    {
        var stays = await Admissions(p).Where(a => a.AdmittedAt < p.End && (a.DischargedAt == null || a.DischargedAt >= p.Start))
            .Select(a => new { a.PatientId, a.AdmittedAt, a.DischargedAt })
            .ToListAsync(ct);
        var reports = await Set<AdrReport>(p)
            .Where(r => r.ReactionStartedOn >= p.From && r.ReactionStartedOn <= p.To)
            .Select(r => new { r.PatientId, r.Number, r.ReactionStartedOn, r.Reaction })
            .ToListAsync(ct);
        var records = new List<MeasureRecord>();
        foreach (var r in reports)
        {
            var day = r.ReactionStartedOn.DayNumber;
            var during = stays.Any(s => s.PatientId == r.PatientId && QualityClock.LocalDate(s.AdmittedAt).DayNumber <= day
                                        && (s.DischargedAt is not { } d || QualityClock.LocalDate(d).DayNumber >= day));
            records.Add(new MeasureRecord(during ? MeasureRole.Numerator : MeasureRole.Excluded, "ADR report", r.Number, r.PatientId,
                QualityClock.StartOfDayUtc(r.ReactionStartedOn), null, during ? r.Reaction : "The reaction did not start during an admission"));
        }

        return new MeasureResult(records.Count(r => r.Role == MeasureRole.Numerator), stays.Count, Cap(records),
            $"Denominator: {stays.Count} admissions in the hospital at some time in the period.");
    }

    private async Task<MeasureResult> TransfusionReactionsAsync(MeasurePeriod p, CancellationToken ct)
    {
        if (!facts.Available)
        {
            return Unavailable("the blood bank");
        }

        var transfusions = await facts.TransfusionsAsync(p.FacilityId, p.Start, p.End, ct);
        var reactions = await facts.TransfusionReactionsAsync(p.FacilityId, p.Start, p.End, ct);
        var records = reactions.Select(r => new MeasureRecord(MeasureRole.Numerator, "Transfusion reaction", $"Unit {r.UnitNumber}", r.PatientId, r.OnsetAt, null,
                $"{r.Severity}, {r.Type}"))
            .Concat(transfusions.Select(t => new MeasureRecord(MeasureRole.Denominator, "Transfusion", $"Unit {t.UnitNumber}", t.PatientId, t.StartedAt, null, null)))
            .ToList();
        return new MeasureResult(reactions.Count, transfusions.Count, Cap(records));
    }

    // ------------------------------------------------------------------ infections

    private async Task<MeasureResult> DeviceInfectionRateAsync(MeasurePeriod p, HaiType type, CancellationToken ct)
    {
        var device = HaiCriteria.DeviceFor(type)!.Value;
        var deviceDays = await DeviceDaysAsync(p, device, ct);
        var records = await ConfirmedCasesAsync(p, type, ct);
        return new MeasureResult(records.Count, deviceDays.Days, Cap(records),
            $"Denominator: {deviceDays.Days} {DeviceWord(device)} days from {deviceDays.Episodes} device episodes (each calendar day a patient had the device in place for any part of it).");
    }

    private async Task<MeasureResult> SsiRateAsync(MeasurePeriod p, CancellationToken ct)
    {
        if (!facts.Available)
        {
            return Unavailable("the operation theatre");
        }

        var operations = await facts.SurgeriesAsync(p.FacilityId, p.Start, p.End, ct);
        var records = await ConfirmedCasesAsync(p, HaiType.Ssi, ct);
        records.AddRange(operations.Select(o => new MeasureRecord(MeasureRole.Denominator, "Operation", $"{o.BookingNumber} {o.ProcedureName}", o.PatientId, o.CompletedAt, null, null)));
        return new MeasureResult(records.Count(r => r.Role == MeasureRole.Numerator), operations.Count, Cap(records),
            "An SSI may surface up to 30 days (90 with an implant) after the operation: a month's rate firms up when it is recomputed later.");
    }

    private async Task<List<MeasureRecord>> ConfirmedCasesAsync(MeasurePeriod p, HaiType type, CancellationToken ct) =>
        (await Set<HaiCase>(p)
            .Where(c => c.Type == type && c.Status == HaiCaseStatus.Confirmed && c.EventOn >= p.From && c.EventOn <= p.To)
            .Select(c => new { c.PatientId, c.CaseNumber, c.EventOn, c.Organism })
            .ToListAsync(ct))
        .Select(c => new MeasureRecord(MeasureRole.Numerator, "HAI case", c.CaseNumber, c.PatientId, QualityClock.StartOfDayUtc(c.EventOn), null, c.Organism))
        .ToList();

    internal async Task<(int Days, int Episodes)> DeviceDaysAsync(MeasurePeriod p, InvasiveDevice device, CancellationToken ct)
    {
        var episodes = await Set<DeviceEpisode>(p)
            .Where(e => e.Device == device && e.InsertedAt < p.End && (e.RemovedAt == null || e.RemovedAt >= p.Start))
            .ToListAsync(ct);
        var today = Math.Min(QualityClock.LocalDate(time.GetUtcNow()).DayNumber, p.ToDay);
        return (DeviceDays.Count(episodes.Select(DeviceDays.Span), p.FromDay, p.ToDay, today), episodes.Count);
    }

    // ------------------------------------------------------------------ returns and re-intubation

    private async Task<MeasureResult> IcuReturnAsync(MeasurePeriod p, CancellationToken ct)
    {
        var window = IndicatorFormulas.ReturnWindow;
        var stays = await (from b in Set<BedAssignment>(p)
                           join a in db.Set<Admission>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on b.AdmissionId equals a.Id
                           where a.Status != AdmissionStatus.Cancelled && b.FromAt < p.End + window && (b.ToAt == null || b.ToAt >= p.Start - window)
                           select new { a.Id, a.PatientId, a.AdmissionNumber, b.IsCriticalCare, b.FromAt, b.ToAt })
            .ToListAsync(ct);
        var records = new List<MeasureRecord>();
        foreach (var stay in stays.GroupBy(s => new { s.Id, s.PatientId, s.AdmissionNumber }))
        {
            foreach (var transfer in IndicatorFormulas.IcuTransfersOut(stay.Select(s => new CareSegment(s.IsCriticalCare, s.FromAt, s.ToAt)), window))
            {
                if (!p.Contains(transfer.At))
                {
                    continue;
                }

                records.Add(new MeasureRecord(transfer.Returned ? MeasureRole.Numerator : MeasureRole.Denominator, "Transfer out of critical care",
                    $"Admission {stay.Key.AdmissionNumber}", stay.Key.PatientId, transfer.At, null,
                    transfer.Returned ? $"Back in critical care after {Hours(transfer.At, transfer.ReturnedAt!.Value)} h" : null));
            }
        }

        return new MeasureResult(records.Count(r => r.Role == MeasureRole.Numerator), records.Count, Cap(records));
    }

    private async Task<MeasureResult> OtReturnAsync(MeasurePeriod p, CancellationToken ct)
    {
        if (!facts.Available)
        {
            return Unavailable("the operation theatre");
        }

        var window = IndicatorFormulas.ReturnWindow;
        var operations = await facts.SurgeriesAsync(p.FacilityId, p.Start - window - TimeSpan.FromDays(1), p.End + TimeSpan.FromDays(1), ct);
        var returns = IndicatorFormulas.UnplannedReturns(
            operations.Select(o => new Operation(o.BookingId, o.PatientId, o.AdmissionId, o.Elective, o.StartedAt, o.CompletedAt)), window);
        var records = new List<MeasureRecord>();
        foreach (var o in operations)
        {
            var isReturn = returns.Contains(o.BookingId) && p.Contains(o.StartedAt);
            var completedIn = p.Contains(o.CompletedAt);
            if (isReturn || completedIn)
            {
                records.Add(new MeasureRecord(isReturn ? MeasureRole.Numerator : MeasureRole.Denominator, "Operation", $"{o.BookingNumber} {o.ProcedureName}", o.PatientId,
                    o.StartedAt, null, isReturn ? "Unplanned return within 48 hours of the previous operation" : null));
            }
        }

        return new MeasureResult(records.Count(r => r.Role == MeasureRole.Numerator), operations.Count(o => p.Contains(o.CompletedAt)), Cap(records));
    }

    private async Task<MeasureResult> ReintubationAsync(MeasurePeriod p, CancellationToken ct)
    {
        var window = IndicatorFormulas.ReturnWindow;
        var spells = await (from e in Set<DeviceEpisode>(p)
                            join a in db.Set<Admission>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on e.AdmissionId equals a.Id
                            where e.Device == InvasiveDevice.Ventilator && e.InsertedAt < p.End + window && (e.RemovedAt == null || e.RemovedAt >= p.Start)
                            select new { e.Id, e.AdmissionId, e.PatientId, a.AdmissionNumber, e.InsertedAt, e.RemovedAt, e.RemovalReason })
            .ToListAsync(ct);
        var byId = spells.ToDictionary(s => s.Id);
        var extubations = IndicatorFormulas.Extubations(spells.Select(s => new VentilationSpell(s.Id, s.AdmissionId, s.InsertedAt, s.RemovedAt,
            s.RemovalReason is DeviceRemovalReason.Planned or DeviceRemovalReason.Accidental)), window);
        var records = extubations.Where(x => p.Contains(x.At))
            .Select(x => new MeasureRecord(x.Reintubated ? MeasureRole.Numerator : MeasureRole.Denominator, "Extubation", $"Admission {byId[x.SpellId].AdmissionNumber}",
                byId[x.SpellId].PatientId, x.At, null,
                (byId[x.SpellId].RemovalReason == DeviceRemovalReason.Accidental ? "Unplanned extubation" : "Planned extubation")
                + (x.Reintubated ? $"; ventilated again after {Hours(x.At, x.ReintubatedAt!.Value)} h" : string.Empty)))
            .ToList();
        return new MeasureResult(records.Count(r => r.Role == MeasureRole.Numerator), records.Count, Cap(records));
    }

    // ------------------------------------------------------------------ incidents

    private async Task<MeasureResult> SentinelRcaAsync(MeasurePeriod p, CancellationToken ct)
    {
        var investigations = db.Set<QualityInvestigation>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);
        var rows = await (from i in db.Set<QualityIncident>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                          where i.FacilityId == p.FacilityId && i.IsSentinel && i.ReportedAt >= p.Start && i.ReportedAt < p.End
                          let completed = investigations.Where(v => v.IncidentId == i.Id).Select(v => v.CompletedAt).FirstOrDefault()
                          select new { i.PatientId, i.IncidentNumber, i.ReportedAt, Completed = completed })
            .ToListAsync(ct);
        var records = rows.Select(r =>
        {
            var inTime = r.Completed is { } done && done - r.ReportedAt <= TimeSpan.FromDays(30);
            return new MeasureRecord(inTime ? MeasureRole.Numerator : MeasureRole.Denominator, "Sentinel event", r.IncidentNumber, r.PatientId, r.ReportedAt, null,
                r.Completed is { } c ? $"RCA completed after {(int)Math.Ceiling((c - r.ReportedAt).TotalDays)} days" : "RCA not completed");
        }).ToList();
        return new MeasureResult(records.Count(r => r.Role == MeasureRole.Numerator), records.Count, Cap(records));
    }

    // ------------------------------------------------------------------ discharges, stays and beds

    private async Task<MeasureResult> DischargeShareAsync(MeasurePeriod p, System.Linq.Expressions.Expression<Func<Admission, bool>> outcome, string word, CancellationToken ct)
    {
        var counted = outcome.Compile();
        var discharges = await Admissions(p).Where(a => a.DischargedAt >= p.Start && a.DischargedAt < p.End).ToListAsync(ct);
        var records = discharges.Select(a => new MeasureRecord(counted(a) ? MeasureRole.Numerator : MeasureRole.Denominator, "Discharge", $"Admission {a.AdmissionNumber}",
                a.PatientId, a.DischargedAt!.Value, null, counted(a) ? word : a.Status.ToString()))
            .ToList();
        return new MeasureResult(records.Count(r => r.Role == MeasureRole.Numerator), records.Count, Cap(records));
    }

    private async Task<MeasureResult> DischargeTatAsync(MeasurePeriod p, CancellationToken ct)
    {
        var discharges = await Admissions(p)
            .Where(a => a.Status == AdmissionStatus.Discharged && a.DischargedAt >= p.Start && a.DischargedAt < p.End)
            .Select(a => new { a.PatientId, a.AdmissionNumber, a.DischargeInitiatedAt, DischargedAt = a.DischargedAt!.Value })
            .ToListAsync(ct);
        return Waits(discharges.Select(d => (d.PatientId, $"Admission {d.AdmissionNumber}", d.DischargeInitiatedAt ?? d.DischargedAt,
            d.DischargeInitiatedAt is null ? (DateTimeOffset?)null : d.DischargedAt, "The discharge was not started in HC360 before the patient left")), "Discharge");
    }

    private async Task<MeasureResult> AverageStayAsync(MeasurePeriod p, CancellationToken ct)
    {
        var discharges = await Admissions(p).Where(a => a.DischargedAt >= p.Start && a.DischargedAt < p.End)
            .Select(a => new { a.PatientId, a.AdmissionNumber, a.AdmittedAt, DischargedAt = a.DischargedAt!.Value })
            .ToListAsync(ct);
        var records = discharges.Select(d =>
        {
            var days = IndicatorFormulas.StayDays(QualityClock.LocalDate(d.AdmittedAt).DayNumber, QualityClock.LocalDate(d.DischargedAt).DayNumber);
            return new MeasureRecord(MeasureRole.Numerator, "Discharge", $"Admission {d.AdmissionNumber}", d.PatientId, d.DischargedAt, null, $"{days} days");
        }).ToList();
        var total = discharges.Sum(d => IndicatorFormulas.StayDays(QualityClock.LocalDate(d.AdmittedAt).DayNumber, QualityClock.LocalDate(d.DischargedAt).DayNumber));
        return new MeasureResult(total, discharges.Count, Cap(records), "The value is the average: total days ÷ discharges.");
    }

    private async Task<MeasureResult> OccupancyAsync(MeasurePeriod p, CancellationToken ct)
    {
        var (patientDays, stays) = await PatientDaysAsync(p, ct);
        var beds = await Set<Bed>(p).CountAsync(b => b.IsActive && b.Status != BedStatus.Blocked && b.Status != BedStatus.Maintenance, ct);
        return new MeasureResult(patientDays, beds * p.Days, [],
            $"{PatientDaysNote(patientDays, stays)} Denominator: {beds} beds in service today (active, not blocked or under maintenance) × {p.Days} days.");
    }

    // ------------------------------------------------------------------ building blocks

    /// <summary>In-patient days of the period by the midnight census, and how many stays gave them.</summary>
    internal async Task<(int Days, int Stays)> PatientDaysAsync(MeasurePeriod p, CancellationToken ct)
    {
        var stays = await Admissions(p).Where(a => a.AdmittedAt < p.End && (a.DischargedAt == null || a.DischargedAt >= p.Start))
            .Select(a => new { a.AdmittedAt, a.DischargedAt })
            .ToListAsync(ct);
        var days = stays.Sum(s => IndicatorFormulas.CensusDays(QualityClock.LocalDate(s.AdmittedAt).DayNumber,
            s.DischargedAt is { } d ? QualityClock.LocalDate(d).DayNumber : null, p.FromDay, p.ToDay));
        return (days, stays.Count);
    }

    /// <summary>Which (patient, moment) pairs fall inside one of the patient's admissions.</summary>
    private async Task<HashSet<(Guid, DateTimeOffset)>> InpatientsAtAsync(MeasurePeriod p, IEnumerable<(Guid PatientId, DateTimeOffset At)> moments, CancellationToken ct)
    {
        var list = moments.ToList();
        var patients = list.Select(m => m.PatientId).Distinct().ToList();
        var stays = await Admissions(p).Where(a => patients.Contains(a.PatientId)).Select(a => new { a.PatientId, a.AdmittedAt, a.DischargedAt }).ToListAsync(ct);
        return list.Where(m => stays.Any(s => s.PatientId == m.PatientId && s.AdmittedAt <= m.At && (s.DischargedAt == null || s.DischargedAt >= m.At))).ToHashSet();
    }

    private IQueryable<Admission> Admissions(MeasurePeriod p) => Set<Admission>(p).Where(a => a.Status != AdmissionStatus.Cancelled);

    private IQueryable<T> Set<T>(MeasurePeriod p)
        where T : class, IFacilityScoped =>
        db.Set<T>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(x => x.FacilityId == p.FacilityId);

    private static string PatientDaysNote(int days, int stays) => $"Denominator: {days} in-patient days (midnight census) from {stays} stays.";

    private static MeasureResult Unavailable(string what) =>
        new(0, 0, [], $"HC360 has no records from {what} here, so this indicator cannot be computed.", Unavailable: true);

    private static string DeviceWord(InvasiveDevice device) => device switch
    {
        InvasiveDevice.CentralLine => "central-line",
        InvasiveDevice.UrinaryCatheter => "urinary-catheter",
        _ => "ventilator",
    };

    private static decimal Minutes(DateTimeOffset start, DateTimeOffset end) => Math.Round((decimal)(end - start).TotalMinutes, 1, MidpointRounding.AwayFromZero);

    private static string Hours(DateTimeOffset start, DateTimeOffset end) => Math.Round((end - start).TotalHours, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private static IReadOnlyList<MeasureRecord> Cap(List<MeasureRecord> records) =>
        records.Count <= MaxRecords ? records : [.. records.OrderBy(r => r.Role).ThenBy(r => r.At).Take(MaxRecords)];
}
