using System.Globalization;
using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// What the timeline shows. <see cref="Types"/> are kinds of entry (encounters, vitals, notes…); <see cref="EncounterTypes"/>
/// are kinds of care (OP, IP, ER, telehealth, health checkup) and, like <see cref="DepartmentId"/>, keep the encounters of
/// that kind and every entry recorded in one. Empty sets mean no filter.
/// </summary>
public sealed record TimelineFilter(
    IReadOnlySet<TimelineEntryType> Types,
    DateOnly? From,
    DateOnly? To,
    Guid? DepartmentId,
    IReadOnlySet<EncounterType> EncounterTypes);

/// <summary>
/// The longitudinal EHR timeline (EHR-01/02) across OP, IP, ER, telehealth and health-checkup care, filterable by type,
/// kind of encounter, department and date. Each source is queried for its newest (skip + take) rows only and the results are merged, so
/// deep histories page efficiently without a database-wide UNION. Entries from every facility are included.
/// </summary>
public sealed class TimelineQueries(HospitalDbContext db)
{
    private delegate Task<(List<TimelineEntry> Rows, int Count)> SourceLoader(int take, CancellationToken ct);

    public async Task<PagedResult<TimelineEntry>> QueryAsync(Guid patientId, TimelineFilter filter, PageRequest page, CancellationToken ct)
    {
        var from = filter.From is { } f ? ClinicalClock.StartOfDayUtc(f) : (DateTimeOffset?)null;
        var to = filter.To is { } t ? ClinicalClock.StartOfDayUtc(t.AddDays(1)) : (DateTimeOffset?)null;
        var fromDate = filter.From;
        var toDate = filter.To;

        var encounters = Facilityless<Encounter>().Where(e => e.PatientId == patientId)
            .Where(e => (from == null || e.StartedAt >= from) && (to == null || e.StartedAt < to));
        var vitals = db.Set<VitalSigns>().AsNoTracking().Where(v => v.PatientId == patientId && !v.IsEnteredInError)
            .Where(v => (from == null || v.RecordedAt >= from) && (to == null || v.RecordedAt < to));
        var diagnoses = db.Set<Diagnosis>().AsNoTracking().Where(d => d.PatientId == patientId)
            .Where(d => (from == null || d.RecordedAt >= from) && (to == null || d.RecordedAt < to));
        var prescriptions = Facilityless<Prescription>().Where(p => p.PatientId == patientId && p.Status != PrescriptionStatus.Draft)
            .Where(p => (from == null || p.PrescribedAt >= from) && (to == null || p.PrescribedAt < to));
        var orders = Facilityless<ClinicalOrder>().Where(o => o.PatientId == patientId)
            .Where(o => (from == null || o.OrderedAt >= from) && (to == null || o.OrderedAt < to));
        var notes = Facilityless<ClinicalNote>().Where(n => n.PatientId == patientId)
            .Where(n => (from == null || n.CreatedAt >= from) && (to == null || n.CreatedAt < to));
        var results = Facilityless<ResultReview>().Where(r => r.PatientId == patientId)
            .Where(r => (from == null || r.ReleasedAt >= from) && (to == null || r.ReleasedAt < to));
        var procedures = Facilityless<ProcedureRecord>().Where(p => p.PatientId == patientId && p.Status != ProcedureStatus.EnteredInError)
            .Where(p => (from == null || p.PerformedAt >= from) && (to == null || p.PerformedAt < to));
        var immunizations = db.Set<Immunization>().AsNoTracking().Where(i => i.PatientId == patientId && !i.IsEnteredInError)
            .Where(i => (fromDate == null || i.AdministeredOn >= fromDate) && (toDate == null || i.AdministeredOn <= toDate));
        var summaries = Facilityless<DischargeSummary>().Where(s => s.PatientId == patientId && s.Status != DischargeSummaryStatus.Draft)
            .Where(s => (from == null || s.CreatedAt >= from) && (to == null || s.CreatedAt < to));

        // A department or a kind of care narrows the timeline to the matching encounters and what was recorded in them.
        var scoped = filter.DepartmentId is not null || filter.EncounterTypes.Count > 0;
        if (scoped)
        {
            var inScope = Facilityless<Encounter>().Where(e => e.PatientId == patientId);
            if (filter.DepartmentId is { } departmentId)
            {
                inScope = inScope.Where(e => e.DepartmentId == departmentId);
                encounters = encounters.Where(e => e.DepartmentId == departmentId);
            }

            if (filter.EncounterTypes.Count > 0)
            {
                var kinds = filter.EncounterTypes.ToList();
                inScope = inScope.Where(e => kinds.Contains(e.Type));
                encounters = encounters.Where(e => kinds.Contains(e.Type));
            }

            var ids = inScope.Select(e => (Guid?)e.Id);
            vitals = vitals.Where(v => ids.Contains(v.EncounterId));
            diagnoses = diagnoses.Where(d => ids.Contains(d.EncounterId));
            prescriptions = prescriptions.Where(p => ids.Contains(p.EncounterId));
            orders = orders.Where(o => ids.Contains(o.EncounterId));
            notes = notes.Where(n => ids.Contains(n.EncounterId));
            results = results.Where(r => ids.Contains(r.EncounterId));
            procedures = procedures.Where(p => ids.Contains(p.EncounterId));
            immunizations = immunizations.Where(i => ids.Contains(i.EncounterId));
            summaries = summaries.Where(s => ids.Contains(s.EncounterId));
        }

        var sources = new Dictionary<TimelineEntryType, SourceLoader>
        {
            [TimelineEntryType.Encounter] = (take, token) => LoadAsync(encounters, e => e.StartedAt,
                e => new TimelineEntry(TimelineEntryType.Encounter, e.Id, e.StartedAt, $"{e.Type} encounter", e.ChiefComplaint, e.Status.ToString(), e.Id, false),
                take, token),
            [TimelineEntryType.Vitals] = (take, token) => LoadAsync(vitals, v => v.RecordedAt,
                v => new TimelineEntry(TimelineEntryType.Vitals, v.Id, v.RecordedAt, "Vital signs", DescribeVitals(v), null, v.EncounterId, false),
                take, token),
            [TimelineEntryType.Diagnosis] = (take, token) => LoadAsync(diagnoses, d => d.RecordedAt,
                d => new TimelineEntry(TimelineEntryType.Diagnosis, d.Id, d.RecordedAt, d.IcdCode == null ? d.Title : $"{d.IcdCode} {d.Title}",
                    $"{d.Type}, {d.Status}", d.Status.ToString(), d.EncounterId, false),
                take, token),
            [TimelineEntryType.Prescription] = (take, token) => LoadAsync(
                prescriptions.Select(p => new { p.Id, p.PrescriptionNumber, p.PrescribedAt, p.Status, p.EncounterId, Drugs = p.Items.OrderBy(i => i.Sequence).Select(i => i.DrugName).ToList() }),
                p => p.PrescribedAt,
                p => new TimelineEntry(TimelineEntryType.Prescription, p.Id, p.PrescribedAt, $"Prescription {p.PrescriptionNumber}", string.Join(", ", p.Drugs),
                    p.Status.ToString(), p.EncounterId, false),
                take, token),
            [TimelineEntryType.Order] = (take, token) => LoadAsync(
                orders.Select(o => new { o.Id, o.OrderNumber, o.OrderedAt, o.Status, o.Priority, o.EncounterId, Services = o.Items.Select(i => i.ServiceName).ToList() }),
                o => o.OrderedAt,
                o => new TimelineEntry(TimelineEntryType.Order, o.Id, o.OrderedAt, $"Order {o.OrderNumber} ({o.Priority})", string.Join(", ", o.Services),
                    o.Status.ToString(), o.EncounterId, o.Priority == OrderPriority.Stat),
                take, token),
            [TimelineEntryType.Note] = (take, token) => LoadAsync(
                notes.Select(n => new { n.Id, n.Type, n.Title, n.CreatedAt, n.IsSigned, n.EncounterId, Excerpt = n.Body.Length > 200 ? n.Body.Substring(0, 200) : n.Body }),
                n => n.CreatedAt,
                n => new TimelineEntry(TimelineEntryType.Note, n.Id, n.CreatedAt, n.Title ?? $"{n.Type} note", n.Excerpt, n.IsSigned ? "Signed" : "Unsigned", n.EncounterId, false),
                take, token),
            [TimelineEntryType.Result] = (take, token) => LoadAsync(results, r => r.ReleasedAt,
                r => new TimelineEntry(TimelineEntryType.Result, r.ReportId, r.ReleasedAt, r.Title, r.Kind.ToString(), r.ReviewedAt == null ? "Awaiting review" : "Reviewed",
                    r.EncounterId, r.HasCriticalFindings)
                {
                    ReportKind = r.Kind,
                },
                take, token),
            [TimelineEntryType.Procedure] = (take, token) => LoadAsync(procedures, p => p.PerformedAt,
                p => new TimelineEntry(TimelineEntryType.Procedure, p.Id, p.PerformedAt, p.Code == null ? p.Display : $"{p.Code} {p.Display}",
                    p.Outcome, p.Status.ToString(), p.EncounterId, false),
                take, token),
            [TimelineEntryType.Immunization] = (take, token) => LoadAsync(immunizations, i => i.AdministeredOn,
                i => new TimelineEntry(TimelineEntryType.Immunization, i.Id, ClinicalClock.StartOfDayUtc(i.AdministeredOn), i.VaccineName,
                    i.DoseNumber is { } dose ? $"Dose {dose.ToString(CultureInfo.InvariantCulture)}" : null, i.IsExternal ? "Recorded from card" : "Given here", i.EncounterId, false),
                take, token),
            [TimelineEntryType.DischargeSummary] = (take, token) => LoadAsync(summaries, s => s.CreatedAt,
                s => new TimelineEntry(TimelineEntryType.DischargeSummary, s.Id, s.SignedAt ?? s.CreatedAt, "Discharge summary", s.Diagnoses, s.Status.ToString(), s.EncounterId, false),
                take, token),
        };

        if (!scoped)
        {
            // Patient documents belong to no encounter (and so to no department or kind of care): only the unfiltered timeline shows them.
            sources[TimelineEntryType.Document] = (take, token) => LoadAsync(
                db.Set<PatientDocument>().AsNoTracking().Where(d => d.PatientId == patientId)
                    .Where(d => (from == null || d.CreatedAt >= from) && (to == null || d.CreatedAt < to)),
                d => d.CreatedAt,
                d => new TimelineEntry(TimelineEntryType.Document, d.Id, d.CreatedAt, d.Title, d.Source, d.Type.ToString(), null, false),
                take, token);
        }

        var take = page.Skip + page.Take;
        var rows = new List<TimelineEntry>();
        long total = 0;
        foreach (var (type, load) in sources)
        {
            if (filter.Types.Count > 0 && !filter.Types.Contains(type))
            {
                continue;
            }

            // Sequential on purpose: one DbContext cannot run queries concurrently.
            var (entries, count) = await load(take, ct);
            rows.AddRange(entries);
            total += count;
        }

        var items = rows.OrderByDescending(r => r.OccurredAt).Skip(page.Skip).Take(page.Take).ToList();
        return new PagedResult<TimelineEntry>(items, page.Page, page.Take, total);
    }

    /// <summary>The EHR is longitudinal: bypass the facility filter but keep the soft-delete filter.</summary>
    private IQueryable<T> Facilityless<T>()
        where T : class =>
        db.Set<T>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);

    private static async Task<(List<TimelineEntry> Rows, int Count)> LoadAsync<T, TKey>(
        IQueryable<T> query, Expression<Func<T, TKey>> orderBy, Func<T, TimelineEntry> map, int take, CancellationToken ct)
    {
        var count = await query.CountAsync(ct);
        if (count == 0)
        {
            return ([], 0);
        }

        var rows = await query.OrderByDescending(orderBy).Take(take).ToListAsync(ct);
        return (rows.Select(map).ToList(), count);
    }

    private static string DescribeVitals(VitalSigns v)
    {
        var parts = new List<string>();
        if (v.SystolicBp is { } sys && v.DiastolicBp is { } dia)
        {
            parts.Add(FormattableString.Invariant($"BP {sys}/{dia}"));
        }

        if (v.Pulse is { } pulse)
        {
            parts.Add(FormattableString.Invariant($"Pulse {pulse}"));
        }

        if (v.SpO2 is { } spo2)
        {
            parts.Add(FormattableString.Invariant($"SpO₂ {spo2}%"));
        }

        if (v.TemperatureCelsius is { } temp)
        {
            parts.Add(FormattableString.Invariant($"Temp {temp:0.0} °C"));
        }

        if (v.RespiratoryRate is { } rr)
        {
            parts.Add(FormattableString.Invariant($"RR {rr}"));
        }

        return string.Join(", ", parts);
    }
}
