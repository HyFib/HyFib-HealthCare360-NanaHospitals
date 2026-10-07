using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// The verified medical records coding of the episode a bill is for, for insurance claims (MB-03). An in-patient bill
/// takes its admission's coding; any other bill takes the out-patient coding of its visit or of an encounter in the visit.
/// As on the coding worklist, coding records are found in any facility, but only those of the bill's patient.
/// </summary>
internal sealed class VerifiedCodingSource(HospitalDbContext db) : IVerifiedCodingSource
{
    public async Task<VerifiedEpisodeCoding?> FindAsync(
        Guid patientId, Guid? admissionId, Guid? visitId, CancellationToken cancellationToken)
    {
        if (admissionId is null && visitId is null)
        {
            return null;
        }

        var codings = db.Set<EncounterCoding>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(c => c.PatientId == patientId && c.Status == CodingStatus.Verified);
        if (admissionId is not null)
        {
            codings = codings.Where(c => c.AdmissionId == admissionId);
        }
        else
        {
            var encounters = db.Set<Encounter>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(e => e.VisitId == visitId)
                .Select(e => (Guid?)e.Id);
            codings = codings.Where(c =>
                c.AdmissionId == null && (c.VisitId == visitId || encounters.Contains(c.EncounterId)));
        }

        var coding = await codings.Include(c => c.Entries)
            .OrderByDescending(c => c.VerifiedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (coding?.VerifiedAt is not { } verifiedAt)
        {
            return null;
        }

        var entries = coding.Entries.OrderBy(e => e.Sequence).ToList();
        return new VerifiedEpisodeCoding(
            Of(entries, CodingEntryKind.Diagnosis), Of(entries, CodingEntryKind.Procedure), verifiedAt);
    }

    private static List<CodedEntry> Of(List<CodingEntry> entries, CodingEntryKind kind) =>
        entries.Where(e => e.Kind == kind).Select(e => new CodedEntry(e.Code, e.Title, e.IsPrimary)).ToList();
}
