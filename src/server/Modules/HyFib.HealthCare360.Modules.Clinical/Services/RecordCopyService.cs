using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Copies of the record that the medical records department issues as documents (MRD-04), made so that they can be
/// given in evidence under the Bharatiya Sakshya Adhiniyam 2023, s.63. Each document is made into a PDF once, when the
/// copy is issued, and kept as it was handed over with its SHA-256 (a PDF made again differs in its bytes); every download
/// is those bytes, checked against the hash first; the certificate of s.63(4) states the hashes; and a file brought back
/// is found by its hash. The issue is recorded in the audit log with the hashes, where the sealed chain keeps them.
/// </summary>
public sealed class RecordCopyService(
    HospitalDbContext db,
    MedicalRecordService charts,
    ClinicalAccess access,
    IEnumerable<IRecordDocumentSource> sources,
    IPdfRenderer renderer,
    IFileStore files,
    IAuditLogger audit,
    DocumentAssets assets,
    ServerNodeBoard servers,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>The <see cref="StoredFile.Category"/> of a copy's files.</summary>
    public const string FileCategory = "record-copy";

    /// <summary>The patient's documents a copy of the request can hold, newest first.</summary>
    public async Task<Result<IReadOnlyList<RecordDocumentEntry>>> DocumentsAsync(Guid requestId, CancellationToken ct)
    {
        var request = await db.Set<RecordRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null)
        {
            return Error.NotFound(nameof(RecordRequest), requestId);
        }

        var opened = await access.OpenAsync(request.PatientId, $"Listed the documents for record request {request.RequestNumber}", ct);
        if (opened.IsFailure)
        {
            return opened.Error!;
        }

        return Result<IReadOnlyList<RecordDocumentEntry>>.Ok(await ListAsync(request.PatientId, ct));
    }

    /// <summary>
    /// Issues the request. A copy with documents is made now: every document is built and made into a PDF before anything
    /// is saved, so a copy is issued whole or not at all.
    /// </summary>
    /// <exception cref="PdfRenderException">A PDF could not be made; nothing is issued.</exception>
    public async Task<Result<RecordRequestDto>> IssueAsync(Guid requestId, IssueRecordRequest body, CancellationToken ct)
    {
        var request = await db.Set<RecordRequest>().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null)
        {
            return Error.NotFound(nameof(RecordRequest), requestId);
        }

        if (request.Status != RecordRequestStatus.Requested)
        {
            return Error.Rule("record_request.not_pending", "Only pending requests can be issued.");
        }

        var made = new List<(RecordDocumentEntry Entry, PrintableDocument Document, byte[] Pdf)>();
        if (body.Documents is { Count: > 0 } wanted)
        {
            var opened = await access.OpenAsync(request.PatientId, $"Opened the record to copy it for request {request.RequestNumber}", ct);
            if (opened.IsFailure)
            {
                return opened.Error!;
            }

            var listed = (await ListAsync(request.PatientId, ct)).ToDictionary(d => (d.Kind, d.Id));
            foreach (var document in wanted)
            {
                if (sources.FirstOrDefault(s => s.Kinds.Contains(document.Kind)) is not { } source)
                {
                    return Error.Field("documents", $"There is no kind of document \"{document.Kind}\".");
                }

                if (!listed.TryGetValue((document.Kind, document.Id), out var entry)
                    || await source.BuildAsync(document.Kind, document.Id, request.PatientId, ct) is not { } printed)
                {
                    return Error.Rule("record_copy.document_unavailable",
                        "A document chosen can't be put in the copy: it is not this patient's, or it is not signed or released. Choose again from the list.");
                }

                made.Add((entry, printed, await renderer.RenderAsync(printed.ToPdfSource(), ct)));
            }
        }

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var copy = new List<RecordCopyFile>();
        foreach (var (entry, document, pdf) in made)
        {
            using var content = new MemoryStream(pdf, writable: false);
            var stored = await files.SaveAsync(content, document.FileName + ".pdf", FileCategory, request.PatientId, "clinical", ct);
            if (stored.IsFailure)
            {
                return stored.Error!;
            }

            copy.Add(new RecordCopyFile
            {
                RecordRequestId = request.Id,
                Sequence = copy.Count + 1,
                DocumentKind = entry.Kind,
                DocumentId = entry.Id,
                Title = entry.Title,
                DocumentDate = entry.Date,
                StoredFileId = stored.Value.Id,
                FileName = stored.Value.FileName,
                SizeBytes = stored.Value.SizeBytes,
                Sha256 = stored.Value.Sha256,
                ProducedAt = now,
                ProducedByUserId = currentUser.UserId,
                ServerId = servers.Node,
                MachineName = Environment.MachineName,
                OsDescription = RuntimeInformation.OSDescription,
                SoftwareVersion = SystemHealthService.ProductVersion,
            });
        }

        var issued = request.Issue(body.DueBackOn, copy, currentUser.UserId, now, time.Today());
        if (issued.IsFailure)
        {
            return issued.Error!;
        }

        if (copy.Count > 0)
        {
            // The hashes go into the sealed audit chain too: a change to the copy's rows is then found against it.
            audit.Add(new AuditEntry(AuditCategories.PhiAccess, "record.copy_issued",
                $"Issued a copy of the record ({request.RequestNumber}) to {request.RequestedByName}: {copy.Count} file(s)")
            {
                PatientId = request.PatientId,
                EntityType = nameof(RecordRequest),
                EntityId = request.Id.ToString(),
                Details = new
                {
                    request.RequestNumber,
                    request.RequestedByName,
                    Purpose = request.Purpose.ToString(),
                    Files = copy.Select(f => new { f.Sequence, f.Title, f.FileName, f.SizeBytes, f.Sha256 }),
                },
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await charts.ToDtoAsync(request, ct);
    }

    /// <summary>
    /// A file of a copy, as it was issued. Its bytes are checked against the SHA-256 recorded then; a file that no longer
    /// matches is not given out, and the mismatch is recorded as a security event.
    /// </summary>
    public async Task<Result<(RecordCopyFile File, byte[] Content)>> FileAsync(Guid requestId, Guid fileId, CancellationToken ct)
    {
        var found = await (
                from f in db.Set<RecordCopyFile>().AsNoTracking()
                join r in db.Set<RecordRequest>() on f.RecordRequestId equals r.Id
                where f.Id == fileId && r.Id == requestId
                select new { File = f, r.PatientId, r.RequestNumber })
            .FirstOrDefaultAsync(ct);
        if (found is null)
        {
            return Error.NotFound("Record copy file", fileId);
        }

        var opened = await access.OpenAsync(found.PatientId, $"Downloaded file {found.File.Sequence} of record copy {found.RequestNumber}", ct);
        if (opened.IsFailure)
        {
            return opened.Error!;
        }

        var stored = await files.ReadAsync(found.File.StoredFileId, ct);
        if (stored.IsFailure)
        {
            return stored.Error!;
        }

        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stored.Value.Content)), found.File.Sha256, StringComparison.Ordinal))
        {
            await audit.WriteAsync(new AuditEntry(AuditCategories.Security, "record.copy_altered",
                $"File {found.File.Sequence} of record copy {found.RequestNumber} no longer matches the SHA-256 recorded when it was issued")
            {
                PatientId = found.PatientId,
                EntityType = nameof(RecordCopyFile),
                EntityId = found.File.Id.ToString(),
                Severity = AuditSeverity.Critical,
            }, ct);
            return Error.Conflict("record_copy.altered",
                "This file no longer matches the SHA-256 recorded when the copy was issued. Do not hand it over: tell the IT officer.");
        }

        return (found.File, stored.Value.Content);
    }

    /// <summary>The certificate under BSA 2023 s.63(4) for a copy issued as documents.</summary>
    public async Task<Result<PrintableDocument>> CertificateAsync(Guid requestId, CancellationToken ct)
    {
        var request = await db.Set<RecordRequest>().AsNoTracking().Include(r => r.Files).FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null)
        {
            return Error.NotFound(nameof(RecordRequest), requestId);
        }

        if (request.Files.Count == 0 || request.IssuedAt is not { } issuedAt)
        {
            return Error.Rule("record_copy.no_files", "Only a copy issued as documents has a certificate: there are no files for it to state.");
        }

        var opened = await access.OpenAsync(request.PatientId, $"Printed the BSA s.63 certificate of record copy {request.RequestNumber}", ct);
        if (opened.IsFailure)
        {
            return opened.Error!;
        }

        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == request.PatientId).Select(p => new { p.FullName, p.Uhid }).FirstAsync(ct);
        var issuedBy = request.IssuedByUserId is { } userId
            ? await db.Set<User>().AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
            : null;
        var letterhead = await assets.LetterheadAsync(request.FacilityId, ct);
        var hospital = string.Join(", ", new[] { letterhead.LegalName ?? letterhead.Name, letterhead.Address }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var requestedBy = request.RequestingDepartment is { Length: > 0 } department ? $"{request.RequestedByName}, {department}" : request.RequestedByName;
        return RecordCopyCertificate.Build(
            new RecordCopyCertificateModel(request.RequestNumber, request.Purpose, requestedBy, patient.FullName, patient.Uhid, issuedAt,
                issuedBy ?? "—", hospital, request.Files),
            letterhead);
    }

    /// <summary>
    /// The files issued whose SHA-256 is <paramref name="sha256"/>: the court's or a lawyer's question "is this what the
    /// hospital issued?". The file itself never comes to the server; the staff app works out its hash.
    /// </summary>
    public async Task<Result<IReadOnlyList<RecordCopyMatch>>> VerifyAsync(string sha256, CancellationToken ct)
    {
        if (RecordCopyFile.NormalizeSha256(sha256) is not { } hash)
        {
            return Error.Field("sha256", "Enter a SHA-256 value: 64 characters, 0-9 and A-F.");
        }

        var matches = await (
                from f in db.Set<RecordCopyFile>().AsNoTracking()
                join r in db.Set<RecordRequest>() on f.RecordRequestId equals r.Id
                join p in db.Set<Patient>() on r.PatientId equals p.Id
                where f.Sha256 == hash
                orderby r.IssuedAt, f.Sequence
                select new
                {
                    r.PatientId,
                    Match = new RecordCopyMatch(
                        r.Id, r.RequestNumber, p.Uhid, p.FullName, r.Purpose, r.RequestedByName, r.IssuedAt, f.Sequence, f.Title, f.FileName,
                        f.SizeBytes, f.Sha256),
                })
            .ToListAsync(ct);
        foreach (var patientId in matches.Select(m => m.PatientId).Distinct())
        {
            var opened = await access.OpenAsync(patientId, "Checked a file against the record copies issued", ct);
            if (opened.IsFailure)
            {
                return opened.Error!;
            }
        }

        return Result<IReadOnlyList<RecordCopyMatch>>.Ok([.. matches.Select(m => m.Match)]);
    }

    private async Task<IReadOnlyList<RecordDocumentEntry>> ListAsync(Guid patientId, CancellationToken ct)
    {
        var documents = new List<RecordDocumentEntry>();
        foreach (var source in sources)
        {
            documents.AddRange(await source.ListAsync(patientId, ct));
        }

        return [.. documents.OrderByDescending(d => d.Date).ThenBy(d => d.Title, StringComparer.Ordinal)];
    }
}
