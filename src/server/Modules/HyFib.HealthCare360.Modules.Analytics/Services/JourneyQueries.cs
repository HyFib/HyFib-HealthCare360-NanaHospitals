using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;
using HyFib.HealthCare360.Modules.Analytics.Features.Journeys;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using BillingChargeStatus = HyFib.HealthCare360.Modules.Billing.Domain.ChargeStatus;
using OtChargeStatus = HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common.ChargeStatus;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>
/// Where each patient is in their out-patient visit, stay, operations and lab orders, derived from the records the
/// modules already keep: the visit and its token, the encounter, the order lines, the pharmacy queue, the charges, bills
/// and receipts, the bed history, the ward's rounds and notes, the discharge summary, the housekeeping task, the surgery
/// booking and the lab's tubes, results and report. Nothing here is set by hand, and nothing is written.
/// Like the Clinical timeline and the CRM 360° view it reads with small indexed queries, a few per kind of record for a
/// whole page of patients at once. Unlike the reports it reads the live database, not the reporting replica: staff act on
/// it. Every read stays in the user's facility, as the visit and admission lists do. Callers check the permission and,
/// for one patient, the restricted-record rules, and audit the read.
/// </summary>
public sealed class JourneyQueries(HospitalDbContext db, PatientAccessGuard guard, TimeProvider time)
{
    /// <summary>Operations and lab orders shown beside a patient's visit and stay, newest first.</summary>
    internal const int MaxSurgeries = 5;

    internal const int MaxLabOrders = 10;

    /// <summary>The order lines that are investigations: they complete when their report is released.</summary>
    private static readonly ServiceCategory[] InvestigationCategories = [ServiceCategory.Laboratory, ServiceCategory.Radiology, ServiceCategory.Cardiology];

    /// <summary>
    /// The patient's journeys: the current out-patient visit (open today, as the board has it) or else the most recent,
    /// the latest stay while it lasts (until the bed the patient left is clean), and the operations and lab orders of
    /// those, or still open. Operations are left out when <paramref name="includeSurgeries"/> is false: the record's
    /// sensitive category closes them to this user.
    /// </summary>
    public async Task<PatientJourneys> ForPatientAsync(Guid patientId, bool includeSurgeries, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = AnalyticsClock.StartOfDay(AnalyticsClock.Today(time));
        var visits = db.Set<Visit>().AsNoTracking().Where(v => v.PatientId == patientId && v.Type == VisitType.OutPatient).OrderByDescending(v => v.VisitedAt);
        var visit = await visits.Where(v => (v.Status == VisitStatus.Open || v.Status == VisitStatus.InProgress) && v.VisitedAt >= today).FirstOrDefaultAsync(ct)
                    ?? await visits.FirstOrDefaultAsync(ct);
        var stay = await db.Set<Admission>().AsNoTracking()
            .Where(a => a.PatientId == patientId)
            .OrderByDescending(a => a.AdmittedAt)
            .FirstOrDefaultAsync(ct);

        JourneyDto? outpatient = null;
        if (visit is not null)
        {
            var facts = await OutpatientFactsAsync([visit], ct);
            var titles = await VisitTitlesAsync([visit], ct);
            outpatient = Journey(JourneyKind.Outpatient, visit.Id, visit.VisitNumber, titles[visit.Id], visit.VisitedAt, OutpatientJourney.Progress(facts[visit.Id], now));
        }

        JourneyDto? inpatient = null;
        if (stay is not null)
        {
            var facts = await InpatientFactsAsync([stay], ct);
            var progress = InpatientJourney.Progress(facts[stay.Id], now);

            // The stay is current while the patient is in, and after discharge until the bed they left is clean.
            if (progress.Status == JourneyStatus.Active)
            {
                inpatient = Journey(JourneyKind.Inpatient, stay.Id, stay.AdmissionNumber, StayTitle(facts[stay.Id]), stay.AdmittedAt, progress);
            }
            else
            {
                stay = null;
            }
        }

        var visitIds = new List<Guid?>();
        if (visit is not null)
        {
            visitIds.Add(visit.Id);
        }

        if (stay is not null)
        {
            visitIds.Add(stay.VisitId);
        }

        var admissionId = stay?.Id;
        var surgeries = includeSurgeries ? await SurgeryJourneysAsync(patientId, visitIds, admissionId, now, ct) : [];
        var labOrders = await LabJourneysAsync(patientId, visitIds, admissionId, now, ct);
        return new PatientJourneys(patientId, now, outpatient, inpatient, surgeries, labOrders, SurgeriesWithheld: !includeSurgeries);
    }

    /// <summary>
    /// The patients in the hospital now: today's out-patient visits that are still open, and the stays of patients still
    /// in a bed (admitted, or with the discharge begun), newest arrival first. Each has its pipeline and the stage the
    /// patient is in. A restricted (VIP) patient's name shows only to those who may open the record (R-3).
    /// </summary>
    public async Task<PagedResult<InHospitalPatient>> InHospitalAsync(InHospitalFilter filter, PageRequest page, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = AnalyticsClock.StartOfDay(AnalyticsClock.Today(time));
        var visits = db.Set<Visit>().AsNoTracking()
            .Where(v => v.Type == VisitType.OutPatient && (v.Status == VisitStatus.Open || v.Status == VisitStatus.InProgress) && v.VisitedAt >= today);
        var stays = db.Set<Admission>().AsNoTracking()
            .Where(a => a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated);
        if (filter.DepartmentId is { } departmentId)
        {
            visits = visits.Where(v => v.DepartmentId == departmentId);
            stays = stays.Where(a => a.DepartmentId == departmentId);
        }

        if (filter.WardId is { } wardId)
        {
            stays = stays.Where(a => a.WardId == wardId);
        }

        // Each source is read on its own index for its newest (skip + take) arrivals and the two merged here, as the
        // Clinical timeline pages its sources: the page can only come from those rows. An out-patient has no ward.
        var depth = page.Skip + page.Take;
        long total = 0;
        List<Visit> visitRows = [];
        List<Admission> stayRows = [];
        if (filter.Kind != JourneyKind.Inpatient && filter.WardId is null)
        {
            total += await visits.LongCountAsync(ct);
            visitRows = await visits.OrderByDescending(v => v.VisitedAt).ThenBy(v => v.Id).Take(depth).ToListAsync(ct);
        }

        if (filter.Kind != JourneyKind.Outpatient)
        {
            total += await stays.LongCountAsync(ct);
            stayRows = await stays.OrderByDescending(a => a.AdmittedAt).ThenBy(a => a.Id).Take(depth).ToListAsync(ct);
        }

        var arrivals = visitRows.Select(v => (At: v.VisitedAt, Visit: (Visit?)v, Stay: (Admission?)null))
            .Concat(stayRows.Select(a => (At: a.AdmittedAt, Visit: (Visit?)null, Stay: (Admission?)a)))
            .OrderByDescending(r => r.At)
            .Skip(page.Skip)
            .Take(page.Take)
            .ToList();
        if (arrivals.Count == 0)
        {
            return new PagedResult<InHospitalPatient>([], page.Page, page.Take, total);
        }

        var pageVisits = arrivals.Where(r => r.Visit is not null).Select(r => r.Visit!).ToList();
        var pageStays = arrivals.Where(r => r.Stay is not null).Select(r => r.Stay!).ToList();
        var outpatient = await OutpatientFactsAsync(pageVisits, ct);
        var visitTitles = await VisitTitlesAsync(pageVisits, ct);
        var inpatient = await InpatientFactsAsync(pageStays, ct);
        var people = await PeopleAsync(arrivals.Select(r => r.Visit?.PatientId ?? r.Stay!.PatientId), ct);
        var departmentIds = arrivals.Select(r => r.Visit?.DepartmentId ?? r.Stay!.DepartmentId).OfType<Guid>().Distinct().ToList();
        var departments = await db.Set<Department>().AsNoTracking().Where(d => departmentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        string? DepartmentName(Guid? id) => id is { } key ? departments.GetValueOrDefault(key) : null;

        var items = arrivals.Select(r =>
        {
            if (r.Stay is { } a)
            {
                var stay = inpatient[a.Id];
                var patient = people.GetValueOrDefault(a.PatientId, Person.Unknown);
                return new InHospitalPatient(
                    a.PatientId, patient.Uhid, patient.Name, patient.Restricted, patient.Gender, patient.Age, JourneyKind.Inpatient, a.DepartmentId,
                    DepartmentName(a.DepartmentId), a.WardId, stay.Bed, a.AdmittedAt,
                    Journey(JourneyKind.Inpatient, a.Id, a.AdmissionNumber, StayTitle(stay), a.AdmittedAt, InpatientJourney.Progress(stay, now)));
            }

            var v = r.Visit!;
            var visit = outpatient[v.Id];
            var person = people.GetValueOrDefault(v.PatientId, Person.Unknown);
            return new InHospitalPatient(
                v.PatientId, person.Uhid, person.Name, person.Restricted, person.Gender, person.Age, JourneyKind.Outpatient, v.DepartmentId,
                DepartmentName(v.DepartmentId), null, visit.Token is { } token ? $"Token {token.Label}" : null, v.VisitedAt,
                Journey(JourneyKind.Outpatient, v.Id, v.VisitNumber, visitTitles[v.Id], v.VisitedAt, OutpatientJourney.Progress(visit, now)));
        }).ToList();

        return new PagedResult<InHospitalPatient>(items, page.Page, page.Take, total);
    }

    private static JourneyDto Journey(JourneyKind kind, Guid id, string number, string title, DateTimeOffset startedAt, JourneyProgress progress) =>
        new(kind, id, number, title, progress.Status, startedAt, progress.Stages, progress.Current);

    // ------------------------------------------------------------------ out-patient visits

    private async Task<Dictionary<Guid, OutpatientFacts>> OutpatientFactsAsync(IReadOnlyCollection<Visit> visits, CancellationToken ct)
    {
        if (visits.Count == 0)
        {
            return [];
        }

        var ids = visits.Select(v => v.Id).ToList();
        var visitIds = ids.Select(id => (Guid?)id).ToList();
        var patientIds = visits.Select(v => v.PatientId).Distinct().ToList();

        // The desk issues one token a visit; were there more, the latest counts.
        var tokens = (await db.Set<QueueToken>().AsNoTracking()
                .Where(t => ids.Contains(t.VisitId))
                .Select(t => new { t.VisitId, t.Label, t.CalledAt, t.ServiceStartedAt, t.Room, t.IssuedAt })
                .ToListAsync(ct))
            .GroupBy(t => t.VisitId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.IssuedAt).Select(t => new TokenFact(t.Label, t.CalledAt, t.ServiceStartedAt, t.Room)).First());
        var encounters = (await db.Set<Encounter>().AsNoTracking()
                .Where(e => visitIds.Contains(e.VisitId))
                .Select(e => new { e.VisitId, e.StartedAt, e.SignedAt })
                .ToListAsync(ct))
            .ToLookup(e => e.VisitId!.Value, e => new EncounterFact(e.StartedAt, e.SignedAt));
        var investigations = (await InvestigationsAsync(db.Set<ClinicalOrder>().Where(o => visitIds.Contains(o.VisitId)), ct))
            .ToLookup(l => l.VisitId!.Value, l => l.Fact);

        // The pharmacy queue is read by patient (its index) and matched to the visit; refills are later visits' business.
        var prescriptions = (await db.Set<PharmacyQueueEntry>().AsNoTracking()
                .Where(e => patientIds.Contains(e.PatientId) && visitIds.Contains(e.VisitId) && !e.IsInpatient && e.RefillOfEntryId == null
                            && e.Status != QueueEntryStatus.Cancelled)
                .Select(e => new { e.VisitId, e.Status, e.LastDispensedAt })
                .ToListAsync(ct))
            .ToLookup(e => e.VisitId!.Value, e => new DispensingFact(e.Status, e.LastDispensedAt));
        var billing = await VisitBillingAsync(visitIds, ct);

        return visits.ToDictionary(v => v.Id, v => new OutpatientFacts(
            v.Status,
            v.VisitedAt,
            v.CompletedAt,
            tokens.GetValueOrDefault(v.Id),
            [.. encounters[v.Id]],
            [.. investigations[v.Id]],
            [.. prescriptions[v.Id]],
            billing.GetValueOrDefault(v.Id, BillingFacts.None)));
    }

    /// <summary>"General medicine · Dr. Meera Iyer": the department and the doctor of each visit.</summary>
    private async Task<Dictionary<Guid, string>> VisitTitlesAsync(IReadOnlyCollection<Visit> visits, CancellationToken ct)
    {
        var departmentIds = visits.Where(v => v.DepartmentId is not null).Select(v => v.DepartmentId!.Value).Distinct().ToList();
        var practitionerIds = visits.Where(v => v.PractitionerId is not null).Select(v => v.PractitionerId!.Value).Distinct().ToList();
        var departments = await db.Set<Department>().AsNoTracking().Where(d => departmentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var doctors = await db.Set<Practitioner>().AsNoTracking().Where(p => practitionerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
        return visits.ToDictionary(v => v.Id, v =>
        {
            var parts = new[]
            {
                v.DepartmentId is { } d ? departments.GetValueOrDefault(d) : null,
                v.PractitionerId is { } p ? doctors.GetValueOrDefault(p) : null,
            }.Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
            return parts.Count == 0 ? "Out-patient visit" : string.Join(" · ", parts);
        });
    }

    /// <summary>The visits' charges that are not cancelled, those not yet on a finalized bill, and the finalized bills holding the rest.</summary>
    private async Task<Dictionary<Guid, BillingFacts>> VisitBillingAsync(List<Guid?> visitIds, CancellationToken ct)
    {
        var charges = await db.Set<Charge>().AsNoTracking()
            .Where(c => visitIds.Contains(c.VisitId) && c.Status != BillingChargeStatus.Cancelled)
            .Select(c => new { c.VisitId, c.Status, c.BillId })
            .ToListAsync(ct);
        var billIds = charges.Where(c => c.Status == BillingChargeStatus.Billed && c.BillId is not null).Select(c => c.BillId!.Value).Distinct().ToList();
        var bills = await BillsAsync(db.Set<Bill>().Where(b => billIds.Contains(b.Id)), ct);
        return charges.GroupBy(c => c.VisitId!.Value).ToDictionary(g => g.Key, g => new BillingFacts(
            g.Count(),
            g.Count(c => c.Status == BillingChargeStatus.Unbilled),
            [.. g.Where(c => c.Status == BillingChargeStatus.Billed && c.BillId is not null).Select(c => c.BillId!.Value).Distinct()
                .Where(bills.ContainsKey).Select(id => bills[id].Fact)]));
    }

    // ------------------------------------------------------------------ stays

    private async Task<Dictionary<Guid, InpatientFacts>> InpatientFactsAsync(IReadOnlyCollection<Admission> stays, CancellationToken ct)
    {
        if (stays.Count == 0)
        {
            return [];
        }

        var ids = stays.Select(a => a.Id).ToList();
        var admissionIds = ids.Select(id => (Guid?)id).ToList();
        var stayVisits = stays.ToDictionary(a => a.VisitId, a => a.Id);
        var visitIds = stayVisits.Keys.Select(id => (Guid?)id).ToList();

        var beddedAt = await db.Set<BedAssignment>().AsNoTracking()
            .Where(b => ids.Contains(b.AdmissionId))
            .GroupBy(b => b.AdmissionId)
            .Select(g => new { AdmissionId = g.Key, From = g.Min(b => b.FromAt) })
            .ToDictionaryAsync(x => x.AdmissionId, x => x.From, ct);
        var wardIds = stays.Where(a => a.WardId is not null).Select(a => a.WardId!.Value).Distinct().ToList();
        var bedIds = stays.Where(a => a.BedId is not null).Select(a => a.BedId!.Value).Distinct().ToList();
        var wards = await db.Set<Ward>().AsNoTracking().Where(w => wardIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var beds = await db.Set<Bed>().AsNoTracking().Where(b => bedIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Label, ct);

        // The OPD or casualty visit each stay comes from, as the admission links it.
        var sourceIds = stays.Where(a => a.SourceVisitId is not null).Select(a => a.SourceVisitId!.Value).Distinct().ToList();
        var sources = await db.Set<Visit>().AsNoTracking().Where(v => sourceIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.VisitNumber, ct);

        // Rounds and nursing begin with the first consultant's round, nursing note or risk assessment, whichever came first.
        var rounds = await db.Set<ConsultantVisit>().AsNoTracking()
            .Where(v => ids.Contains(v.AdmissionId))
            .GroupBy(v => v.AdmissionId)
            .Select(g => new { AdmissionId = g.Key, First = g.Min(v => v.VisitedAt) })
            .ToDictionaryAsync(x => x.AdmissionId, x => x.First, ct);
        var notes = await db.Set<NursingNote>().AsNoTracking()
            .Where(n => ids.Contains(n.AdmissionId))
            .GroupBy(n => n.AdmissionId)
            .Select(g => new { AdmissionId = g.Key, First = g.Min(n => n.NoteFor) })
            .ToDictionaryAsync(x => x.AdmissionId, x => x.First, ct);
        var assessments = await db.Set<NursingAssessment>().AsNoTracking()
            .Where(n => ids.Contains(n.AdmissionId))
            .GroupBy(n => n.AdmissionId)
            .Select(g => new { AdmissionId = g.Key, First = g.Min(n => n.AssessedAt) })
            .ToDictionaryAsync(x => x.AdmissionId, x => x.First, ct);

        // Orders of the stay name the admission; an order placed from the stay's visit alone still belongs to it.
        var investigations = (await InvestigationsAsync(
                db.Set<ClinicalOrder>().Where(o => admissionIds.Contains(o.AdmissionId) || visitIds.Contains(o.VisitId)), ct))
            .ToLookup(l => l.AdmissionId is { } id && ids.Contains(id) ? id : stayVisits[l.VisitId!.Value], l => l.Fact);
        var summaries = await db.Set<DischargeSummary>().AsNoTracking()
            .Where(s => ids.Contains(s.AdmissionId))
            .Select(s => new { s.AdmissionId, s.SignedAt })
            .ToDictionaryAsync(s => s.AdmissionId, s => s.SignedAt, ct);
        var clearance = await ClearanceAsync(ids, admissionIds, ct);
        var cleaning = await CleaningAsync(stays.Where(a => a.DischargedAt is not null).ToList(), ct);

        return stays.ToDictionary(a => a.Id, a => new InpatientFacts(
            a.Status,
            a.AdmittedAt,
            a.Source,
            a.SourceVisitId is { } sourceId ? sources.GetValueOrDefault(sourceId) : null,
            beddedAt.TryGetValue(a.Id, out var from) ? from : null,
            Place(a.WardId is { } w ? wards.GetValueOrDefault(w) : null, a.BedId is { } b ? beds.GetValueOrDefault(b) : null),
            Facts.Earliest(Value(rounds, a.Id), Value(notes, a.Id), Value(assessments, a.Id)),
            [.. investigations[a.Id]],
            a.DischargeInitiatedAt,
            summaries.GetValueOrDefault(a.Id),
            SummaryDrafted: summaries.TryGetValue(a.Id, out var signedAt) && signedAt is null,
            clearance.GetValueOrDefault(a.Id, ClearanceFacts.None),
            a.DischargedAt,
            a.DischargeType,
            cleaning.GetValueOrDefault(a.Id)));
    }

    /// <summary>Billing's position on each stay, as Billing reads it to clear a discharge.</summary>
    private async Task<Dictionary<Guid, ClearanceFacts>> ClearanceAsync(List<Guid> ids, List<Guid?> admissionIds, CancellationToken ct)
    {
        var charges = await db.Set<Charge>().AsNoTracking()
            .Where(c => admissionIds.Contains(c.AdmissionId) && c.Status != BillingChargeStatus.Cancelled)
            .GroupBy(c => c.AdmissionId)
            .Select(g => new { AdmissionId = g.Key!.Value, Unbilled = g.Count(c => c.Status == BillingChargeStatus.Unbilled) })
            .ToDictionaryAsync(x => x.AdmissionId, x => x.Unbilled, ct);
        var bills = await BillsAsync(db.Set<Bill>().Where(b => admissionIds.Contains(b.AdmissionId)), ct);
        var approvals = await db.Set<IpAccount>().AsNoTracking()
            .Where(a => ids.Contains(a.AdmissionId) && a.DischargeWithDuesApproved)
            .Select(a => new { a.AdmissionId, a.DischargeApprovedAt })
            .ToDictionaryAsync(a => a.AdmissionId, a => a.DischargeApprovedAt, ct);
        return ids.ToDictionary(id => id, id => new ClearanceFacts(
            charges.ContainsKey(id),
            charges.GetValueOrDefault(id),
            [.. bills.Values.Where(b => b.AdmissionId == id).Select(b => b.Fact)],
            approvals.GetValueOrDefault(id)));
    }

    /// <summary>The cleaning of the bed each patient left at discharge (a task cancelled when the bed went for maintenance is no cleaning).</summary>
    private async Task<Dictionary<Guid, CleaningFact>> CleaningAsync(List<Admission> left, CancellationToken ct)
    {
        if (left.Count == 0)
        {
            return [];
        }

        // Read by the beds (their index), then matched to the stays.
        var bedIds = left.Where(a => a.BedId is not null).Select(a => a.BedId!.Value).Distinct().ToList();
        var ids = left.Select(a => (Guid?)a.Id).ToList();
        return (await db.Set<HousekeepingTask>().AsNoTracking()
                .Where(t => bedIds.Contains(t.BedId) && ids.Contains(t.AdmissionId) && t.Trigger == HousekeepingTrigger.Discharge
                            && t.Status != HousekeepingStatus.Cancelled)
                .Select(t => new { t.AdmissionId, t.RequestedAt, t.CompletedAt })
                .ToListAsync(ct))
            .GroupBy(t => t.AdmissionId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.RequestedAt).Select(t => new CleaningFact(t.RequestedAt, t.CompletedAt)).First());
    }

    private static string StayTitle(InpatientFacts facts) => facts.Bed ?? "In-patient stay";

    // ------------------------------------------------------------------ operations

    private async Task<List<JourneyDto>> SurgeryJourneysAsync(Guid patientId, List<Guid?> visitIds, Guid? admissionId, DateTimeOffset now, CancellationToken ct)
    {
        var bookings = await db.Set<SurgeryBooking>().AsNoTracking()
            .Where(b => b.PatientId == patientId
                        && ((b.Status != SurgeryStatus.Completed && b.Status != SurgeryStatus.Cancelled)
                            || visitIds.Contains(b.VisitId)
                            || (admissionId != null && b.AdmissionId == admissionId)))
            .OrderByDescending(b => b.CreatedAt)
            .Take(MaxSurgeries)
            .ToListAsync(ct);
        var theatreIds = bookings.Where(b => b.TheatreId is not null).Select(b => b.TheatreId!.Value).Distinct().ToList();
        var theatres = await db.Set<OperationTheatre>().AsNoTracking().Where(t => theatreIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Code, ct);

        return [.. bookings.Select(b => Journey(JourneyKind.Surgery, b.Id, b.BookingNumber, Procedure(b), b.CreatedAt, SurgeryJourney.Progress(new SurgeryFacts(
            b.Status, b.CreatedAt, b.ScheduledStart, b.TheatreId is { } t ? theatres.GetValueOrDefault(t) : null, b.PostponeReason, b.PreOpChecks,
            b.ReceivedInPreOpAt, b.SignInCompletedAt, b.TimeOutCompletedAt, b.StartedAt, b.SignOutCompletedAt, b.CompletedAt,
            ChargesFailed: b.ChargeStatus == OtChargeStatus.Failed), now)))];
    }

    /// <summary>"Total knee replacement (left)".</summary>
    private static string Procedure(SurgeryBooking b) =>
        b.Laterality == Laterality.NotApplicable ? b.ProcedureName : $"{b.ProcedureName} ({b.Laterality.ToString().ToLowerInvariant()})";

    // ------------------------------------------------------------------ lab orders

    private async Task<List<JourneyDto>> LabJourneysAsync(Guid patientId, List<Guid?> visitIds, Guid? admissionId, DateTimeOffset now, CancellationToken ct)
    {
        var orders = await db.Set<LabOrder>().AsNoTracking()
            .Where(o => o.PatientId == patientId
                        && (o.Status == LabOrderStatus.Ordered || o.Status == LabOrderStatus.InProgress
                            || visitIds.Contains(o.VisitId)
                            || (admissionId != null && o.AdmissionId == admissionId)))
            .OrderByDescending(o => o.OrderedAt)
            .Take(MaxLabOrders)
            .Select(o => new { o.Id, o.AccessionNumber, o.OrderedAt, o.Status, o.PatientClass, o.Source, o.VisitId })
            .ToListAsync(ct);
        if (orders.Count == 0)
        {
            return [];
        }

        var orderIds = orders.Select(o => o.Id).ToList();
        var items = await db.Set<LabOrderItem>().AsNoTracking()
            .Where(i => orderIds.Contains(i.LabOrderId) && i.Status != LabOrderItemStatus.Cancelled)
            .Select(i => new { i.Id, i.LabOrderId, i.TestName, i.SampleId, i.PackageBookingId, i.SentOutAt, i.VerifiedAt, i.ReleasedAt, i.Status })
            .ToListAsync(ct);
        var sampleIds = items.Where(i => i.SampleId is not null).Select(i => i.SampleId!.Value).Distinct().ToList();
        var samples = await db.Set<LabSample>().AsNoTracking()
            .Where(s => sampleIds.Contains(s.Id))
            .Select(s => new { s.Id, s.CollectedAt, s.ReceivedAt })
            .ToDictionaryAsync(s => s.Id, ct);

        // A test is resulted once none of its results is still to be entered; read by the accession (its index).
        var results = await db.Set<LabResult>().AsNoTracking()
            .Where(r => orderIds.Contains(r.LabOrderId))
            .GroupBy(r => r.LabOrderItemId)
            .Select(g => new
            {
                ItemId = g.Key,
                Pending = g.Count(r => r.Status == LabResultStatus.Pending),
                EnteredAt = g.Max(r => r.EnteredAt),
                Manual = g.Count(r => !r.AutoValidated),
            })
            .ToDictionaryAsync(r => r.ItemId, ct);
        var paid = await LabTestsPaidAsync([.. items.Where(i => i.PackageBookingId is null).Select(i => i.Id)], ct);
        var reports = await db.Set<LabReport>().AsNoTracking()
            .Where(r => orderIds.Contains(r.LabOrderId))
            .Select(r => new { r.LabOrderId, r.Id })
            .ToDictionaryAsync(r => r.LabOrderId, r => r.Id, ct);
        var reportIds = reports.Values.ToList();

        // The review is the ordering doctor's, kept against the report; read whatever facility the order came from.
        var reviews = await db.Set<ResultReview>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => reportIds.Contains(r.ReportId))
            .Select(r => new { r.ReportId, r.ReviewedAt })
            .ToDictionaryAsync(r => r.ReportId, r => r.ReviewedAt, ct);

        // The victim of a sexual offence or an acid attack is treated free of cost (BNSS 2023, s.397): nothing is paid first.
        var orderVisitIds = orders.Where(o => o.VisitId is not null).Select(o => o.VisitId!.Value).Distinct().ToList();
        var freeVisits = (await db.Set<Visit>().AsNoTracking()
            .Where(v => orderVisitIds.Contains(v.Id) && v.FreeTreatmentReason != null)
            .Select(v => v.Id)
            .ToListAsync(ct)).ToHashSet();

        var tests = items.ToLookup(i => i.LabOrderId);
        return [.. orders.Select(o =>
        {
            var facts = new LabFacts(
                o.Status,
                o.OrderedAt,
                o.PatientClass.PaysBeforeService() && !(o.VisitId is { } visitId && freeVisits.Contains(visitId)),
                o.Source == LabOrderSource.ClinicalOrder,
                [.. tests[o.Id].Select(i =>
                {
                    var sample = i.SampleId is { } sampleId ? samples.GetValueOrDefault(sampleId) : null;
                    var result = results.GetValueOrDefault(i.Id);
                    var resulted = i.Status is LabOrderItemStatus.ResultsEntered or LabOrderItemStatus.Verified or LabOrderItemStatus.Released
                                   || (result is { Pending: 0 } && result.EnteredAt is not null);
                    var payment = paid.GetValueOrDefault(i.Id);
                    return new LabTestFact(
                        i.PackageBookingId is not null,
                        payment.Paid,
                        payment.At,
                        sample?.CollectedAt,
                        sample?.ReceivedAt ?? i.SentOutAt,
                        resulted ? result?.EnteredAt : null,
                        i.VerifiedAt,
                        result is { Manual: 0 } && i.VerifiedAt is not null,
                        i.ReleasedAt);
                })],
                reports.TryGetValue(o.Id, out var reportId) ? reviews.GetValueOrDefault(reportId) : null);
            return Journey(JourneyKind.Lab, o.Id, o.AccessionNumber, Tests([.. tests[o.Id].Select(i => i.TestName)]), o.OrderedAt, LabJourney.Progress(facts, now));
        })];
    }

    /// <summary>Whether each test's charge is on a paid bill, and when that bill was settled, as the collection payment gate reads it.</summary>
    private async Task<Dictionary<Guid, (bool Paid, DateTimeOffset? At)>> LabTestsPaidAsync(List<Guid> itemIds, CancellationToken ct)
    {
        if (itemIds.Count == 0)
        {
            return [];
        }

        var references = itemIds.Select(id => id.ToString()).ToList();
        var charges = await db.Set<Charge>().AsNoTracking()
            .Where(c => c.SourceModule == ChargeSources.Lab && references.Contains(c.SourceReference) && c.Status == BillingChargeStatus.Billed)
            .Select(c => new { c.SourceReference, c.BillId })
            .ToListAsync(ct);
        var billIds = charges.Where(c => c.BillId is not null).Select(c => c.BillId!.Value).Distinct().ToList();
        var bills = await BillsAsync(db.Set<Bill>().Where(b => billIds.Contains(b.Id) && b.Status == BillStatus.Paid), ct);
        return charges
            .Where(c => c.BillId is { } billId && bills.ContainsKey(billId))
            .GroupBy(c => Guid.Parse(c.SourceReference))
            .ToDictionary(g => g.Key, g => (true, Facts.Latest(g.Select(c => bills[c.BillId!.Value].Fact.SettledAt))));
    }

    /// <summary>"CBC, LFT, Lipid profile and 2 more".</summary>
    private static string Tests(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "Lab tests",
        <= 3 => string.Join(", ", names),
        _ => $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more",
    };

    // ------------------------------------------------------------------ shared

    private sealed record LineFact(Guid? VisitId, Guid? AdmissionId, InvestigationFact Fact);

    /// <summary>The investigation lines (lab, imaging, cardiology) of the orders, cancelled lines left out.</summary>
    private async Task<List<LineFact>> InvestigationsAsync(IQueryable<ClinicalOrder> orders, CancellationToken ct) =>
        (await orders.AsNoTracking()
            .SelectMany(o => o.Items, (o, i) => new { o.VisitId, o.AdmissionId, i.Category, i.Status, i.CompletedAt })
            .Where(l => InvestigationCategories.Contains(l.Category) && l.Status != OrderItemStatus.Cancelled)
            .ToListAsync(ct))
        .Select(l => new LineFact(l.VisitId, l.AdmissionId, new InvestigationFact(l.Status == OrderItemStatus.Completed ? l.CompletedAt : null)))
        .ToList();

    private sealed record BillRow(Guid Id, Guid? AdmissionId, BillFact Fact);

    /// <summary>The finalized bills among <paramref name="bills"/> (drafts, cancelled and refunded bills left out), with their last payment.</summary>
    private async Task<Dictionary<Guid, BillRow>> BillsAsync(IQueryable<Bill> bills, CancellationToken ct)
    {
        var rows = await bills.AsNoTracking()
            .Where(b => b.Status != BillStatus.Draft && b.Status != BillStatus.Cancelled && b.Status != BillStatus.Refunded)
            .Select(b => new { b.Id, b.AdmissionId, b.FinalizedAt, b.DueAmount, b.IsFinalBill })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var billIds = rows.Select(b => (Guid?)b.Id).ToList();
        var payments = await db.Set<Receipt>().AsNoTracking()
            .Where(r => billIds.Contains(r.BillId) && r.Purpose == ReceiptPurpose.BillPayment)
            .GroupBy(r => r.BillId)
            .Select(g => new { BillId = g.Key!.Value, Last = g.Max(r => r.ReceivedAt) })
            .ToDictionaryAsync(p => p.BillId, p => p.Last, ct);
        return rows.ToDictionary(b => b.Id, b => new BillRow(
            b.Id, b.AdmissionId, new BillFact(b.FinalizedAt, b.DueAmount, payments.TryGetValue(b.Id, out var last) ? last : null, b.IsFinalBill)));
    }

    private sealed record Person(string Uhid, string Name, bool Restricted, Gender Gender, string? Age)
    {
        /// <summary>For a row whose patient the register does not return (a deleted record): the row still shows, unnamed.</summary>
        public static readonly Person Unknown = new(string.Empty, "Patient not found", false, Gender.Unknown, null);
    }

    /// <summary>The patients' names; a restricted (VIP) patient the user may not open shows initials, as patient search does (R-3).</summary>
    private async Task<Dictionary<Guid, Person>> PeopleAsync(IEnumerable<Guid> patientIds, CancellationToken ct)
    {
        var ids = patientIds.Distinct().ToList();
        var patients = await db.Set<Patient>().AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Uhid, p.FullName, p.Gender, p.DateOfBirth, p.IsVip })
            .ToListAsync(ct);
        var openable = await guard.OpenableAsync([.. patients.Where(p => p.IsVip).Select(p => p.Id)], ct);
        var today = time.Today();
        return patients.ToDictionary(p => p.Id, p =>
        {
            var restricted = p.IsVip && !openable.Contains(p.Id);
            return new Person(p.Uhid, restricted ? PatientNames.Restricted(p.FullName) : p.FullName, restricted, p.Gender,
                p.DateOfBirth is { } dob ? Age.Format(dob, today) : null);
        });
    }

    /// <summary>"General Ward — Male · GWM-07".</summary>
    private static string? Place(string? ward, string? bed) =>
        ward is null && bed is null ? null : string.Join(" · ", new[] { ward, bed }.Where(part => part is not null));

    private static DateTimeOffset? Value(Dictionary<Guid, DateTimeOffset> times, Guid id) => times.TryGetValue(id, out var at) ? at : null;
}
