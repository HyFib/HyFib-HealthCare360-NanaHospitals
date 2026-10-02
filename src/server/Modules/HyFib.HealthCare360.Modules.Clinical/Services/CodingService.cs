using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Clinical coding at discharge (MRD-02, MB-03) and the morbidity index (MRD-06). Codes must exist in the local
/// catalogue and ICD-10 categories that have subdivisions are rejected, so claims and statistics use codes of the
/// highest specificity. An asterisk code (a manifestation) cannot be the primary diagnosis. Verification is done by a
/// second coder.
/// </summary>
public sealed class CodingService(
    HospitalDbContext db,
    ClinicalAccess access,
    ICurrentUser currentUser,
    TimeProvider time,
    Catalogue.IcdClassificationProvider classification)
{
    public async Task<Result<CodingDto>> CreateAsync(CreateCodingRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var allowed = await access.EnsureCanOpenAsync(request.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var existing = await Codings()
            .Where(c => (request.AdmissionId != null && c.AdmissionId == request.AdmissionId) || (request.EncounterId != null && c.EncounterId == request.EncounterId))
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return Error.Conflict("coding.exists", "This episode already has a coding record.", new { codingId = existing });
        }

        var coding = new EncounterCoding
        {
            FacilityId = facilityId,
            PatientId = request.PatientId,
            EncounterId = request.EncounterId,
            AdmissionId = request.AdmissionId,
            VisitId = request.VisitId,
            EpisodeDate = request.EpisodeDate ?? time.Today(),
            Notes = request.Notes?.Trim(),
        };
        db.Add(coding);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(coding, ct);
    }

    /// <summary>Pending coding record for an admission (created when the patient is discharged). The caller saves.</summary>
    public async Task<EncounterCoding> EnsureForAdmissionAsync(Guid admissionId, Guid patientId, Guid facilityId, Guid? visitId, DateOnly episodeDate, CancellationToken ct)
    {
        var coding = await Codings().FirstOrDefaultAsync(c => c.AdmissionId == admissionId, ct);
        if (coding is null)
        {
            coding = new EncounterCoding { FacilityId = facilityId, PatientId = patientId, AdmissionId = admissionId, VisitId = visitId, EpisodeDate = episodeDate };
            db.Add(coding);
        }
        else
        {
            coding.EpisodeDate = episodeDate;
        }

        return coding;
    }

    public async Task<Result<CodingDto>> SaveAsync(Guid id, SaveCodingRequest request, CancellationToken ct)
    {
        var coding = await Codings().Include(c => c.Entries).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (coding is null)
        {
            return Error.NotFound(nameof(EncounterCoding), id);
        }

        var lines = await ResolveLinesAsync(request.Entries, ct);
        if (lines.IsFailure)
        {
            return lines.Error!;
        }

        db.SetExpectedVersion(coding, request.Version);
        var coded = coding.Code(lines.Value, currentUser.UserId, time.GetUtcNow());
        if (coded.IsFailure)
        {
            return coded.Error!;
        }

        coding.Notes = string.IsNullOrWhiteSpace(request.Notes) ? coding.Notes : request.Notes.Trim();
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(coding, ct);
    }

    public async Task<Result<CodingDto>> VerifyAsync(Guid id, uint version, CancellationToken ct)
    {
        var coding = await Codings().Include(c => c.Entries).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (coding is null)
        {
            return Error.NotFound(nameof(EncounterCoding), id);
        }

        db.SetExpectedVersion(coding, version);
        var verified = coding.Verify(currentUser.UserId, time.GetUtcNow());
        if (verified.IsFailure)
        {
            return verified.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(coding, ct);
    }

    /// <summary>Diagnoses documented for the episode (encounters and the discharge summary) as coding suggestions.</summary>
    public async Task<Result<IReadOnlyList<CodingSuggestion>>> SuggestionsAsync(Guid id, CancellationToken ct)
    {
        var coding = await Codings().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (coding is null)
        {
            return Error.NotFound(nameof(EncounterCoding), id);
        }

        var encounterIds = await db.Set<Encounter>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(e => (coding.AdmissionId != null && e.AdmissionId == coding.AdmissionId) || e.Id == coding.EncounterId)
            .Select(e => (Guid?)e.Id)
            .ToListAsync(ct);
        var suggestions = await db.Set<Diagnosis>().AsNoTracking()
            .Where(d => encounterIds.Contains(d.EncounterId) && d.Type != DiagnosisType.Differential)
            .OrderByDescending(d => d.IsPrimary).ThenByDescending(d => d.Type == DiagnosisType.Final)
            .Select(d => new CodingSuggestion(d.IcdCode, d.Title, d.Type, d.IsPrimary, "Encounter diagnosis"))
            .ToListAsync(ct);

        if (coding.AdmissionId is { } admissionId)
        {
            var summaryCodes = await db.Set<DischargeSummary>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(s => s.AdmissionId == admissionId).Select(s => s.DiagnosisCodes).FirstOrDefaultAsync(ct) ?? [];
            var titles = await db.Set<IcdCode>().Where(c => summaryCodes.Contains(c.Code)).ToDictionaryAsync(c => c.Code, c => c.Title, ct);
            suggestions.AddRange(summaryCodes.Select(code => new CodingSuggestion(code, titles.GetValueOrDefault(code, code), DiagnosisType.Final, false, "Discharge summary")));
        }

        return Result<IReadOnlyList<CodingSuggestion>>.Ok(suggestions.DistinctBy(s => (s.Code, s.Title)).ToList());
    }

    /// <summary>
    /// Morbidity index: episodes and distinct patients per ICD code/block/chapter with sex and age-band breakdown.
    /// <paramref name="source"/> "coded" uses verified/coded MRD coding; "clinical" uses clinicians' diagnoses.
    /// </summary>
    public async Task<MorbidityReport> MorbidityAsync(DateOnly fromDate, DateOnly toDate, string groupBy, string source, CancellationToken ct)
    {
        var (fromUtc, toUtc) = ClinicalClock.RangeUtc(fromDate, toDate);
        var coded = string.Equals(source, "coded", StringComparison.OrdinalIgnoreCase);
        var rows = coded
            ? await (
                    from c in Codings()
                    from e in c.Entries
                    join p in db.Set<Patient>().IgnoreQueryFilters() on c.PatientId equals p.Id
                    where c.Status != CodingStatus.Pending && c.EpisodeDate >= fromDate && c.EpisodeDate <= toDate && e.Kind == CodingEntryKind.Diagnosis
                    select new MorbidityFact(e.Code, e.Title, c.PatientId, c.Id, p.Gender, p.DateOfBirth))
                .ToListAsync(ct)
            : await (
                    from d in db.Set<Diagnosis>()
                    join p in db.Set<Patient>().IgnoreQueryFilters() on d.PatientId equals p.Id
                    where d.IcdCode != null && d.Type != DiagnosisType.Differential && d.RecordedAt >= fromUtc && d.RecordedAt < toUtc
                    select new MorbidityFact(d.IcdCode!, d.Title, d.PatientId, d.EncounterId ?? d.Id, p.Gender, p.DateOfBirth))
                .ToListAsync(ct);

        var mode = groupBy.ToLowerInvariant() is "block" or "chapter" ? groupBy.ToLowerInvariant() : "code";
        var set = await classification.GetAsync(ct);
        var grouped = rows
            .Select(r => (Fact: r, Group: KeyFor(r, mode, set)))
            .GroupBy(x => x.Group.Key)
            .Select(g =>
            {
                var facts = g.Select(x => x.Fact).DistinctBy(f => (f.EpisodeId, f.Code)).ToList();
                var episodes = facts.DistinctBy(f => f.EpisodeId).ToList();
                return new MorbidityRow(
                    g.Key, g.First().Group.Title, g.First().Group.Chapter, episodes.Count, facts.Select(f => f.PatientId).Distinct().Count(),
                    episodes.Count(f => f.Gender == Gender.Male), episodes.Count(f => f.Gender == Gender.Female),
                    episodes.Count(f => f.Gender is not (Gender.Male or Gender.Female)),
                    episodes.Count(f => Band(f.DateOfBirth, toDate) == 0), episodes.Count(f => Band(f.DateOfBirth, toDate) == 1),
                    episodes.Count(f => Band(f.DateOfBirth, toDate) == 2), episodes.Count(f => Band(f.DateOfBirth, toDate) == 3),
                    episodes.Count(f => Band(f.DateOfBirth, toDate) < 0));
            })
            .OrderByDescending(r => r.Episodes)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();

        return new MorbidityReport(fromDate, toDate, mode, coded ? "coded" : "clinical", rows.Select(r => r.EpisodeId).Distinct().Count(), grouped);
    }

    public IQueryable<EncounterCoding> Codings() => db.Set<EncounterCoding>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);

    public async Task<CodingDto> ToDtoAsync(EncounterCoding c, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().Where(p => p.Id == c.PatientId).Select(p => new { p.Uhid, p.FullName }).FirstOrDefaultAsync(ct);
        return new CodingDto(c.Id, c.PatientId, patient?.Uhid, patient?.FullName, c.EncounterId, c.AdmissionId, c.VisitId, c.EpisodeDate, c.Status,
            c.CoderUserId, c.CodedAt, c.VerifiedByUserId, c.VerifiedAt, c.Notes,
            c.Entries.OrderBy(e => e.Sequence).Select(e => new CodingEntryDto(e.Id, e.Kind, e.Code, e.Title, e.IsPrimary, e.Sequence)).ToList(), c.Version);
    }

    private async Task<Result<IReadOnlyList<CodingLine>>> ResolveLinesAsync(IReadOnlyList<CodingLineRequest> entries, CancellationToken ct)
    {
        var diagnosisCodes = entries.Where(e => e.Kind == CodingEntryKind.Diagnosis).Select(e => e.Code.Trim().ToUpperInvariant()).Distinct().ToList();
        var procedureCodes = entries.Where(e => e.Kind == CodingEntryKind.Procedure).Select(e => e.Code.Trim().ToUpperInvariant()).Distinct().ToList();
        var icd = await db.Set<IcdCode>().AsNoTracking().Where(c => diagnosisCodes.Contains(c.Code) && c.IsActive).ToListAsync(ct);
        var procedures = await db.Set<ProcedureCode>().AsNoTracking().Where(c => procedureCodes.Contains(c.Code) && c.IsActive).ToListAsync(ct);

        var lines = new List<CodingLine>();
        foreach (var entry in entries)
        {
            var code = entry.Code.Trim().ToUpperInvariant();
            if (entry.Kind == CodingEntryKind.Diagnosis)
            {
                var match = icd.Where(c => c.Code == code).OrderBy(c => c.Release == CodeReleases.Icd10 ? 0 : 1).FirstOrDefault();
                if (match is null)
                {
                    return Error.Field("entries", $"{code} is not in the ICD catalogue. Search the catalogue or add the code first.");
                }

                if (!match.IsBillable)
                {
                    return Error.Field("entries", $"{code} is a category with subdivisions; choose the specific code.");
                }

                if (entry.IsPrimary && match.Usage == IcdUsage.Asterisk)
                {
                    return Error.Field("entries",
                        $"{code}* is an asterisk code: it records how a disease shows in an organ. Make the underlying disease (its dagger code) the primary diagnosis and keep {code} as a secondary one.");
                }

                lines.Add(new CodingLine(CodingEntryKind.Diagnosis, match.Code, match.Title, entry.IsPrimary));
            }
            else
            {
                var match = procedures.FirstOrDefault(c => c.Code == code);
                if (match is null)
                {
                    return Error.Field("entries", $"{code} is not in the procedure catalogue.");
                }

                lines.Add(new CodingLine(CodingEntryKind.Procedure, match.Code, match.Title, entry.IsPrimary));
            }
        }

        return Result<IReadOnlyList<CodingLine>>.Ok(lines);
    }

    /// <summary>The group of a fact, by the chapters and blocks in use (the imported WHO file's, or the built-in list).</summary>
    private static (string Key, string Title, string? Chapter) KeyFor(MorbidityFact fact, string mode, IcdClassificationSet set)
    {
        var chapter = set.ChapterOf(fact.Code);
        if (mode == "chapter")
        {
            return chapter is null ? ("?", "Unclassified", null) : (chapter.Number, chapter.Title, chapter.Number);
        }

        if (mode == "block")
        {
            var block = set.BlockOf(fact.Code);
            return block is null ? ("?", "Unclassified", chapter?.Number) : (block.Range, block.Title, chapter?.Number);
        }

        return (fact.Code, fact.Title, chapter?.Number);
    }

    /// <summary>0: 0–14, 1: 15–44, 2: 45–64, 3: 65+, -1: unknown (age at the end of the period).</summary>
    private static int Band(DateOnly? dateOfBirth, DateOnly asOf) => dateOfBirth is not { } dob
        ? -1
        : Age.InYears(dob, asOf) switch
        {
            < 15 => 0,
            < 45 => 1,
            < 65 => 2,
            _ => 3,
        };

    private sealed record MorbidityFact(string Code, string Title, Guid PatientId, Guid EpisodeId, Gender Gender, DateOnly? DateOfBirth);
}
