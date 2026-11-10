using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using Theatre = HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre.OperationTheatre;

namespace HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;

// ClosedXML brings the Open XML SDK's global DocumentFormat namespace, which hides the document layer's DocumentFormat;
// an alias inside the namespace is found first.
using DocumentFormat = HyFib.HealthCare360.Platform.Documents.DocumentFormat;

/// <summary>
/// The OT register (OT-08): every patient taken into an operation theatre in the period, one row each, in the columns and
/// the order of the paper register the hospital keeps. Nothing in it is typed for the register: each column is read from
/// the record it belongs to (the booking's times, the operation note, the anaesthesia record, the practitioner master, the
/// stay's beds), so the register and the chart cannot disagree. Patient-level: every view and export is audited per patient.
/// </summary>
internal sealed class OtRegisterReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "ot-register",
        "OT register",
        ReportCategory.SpecialtyCare,
        "Every patient taken into an operation theatre, in the columns of the paper OT register: times, patient, diagnoses, surgery, anaesthesia, the team with qualifications and registration numbers, where the patient went after and the signatures.",
        "Patients taken into a theatre in the period (WHO Sign-in, or the start of surgery if Sign-in was not recorded), in the order they went in; " +
        "a case cancelled after Sign-in stays on the register with its reason. Time to = out of the theatre. " +
        "Surgery, diagnoses and findings are those of the operation note, or the booking's while the note is not written. " +
        "Anaesthesia and drugs are those of the anaesthesia record, or the booking's planned technique while there is none. " +
        "Transferred to = the ward of the first bed the patient moved to within 6 hours of leaving the theatre, or the bed held when they left. " +
        "Qualification and registration number come from the practitioner master; the signatures are those of the operation note and the anaesthesia record. " +
        "Contact details show as the viewer may see them; a restricted patient's name shows as initials, and the clinical columns of a confidential record are withheld from staff not allowed to open it. " +
        "Lists patient identifiers: opening or exporting it is recorded in the audit trail for each patient.",
        Audience.Clinical,
        [P.Period(), P.Facility(), P.Theatre()],
        [
            C.Int("serial", "S.No"),
            C.Text("caseNumber", "OT case no."),
            C.Text("theatre", "OT"),
            C.Date("date", "Date"),
            C.Text("timeFrom", "Time from"),
            C.Text("timeTo", "Time to"),
            C.Text("duration", "Duration"),
            C.Text("ipNumber", "IP no."),
            C.Patient("uhid", "UHID"),
            C.Text("transferredTo", "Transferred to"),
            C.Patient("patient", "Name"),
            C.Text("ageSex", "Age / sex"),
            C.Text("address", "Address"),
            C.Text("idNumber", "ID no."),
            C.Text("mobile", "Mobile no."),
            C.Text("provisionalDiagnosis", "Provisional diagnosis"),
            C.Text("surgery", "Name of the surgery"),
            C.Text("finalDiagnosis", "Final diagnosis & operating notes"),
            C.Text("anaesthesia", "Anaesthesia given"),
            C.Text("drugs", "Drug name & dose"),
            C.Text("surgeon", "Surgeon"),
            C.Text("anaesthetist", "Anaesthetist"),
            C.Text("assistants", "Assisting doctor"),
            C.Text("nurse", "Assisting nurse / OT in-charge"),
            C.Text("remarks", "Remarks"),
        ],
        null,
        DefaultSort: "serial",
        ContainsPatientIdentifiers: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var theatreId = ctx.Args.TheatreId;

        // One query per source, each keeping its own filters: the booking's facility, and a merged patient's removed row read
        // past its soft-delete filter (as the deaths register reads it) without lifting any other source's.
        var cases = await ctx.ListAsync(
            ctx.Db.Set<SurgeryBooking>().AsNoTracking()
                .Where(b => (b.InRoomAt ?? b.StartedAt) >= start && (b.InRoomAt ?? b.StartedAt) < end && (theatreId == null || b.TheatreId == theatreId))
                .OrderBy(b => b.InRoomAt ?? b.StartedAt).ThenBy(b => b.BookingNumber)
                .Select(b => new OtRegister.Case(
                    b.Id, b.PatientId, b.AdmissionId, b.BookingNumber, b.TheatreId, b.Status, b.Priority, (b.InRoomAt ?? b.StartedAt)!.Value, b.CompletedAt,
                    b.ProcedureName, b.Laterality, b.Diagnosis, b.SurgeonId, b.AssistantIds, b.AnaesthetistId, b.AnaesthesiaType, b.CancelReason,
                    b.NurseId)),
            ct);

        var bookingIds = cases.Select(c => c.Id).ToList();
        var notes = (await ctx.LookupAsync(
                ctx.Db.Set<OperationNote>().AsNoTracking().Where(n => bookingIds.Contains(n.SurgeryBookingId))
                    .Select(n => new OtRegister.Note(
                        n.SurgeryBookingId, n.PreOperativeDiagnosis, n.PostOperativeDiagnosis, n.ProcedurePerformed, n.Findings, n.Complications, n.SignedAt,
                        n.SignedByName)),
                ct))
            .GroupBy(n => n.BookingId).ToDictionary(g => g.Key, g => g.First());
        var anaesthesia = (await ctx.LookupAsync(
                ctx.Db.Set<AnaesthesiaRecord>().AsNoTracking().Where(r => bookingIds.Contains(r.SurgeryBookingId))
                    .Select(r => new OtRegister.Anaesthesia(r.SurgeryBookingId, r.AnaesthetistId, r.Technique, r.Drugs, r.Complications, r.SignedAt, r.SignedByName)),
                ct))
            .GroupBy(r => r.BookingId).ToDictionary(g => g.Key, g => g.First());

        var patientIds = cases.Select(c => c.PatientId).Distinct().ToList();
        var patients = (await ctx.LookupAsync(
                ctx.Db.Set<Patient>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking().Where(p => patientIds.Contains(p.Id))
                    // The address field by field: the whole complex type would load the whole row, ABHA cipher and all.
                    .Select(p => new OtRegister.Person(
                        p.Id, p.Uhid, p.FullName, p.Gender, p.DateOfBirth,
                        new Address
                        {
                            Line1 = p.Address.Line1,
                            Line2 = p.Address.Line2,
                            City = p.Address.City,
                            District = p.Address.District,
                            State = p.Address.State,
                            PinCode = p.Address.PinCode,
                        },
                        p.Phone, p.AadhaarLast4, p.AbhaNumberMasked, p.IsVip, p.SensitiveCategories)),
                ct))
            .ToDictionary(p => p.Id);

        var admissionIds = cases.Select(c => c.AdmissionId).OfType<Guid>().Distinct().ToList();
        var admissions = (await ctx.LookupAsync(
                ctx.Db.Set<Admission>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking().Where(a => admissionIds.Contains(a.Id))
                    .Select(a => new { a.Id, a.AdmissionNumber }),
                ct))
            .ToDictionary(a => a.Id, a => a.AdmissionNumber);
        var beds = (await ctx.LookupAsync(
                ctx.Db.Set<BedAssignment>().AsNoTracking().Where(a => admissionIds.Contains(a.AdmissionId))
                    .Select(a => new OtRegister.BedStay(a.AdmissionId, a.WardId, a.FromAt, a.ToAt)),
                ct))
            .ToLookup(b => b.AdmissionId);
        var wards = await Names.WardsAsync(ctx, beds.SelectMany(g => g).Select(b => (Guid?)b.WardId), ct);
        var theatres = await TheatreCodesAsync(ctx, cases.Select(c => c.TheatreId), ct);

        var clinicianIds = cases.SelectMany(c => c.AssistantIds.Select(id => (Guid?)id).Append(c.SurgeonId).Append(c.AnaesthetistId).Append(c.NurseId))
            .Concat(anaesthesia.Values.Select(r => r.AnaesthetistId)).OfType<Guid>().Distinct().ToList();
        var clinicians = (await ctx.LookupAsync(
                ctx.Db.Set<Practitioner>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking().Where(p => clinicianIds.Contains(p.Id))
                    .Select(p => new OtRegister.Clinician(p.Id, p.DisplayName, p.Qualification, p.RegistrationCouncil, p.RegistrationNumber)),
                ct))
            .ToDictionary(p => p.Id);

        var viewer = new OtRegister.Viewer(
            ctx.ViewerHas(PatientsPermissions.ContactRead),
            ctx.ViewerHas(PatientsPermissions.ReadRestricted),
            PatientAccessGuard.CategoryPermissions.Where(p => ctx.ViewerHas(p.Permission)).Aggregate(SensitiveCategory.None, (held, p) => held | p.Category));

        var rows = new List<object?[]>(cases.Count);
        var unsigned = 0;
        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            var note = notes.GetValueOrDefault(c.Id);
            var record = anaesthesia.GetValueOrDefault(c.Id);
            var patient = patients.GetValueOrDefault(c.PatientId);
            var admission = c.AdmissionId is { } admissionId ? admissions.GetValueOrDefault(admissionId) : null;
            var ward = c.AdmissionId is { } stay && c.CompletedAt is { } left ? OtRegister.WardAfter([.. beds[stay]], c.EnteredAt, left) : null;
            var transferred = c.AdmissionId is null ? "Not admitted" : ward is { } wardId ? Labels.Name(wards, wardId) : OtRegister.None;
            if (OtRegister.LacksSignedRecords(c, note, record))
            {
                unsigned++;
            }

            rows.Add(OtRegister.Row(i + 1, c, theatres.GetValueOrDefault(c.TheatreId ?? Guid.Empty), admission, transferred, patient, note, record, clinicians, viewer));
        }

        return new ReportData(rows)
        {
            Summary =
            [
                new("cases", "Patients taken into theatre", cases.Count, ReportValueType.Integer),
                new("completed", "Operations completed", cases.Count(c => c.Status == SurgeryStatus.Completed), ReportValueType.Integer),
                new("emergency", "Emergency operations", cases.Count(c => c.Priority == SurgeryPriority.Emergency), ReportValueType.Integer),
                new("unsigned", "Completed without a signed note or anaesthesia record", unsigned, ReportValueType.Integer),
            ],
            RowPatientIds = cases.Select(c => (Guid?)c.PatientId).ToList(),
        };
    }

    private static async Task<Dictionary<Guid, string>> TheatreCodesAsync(ReportContext ctx, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return (await ctx.LookupAsync(
                ctx.Db.Set<Theatre>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                    .Where(t => list.Contains(t.Id)).Select(t => new { t.Id, t.Code }),
                ct))
            .ToDictionary(t => t.Id, t => t.Code);
    }
}

/// <summary>How one case of the OT register is written: the rules apart from the queries, so they are tested on their own.</summary>
internal static class OtRegister
{
    public const string None = "—";

    /// <summary>What a clinical column shows to someone the patient's record is closed to: no word of which category it is in.</summary>
    public const string Withheld = "Withheld: confidential record";

    /// <summary>How long after leaving the theatre a move to another bed is still where the theatre sent the patient.</summary>
    public static readonly TimeSpan PostOperativeMove = TimeSpan.FromHours(6);

    public sealed record Case(
        Guid Id, Guid PatientId, Guid? AdmissionId, string BookingNumber, Guid? TheatreId, SurgeryStatus Status, SurgeryPriority Priority,
        DateTimeOffset EnteredAt, DateTimeOffset? CompletedAt, string ProcedureName, Laterality Laterality, string? Diagnosis, Guid? SurgeonId,
        List<Guid> AssistantIds, Guid? AnaesthetistId, AnaesthesiaType AnaesthesiaType, string? CancelReason, Guid? NurseId = null);

    public sealed record Note(
        Guid BookingId, string? PreOperativeDiagnosis, string? PostOperativeDiagnosis, string ProcedurePerformed, string? Findings, string? Complications,
        DateTimeOffset? SignedAt, string? SignedByName);

    public sealed record Anaesthesia(
        Guid BookingId, Guid? AnaesthetistId, AnaesthesiaType Technique, List<AnaesthesiaDrug> Drugs, string? Complications, DateTimeOffset? SignedAt,
        string? SignedByName);

    public sealed record Person(
        Guid Id, string Uhid, string FullName, Gender Gender, DateOnly? DateOfBirth, Address Address, string? Phone, string? AadhaarLast4,
        string? AbhaNumberMasked, bool IsVip, SensitiveCategory SensitiveCategories);

    public sealed record BedStay(Guid AdmissionId, Guid WardId, DateTimeOffset FromAt, DateTimeOffset? ToAt);

    public sealed record Clinician(Guid Id, string DisplayName, string? Qualification, string? RegistrationCouncil, string? RegistrationNumber);

    /// <summary>What the member of staff running the register may see.</summary>
    public sealed record Viewer(bool FullContacts, bool ReadRestricted, SensitiveCategory Categories);

    /// <summary>
    /// A completed case still missing a signed operation note, or a signed anaesthesia record where an anaesthetist gave the
    /// anaesthesia (local and topical are the surgeon's, written on the operation note): the deficiency an MRD audit looks for.
    /// The technique the anaesthesia record holds wins over the one booked.
    /// </summary>
    public static bool LacksSignedRecords(Case c, Note? note, Anaesthesia? record) =>
        c.Status == SurgeryStatus.Completed
        && (note?.SignedAt is null || (record?.SignedAt is null && !AnaesthesiaTypes.GivenBySurgeon(record?.Technique ?? c.AnaesthesiaType)));

    /// <summary>
    /// The register's own words for the technique (General, Spinal, Epidural, Nerve block, IV / monitored, Local, Topical), as
    /// the paper lists them.
    /// </summary>
    public static string AnaesthesiaLabel(AnaesthesiaType type) => type switch
    {
        AnaesthesiaType.GA => "General",
        AnaesthesiaType.Spinal => "Spinal",
        AnaesthesiaType.Epidural => "Epidural",
        AnaesthesiaType.Regional => "Nerve block",
        AnaesthesiaType.Local => "Local",
        AnaesthesiaType.Sedation => "IV / monitored sedation",
        AnaesthesiaType.Topical => "Topical",
        _ => type.ToString(),
    };

    /// <summary>"M", "F" or "TG", as the register writes the sex.</summary>
    public static string Sex(Gender gender) => gender switch
    {
        Gender.Male => "M",
        Gender.Female => "F",
        Gender.Other => "TG",
        _ => None,
    };

    /// <summary>"1 h 35 min", "45 min" or "2 h".</summary>
    public static string Duration(TimeSpan span)
    {
        var minutes = (int)Math.Round(Math.Max(span.TotalMinutes, 0), MidpointRounding.AwayFromZero);
        var (hours, rest) = (minutes / 60, minutes % 60);
        return hours == 0 ? $"{rest} min" : rest == 0 ? $"{hours} h" : $"{hours} h {rest} min";
    }

    /// <summary>
    /// The ward the patient went to from the theatre: the next bed after the one they held when they left, if they moved
    /// within <see cref="PostOperativeMove"/> of leaving (the ICU, the recovery ward), else the bed they held (back to their
    /// ward, or an ICU bed given while they were still in the theatre). A patient admitted only after the operation goes to
    /// the first bed they took. Null when the stay had no bed then.
    /// </summary>
    public static Guid? WardAfter(IReadOnlyList<BedStay> stays, DateTimeOffset entered, DateTimeOffset left)
    {
        var held = stays.Where(s => s.FromAt <= left && (s.ToAt is null || s.ToAt > left)).OrderByDescending(s => s.FromAt).FirstOrDefault();
        var after = held?.FromAt ?? entered;
        var moved = stays.Where(s => s.FromAt > after && s.FromAt <= left + PostOperativeMove).OrderBy(s => s.FromAt).FirstOrDefault();
        return (moved ?? held)?.WardId;
    }

    /// <summary>"Dr. Arun Kumar, MS (General Surgery), Reg. 12345 (TNMC)": the name, then what the master holds of the rest.</summary>
    public static string Credentials(Clinician? clinician)
    {
        if (clinician is null)
        {
            return None;
        }

        var registration = clinician.RegistrationNumber is not { Length: > 0 } number ? null
            : clinician.RegistrationCouncil is { Length: > 0 } council ? $"Reg. {number} ({council})"
            : $"Reg. {number}";
        return string.Join(", ", new[] { clinician.DisplayName, clinician.Qualification, registration }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>"Aadhaar XXXX XXXX 1234; ABHA xx-xxxx-xxxx-1234": the identity documents the record holds, masked as stored.</summary>
    public static string IdNumber(Person patient)
    {
        var ids = new[]
        {
            patient.AadhaarLast4 is { Length: > 0 } last4 ? $"Aadhaar XXXX XXXX {last4}" : null,
            patient.AbhaNumberMasked is { Length: > 0 } abha ? $"ABHA {abha}" : null,
        };
        var shown = string.Join("; ", ids.Where(id => id is not null));
        return shown.Length == 0 ? None : shown;
    }

    /// <summary>
    /// Whether the viewer may read the patient's clinical columns: as the chart opens (R-3, R-4) — a restricted record to those
    /// who open restricted records, a record in a sensitive category to those holding every category it is in.
    /// </summary>
    public static bool MayReadClinical(Person? patient, Viewer viewer) =>
        patient is null || viewer.ReadRestricted
        || (!patient.IsVip && (patient.SensitiveCategories & ~viewer.Categories) == SensitiveCategory.None);

    public static object?[] Row(
        int serial, Case c, string? theatre, string? admissionNumber, string transferredTo, Person? patient, Note? note, Anaesthesia? record,
        IReadOnlyDictionary<Guid, Clinician> clinicians, Viewer viewer)
    {
        var clinical = MayReadClinical(patient, viewer);
        var named = patient is null || !patient.IsVip || viewer.ReadRestricted;
        var date = AnalyticsClock.LocalDate(c.EnteredAt);

        var (timeTo, duration) = c.CompletedAt is { } left
            ? (DocumentFormat.Time(left), Duration(left - c.EnteredAt))
            : (c.Status == SurgeryStatus.Cancelled ? "Cancelled" : "In theatre", None);
        var name = patient is null ? None : named ? patient.FullName : PatientNames.Restricted(patient.FullName);
        var ageSex = patient is null ? None
            : $"{(patient.DateOfBirth is { } dob ? Age.Format(dob, date) : None)} / {Sex(patient.Gender)}";
        var address = patient is null || !named ? None : Text(PatientContacts.Address(patient.Address, viewer.FullContacts).ToString());
        var id = patient is null || !named ? None : IdNumber(patient);
        var mobile = patient is null || !named ? None : PatientContacts.Phone(patient.Phone, viewer.FullContacts) ?? None;

        var technique = record?.Technique ?? c.AnaesthesiaType;
        var surgery = note is { ProcedurePerformed.Length: > 0 } ? note.ProcedurePerformed : $"{c.ProcedureName} (as booked)";
        var side = c.Laterality switch
        {
            Laterality.Left => ", left",
            Laterality.Right => ", right",
            Laterality.Bilateral => ", bilateral",
            _ => string.Empty,
        };

        return
        [
            serial,
            c.BookingNumber,
            theatre ?? None,
            date,
            DocumentFormat.Time(c.EnteredAt),
            timeTo,
            duration,
            admissionNumber ?? None,
            patient?.Uhid ?? None,
            transferredTo,
            name,
            ageSex,
            address,
            id,
            mobile,
            clinical ? Text(note?.PreOperativeDiagnosis ?? c.Diagnosis) : Withheld,
            clinical ? surgery + side : Withheld,
            clinical ? FinalDiagnosis(note) : Withheld,
            AnaesthesiaLabel(technique),
            clinical ? Drugs(record) : Withheld,
            Signed(Credentials(c.SurgeonId is { } surgeon ? clinicians.GetValueOrDefault(surgeon) : null), "operation note", note?.SignedAt, note?.SignedByName, note is not null),
            Anaesthetist(c, technique, record, clinicians),
            c.AssistantIds.Count == 0 ? None : string.Join("; ", c.AssistantIds.Select(a => Credentials(clinicians.GetValueOrDefault(a)))),
            c.NurseId is { } nurse ? Credentials(clinicians.GetValueOrDefault(nurse)) : None,
            Remarks(c, note, record, clinical),
        ];
    }

    /// <summary>"Acute appendicitis, perforated. Findings: …", from the operation note.</summary>
    private static string FinalDiagnosis(Note? note)
    {
        if (note is null)
        {
            return "Operation note not written";
        }

        var parts = new[] { note.PostOperativeDiagnosis, note.Findings is { Length: > 0 } findings ? $"Findings: {findings}" : null };
        return Text(string.Join(". ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim().TrimEnd('.'))));
    }

    /// <summary>"Bupivacaine 0.5% heavy 3 ml; Fentanyl 25 mcg", in the order given.</summary>
    private static string Drugs(Anaesthesia? record) =>
        record is null || record.Drugs.Count == 0 ? None
        : string.Join("; ", record.Drugs.OrderBy(d => d.At).Select(d => $"{d.Drug.Trim()} {d.Dose.Trim()}".Trim()));

    /// <summary>The team member, then the signature their record carries: who signed it and when, or that it is not yet signed.</summary>
    /// <summary>
    /// The anaesthetist column. Local and topical anaesthesia are the surgeon's: the column names an anaesthetist only when the
    /// booking named one (standing by), not whoever saved the anaesthesia record, which takes the saver as its anaesthetist.
    /// Any other technique names the anaesthetist the record holds, else the one booked.
    /// </summary>
    private static string Anaesthetist(Case c, AnaesthesiaType technique, Anaesthesia? record, IReadOnlyDictionary<Guid, Clinician> clinicians)
    {
        var anaesthetist = AnaesthesiaTypes.GivenBySurgeon(technique) ? c.AnaesthetistId : record?.AnaesthetistId ?? c.AnaesthetistId;
        return anaesthetist is null && AnaesthesiaTypes.GivenBySurgeon(technique)
            ? $"{AnaesthesiaLabel(technique)} by the surgeon"
            : Signed(Credentials(anaesthetist is { } id ? clinicians.GetValueOrDefault(id) : null),
                "anaesthesia record", record?.SignedAt, record?.SignedByName, record is not null);
    }

    private static string Signed(string member, string document, DateTimeOffset? signedAt, string? signedBy, bool written) =>
        !written ? $"{member} · {document} not written"
        : signedAt is { } at ? $"{member} · {document} signed by {signedBy ?? "staff"}, {DocumentFormat.DateTime(at)}"
        : $"{member} · {document} not signed";

    private static string Remarks(Case c, Note? note, Anaesthesia? record, bool clinical)
    {
        var remarks = new List<string>();
        if (c.Priority != SurgeryPriority.Elective)
        {
            remarks.Add(c.Priority == SurgeryPriority.Emergency ? "Emergency" : "Urgent");
        }

        if (c.Status == SurgeryStatus.Cancelled)
        {
            // The reason is what a clinician wrote ("BP 210/110 on the table"): part of the record, withheld with it.
            remarks.Add(clinical
                ? $"Cancelled after Sign-in: {c.CancelReason ?? "no reason recorded"}"
                : "Cancelled after Sign-in (reason withheld: confidential record)");
        }

        if (clinical)
        {
            remarks.AddRange(new[] { note?.Complications, record?.Complications }
                .Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.OrdinalIgnoreCase).Select(text => $"Complications: {text!.Trim()}"));
        }

        return remarks.Count == 0 ? None : string.Join(". ", remarks);
    }

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? None : value.Trim();
}
