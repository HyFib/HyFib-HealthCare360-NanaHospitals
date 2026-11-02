using System.Globalization;
using System.Security.Cryptography;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>Where an import of the HBP master stands.</summary>
public enum HbpImportStatus
{
    /// <summary>Waiting for a second person's approval; the master is as it was.</summary>
    Waiting = 0,

    /// <summary>Approved and applied: the master in force from then.</summary>
    Applied,

    /// <summary>Refused by the approver; nothing was changed.</summary>
    Rejected,

    /// <summary>Taken back by the person who asked; nothing was changed.</summary>
    Withdrawn,

    /// <summary>Not decided in the time a request may wait; nothing was changed.</summary>
    Expired,

    /// <summary>Approved, but it could no longer be applied as asked (the master had changed); nothing was changed.</summary>
    NotApplied,
}

/// <summary>An import of the HBP master: the file, what it reads and changes, where it stands, who asked and who decided.</summary>
public sealed record HbpImportDto(
    Guid Id,
    string MasterVersion,
    string FileName,
    string FileSha256,
    int Read,
    int Added,
    int Updated,
    int Retired,
    int ProblemCount,
    IReadOnlyList<string> Problems,
    HbpImportStatus Status,
    string AskedBy,
    DateTimeOffset AskedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? Note,
    Guid? ApprovalId);

/// <summary>An import staged, and the approval it waits for.</summary>
public sealed record HbpImportAsked(HbpImportDto Import, ApprovalRequestDto Approval);

/// <summary>What an import's approval names: the staged import, the hash of its rows, and the master they were read against.</summary>
public sealed record HbpMasterPayload(Guid ImportId, string RowsSha256, string MasterStamp);

/// <summary>A procedure of the HBP master as staff look it up.</summary>
public sealed record HbpProcedureDto(
    string Code,
    string PackageCode,
    string SpecialtyCode,
    string? Specialty,
    string? PackageName,
    string ProcedureName,
    decimal? Rate,
    int? LengthOfStayDays,
    bool ReservedForPublic,
    string? Stratification,
    string MasterVersion,
    bool IsActive);

/// <summary>
/// AB PM-JAY's Health Benefit Package master in HC360 (INS-PMJAY): imported from NHA's file, looked up when a package
/// is mapped to the scheme's procedure, and checked when one is. An import changes the master only when two people agree
/// (maker-checker): whoever keeps the tariffs stages NHA's file, and someone else who may approve prices
/// (<see cref="PlatformPermissions.PricesApprove"/>) puts it in force, after reading what it changes. An import updates the
/// procedures it lists, adds the new ones and retires (does not delete) those it no longer lists, so a package or claim
/// that names a retired procedure still reads; a retired procedure cannot be newly mapped.
/// </summary>
internal sealed class HbpService(HospitalDbContext db, IAuditLogger audit, IApprovals approvals, ICurrentUser current, TimeProvider time)
{
    public const string Imported = "billing.hbp.imported";

    public const string ApprovalKind = "billing.hbp_master";

    /// <summary>The master's procedures are the same as the file's.</summary>
    public const string UnchangedCode = "hbp.unchanged";

    /// <summary>The master changed after the import was staged, so the approval did not apply it.</summary>
    public const string MasterChangedCode = "hbp.master_changed";

    /// <summary>The staged rows no longer match the hash the approval names.</summary>
    public const string AlteredCode = "hbp.import_altered";

    /// <summary>
    /// The record an import's approval is about: the master, one for the hospital, so that one import waits for approval
    /// at a time (the approvals keep one waiting request of a kind for a record).
    /// </summary>
    public const string SubjectType = "HbpMaster";

    public static readonly Guid MasterId = new("48425000-0000-4000-8000-000000000001");

    /// <summary>How many imports the master's history lists.</summary>
    private const int HistoryShown = 10;

    /// <summary>How many packages a detail of the approval names; the rest are counted.</summary>
    private const int Named = 5;

    private const string NoReason = "No reason given.";

    /// <summary>
    /// Reads NHA's master and asks a second person to put it in force: nothing in the master changes until then. The
    /// approver is told what it changes, and which of the hospital's packages are mapped to procedures it retires,
    /// newly reserves for public hospitals or rates otherwise.
    /// </summary>
    public async Task<Result<HbpImportAsked>> StageAsync(byte[] content, string fileName, string masterVersion, string? reason, CancellationToken ct)
    {
        var read = HbpMasterFile.Read(content);
        if (read.IsFailure)
        {
            return read.Error!;
        }

        var rows = read.Value.Rows;
        if (rows.Count == 0)
        {
            return Error.Field("file", "No procedure in the file could be read: " + (read.Value.Problems.FirstOrDefault() ?? "it has no rows."));
        }

        var version = masterVersion.Trim();
        var master = await db.Set<HbpProcedure>().AsNoTracking().ToListAsync(ct);
        var diff = HbpMasterDiff.Of(master, rows, version);
        if (diff.IsEmpty)
        {
            return Error.Rule(UnchangedCode,
                $"The master in use already lists these {Count(rows.Count)} procedures as {version}, as this file does, and no others: there is nothing to change.");
        }

        var problems = read.Value.Problems;
        var text = HbpMasterImportRows.Write(rows);
        var import = new HbpMasterImport
        {
            MasterVersion = version,
            FileName = Clip(Path.GetFileName(fileName), 260),
            FileSha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            Rows = text,
            RowsSha256 = HbpMasterImportRows.Hash(text),
            MasterStamp = HbpMasterDiff.Stamp(master),
            Read = rows.Count,
            Added = diff.Added.Count,
            Updated = diff.Updated.Count,
            Retired = diff.Retired.Count,
            ProblemCount = problems.Count,
            Problems = [.. problems.Take(HbpMasterImport.ProblemsKept)],
            AskedByName = Name(),
            AskedAt = time.GetUtcNow(),
        };

        var details = await DetailsAsync(import, diff, problems, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Add(import);
        var asked = await approvals.RequestAsync(
            ApprovalKind,
            new ApprovalSubject(SubjectType, MasterId, null, await FacilityAsync(ct), null),
            new HbpMasterPayload(import.Id, import.RowsSha256, import.MasterStamp),
            $"PM-JAY HBP master {version}: {Changes(import)}",
            details,
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason.Trim(),
            ct);
        if (asked.IsFailure)
        {
            return asked.Error!.Code == "approval.already_requested"
                ? Error.Conflict("approval.already_requested", "An import of the PM-JAY master is already waiting for approval. Withdraw it, or wait for its decision, before importing another.")
                : asked.Error!;
        }

        import.ApprovalId = asked.Value.Id;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new HbpImportAsked(ToDto(import, HbpImportStatus.Waiting, null, null, null), asked.Value);
    }

    /// <summary>
    /// Puts an approved import in force, in the approval's transaction: the rows staged, as long as they still match the
    /// hash the approval names, and only over the master they were read against.
    /// </summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var payload = ApprovalPayload.Read<HbpMasterPayload>(request.Payload);
        var import = await db.Set<HbpMasterImport>().FirstOrDefaultAsync(i => i.Id == payload.ImportId, ct);
        if (import is null)
        {
            return Error.Rule("hbp.import_gone", "The import asked for no longer exists, so the master was not changed.");
        }

        if (import.RowsSha256 != payload.RowsSha256 || HbpMasterImportRows.Hash(import.Rows) != payload.RowsSha256)
        {
            return Error.Rule(AlteredCode, "The procedures staged for this import were changed after it was asked for, so the master was not changed. Import the file again.");
        }

        var master = await db.Set<HbpProcedure>().ToListAsync(ct);
        if (HbpMasterDiff.Stamp(master) != payload.MasterStamp)
        {
            return Error.Rule(MasterChangedCode,
                "The PM-JAY master changed after this import was asked for, so it was not applied: what it would change is no longer what was approved. Import the file again.");
        }

        var rows = HbpMasterImportRows.Read(import.Rows);
        var existing = master.ToDictionary(p => p.Code, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!existing.TryGetValue(row.Code, out var procedure))
            {
                procedure = new HbpProcedure { Code = row.Code };
                db.Add(procedure);
            }

            procedure.PackageCode = row.Code[..5];
            procedure.SpecialtyCode = row.SpecialtyCode;
            procedure.Specialty = row.Specialty;
            procedure.PackageName = row.PackageName;
            procedure.ProcedureName = row.ProcedureName;
            procedure.Rate = row.Rate;
            procedure.LengthOfStayDays = row.LengthOfStayDays;
            procedure.ReservedForPublic = row.ReservedForPublic;
            procedure.Stratification = row.Stratification;
            procedure.MasterVersion = import.MasterVersion;
            procedure.IsActive = true;
        }

        var listed = rows.Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var procedure in master.Where(p => p.IsActive && !listed.Contains(p.Code)))
        {
            procedure.IsActive = false;
        }

        var approver = Name();
        import.AppliedAt = time.GetUtcNow();
        import.ApprovedByName = approver;
        audit.Add(new AuditEntry(AuditCategories.Data, Imported,
            Clip($"PM-JAY HBP master {import.MasterVersion} put in force: {Changes(import)} (file {import.FileName}, SHA-256 {import.FileSha256}; asked for by {import.AskedByName}, approved by {approver})", 1000))
        {
            EntityType = "hbp_master",
            EntityId = import.MasterVersion,
            Severity = AuditSeverity.Warning,
            Details = new
            {
                version = import.MasterVersion,
                import = import.Id,
                file = import.FileName,
                fileSha256 = import.FileSha256,
                rowsSha256 = import.RowsSha256,
                read = import.Read,
                added = import.Added,
                updated = import.Updated,
                retired = import.Retired,
                problems = import.ProblemCount,
                approval = request.Id,
                requestedBy = request.RequestedBy,
            },
        });
        return $"{import.MasterVersion} in force: {Changes(import)}";
    }

    /// <summary>The latest imports, newest first: the master's history, and an import still waiting.</summary>
    public async Task<IReadOnlyList<HbpImportDto>> ImportsAsync(CancellationToken ct)
    {
        // Everything but the rows, which only the approval reads.
        var imports = await db.Set<HbpMasterImport>().AsNoTracking().OrderByDescending(i => i.AskedAt).Take(HistoryShown)
            .Select(i => new HbpMasterImport
            {
                Id = i.Id,
                MasterVersion = i.MasterVersion,
                FileName = i.FileName,
                FileSha256 = i.FileSha256,
                Read = i.Read,
                Added = i.Added,
                Updated = i.Updated,
                Retired = i.Retired,
                ProblemCount = i.ProblemCount,
                Problems = i.Problems,
                ApprovalId = i.ApprovalId,
                AskedByName = i.AskedByName,
                AskedAt = i.AskedAt,
                AppliedAt = i.AppliedAt,
                ApprovedByName = i.ApprovedByName,
            })
            .ToListAsync(ct);
        // The master is the hospital's, whichever facility its import was asked for from.
        var ids = imports.Select(i => i.ApprovalId).OfType<Guid>().ToList();
        var decisions = await db.Set<ApprovalRequest>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.Status, r.ExpiresAt, r.DecidedByName, r.DecidedAt, r.DecisionNote, r.Outcome })
            .ToDictionaryAsync(r => r.Id, ct);
        var now = time.GetUtcNow();
        return
        [
            .. imports.Select(i =>
            {
                if (i.AppliedAt is not null)
                {
                    return ToDto(i, HbpImportStatus.Applied, i.ApprovedByName, i.AppliedAt, null);
                }

                var decision = i.ApprovalId is { } id ? decisions.GetValueOrDefault(id) : null;
                if (decision is null)
                {
                    return ToDto(i, HbpImportStatus.Expired, null, null, null);
                }

                return decision.Status switch
                {
                    ApprovalStatus.Pending when decision.ExpiresAt > now => ToDto(i, HbpImportStatus.Waiting, null, null, null),
                    ApprovalStatus.Rejected => ToDto(i, HbpImportStatus.Rejected, decision.DecidedByName, decision.DecidedAt, decision.DecisionNote),
                    ApprovalStatus.Withdrawn => ToDto(i, HbpImportStatus.Withdrawn, decision.DecidedByName, decision.DecidedAt, null),
                    ApprovalStatus.Failed or ApprovalStatus.Approved => ToDto(i, HbpImportStatus.NotApplied, decision.DecidedByName, decision.DecidedAt, decision.Outcome),
                    _ => ToDto(i, HbpImportStatus.Expired, null, null, null),
                };
            }),
        ];
    }

    /// <summary>Procedures by code, name, package or specialty (the first 50), active ones unless asked otherwise.</summary>
    public async Task<IReadOnlyList<HbpProcedureDto>> SearchAsync(string? search, string? specialtyCode, bool includeRetired, CancellationToken ct)
    {
        var query = db.Set<HbpProcedure>().AsNoTracking();
        if (!includeRetired)
        {
            query = query.Where(p => p.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(specialtyCode))
        {
            var specialty = specialtyCode.Trim().ToUpperInvariant();
            query = query.Where(p => p.SpecialtyCode == specialty);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var code = term.ToUpperInvariant();
            var like = term.ToLikePattern();
            query = query.Where(p => p.Code.StartsWith(code) || EF.Functions.ILike(p.ProcedureName, like, QueryExtensions.LikeEscape)
                || (p.PackageName != null && EF.Functions.ILike(p.PackageName, like, QueryExtensions.LikeEscape))
                || (p.Specialty != null && EF.Functions.ILike(p.Specialty, like, QueryExtensions.LikeEscape)));
        }

        return await query.OrderBy(p => p.Code).Take(50).Select(p => ToDto(p)).ToListAsync(ct);
    }

    public async Task<HbpProcedureDto?> GetAsync(string code, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        var procedure = await db.Set<HbpProcedure>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == normalized, ct);
        return procedure is null ? null : ToDto(procedure);
    }

    /// <summary>The code to keep on a package, or why it cannot be: unknown, or retired by the latest master.</summary>
    public async Task<Result<string?>> CheckForPackageAsync(string? code, string? current, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Result<string?>.Ok(null);
        }

        var normalized = code.Trim().ToUpperInvariant();
        if (!HbpMasterFile.IsProcedureCode(normalized))
        {
            return Error.Field("hbpCode", "An HBP procedure code is two letters, three digits and, for a stratified procedure, a letter (for example SG039A).");
        }

        var procedure = await db.Set<HbpProcedure>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == normalized, ct);
        if (procedure is null)
        {
            return Error.Field("hbpCode", $"{normalized} is not in the imported HBP master. Import NHA's latest master first.");
        }

        // A package already mapped to a procedure the latest master retired keeps it until someone changes it.
        return procedure.IsActive || normalized == current
            ? normalized
            : Error.Field("hbpCode", $"{normalized} was retired by HBP master {procedure.MasterVersion}; map the package to its replacement.");
    }

    /// <summary>
    /// What the approver reads: the master and its file (whose SHA-256 they can match to the copy they were sent), what it
    /// changes, and what that means for the hospital's own packages mapped to the scheme's procedures.
    /// </summary>
    private async Task<List<ApprovalDetail>> DetailsAsync(HbpMasterImport import, HbpMasterDiff diff, IReadOnlyList<string> problems, CancellationToken ct)
    {
        var mapped = await db.Set<Package>().AsNoTracking().Where(p => p.HbpCode != null).OrderBy(p => p.Code)
            .Select(p => new { p.Code, p.Name, HbpCode = p.HbpCode! }).ToListAsync(ct);
        var rates = diff.Rates.ToDictionary(r => r.Code, StringComparer.Ordinal);
        var retired = diff.Retired.ToHashSet(StringComparer.Ordinal);
        var reserved = diff.NewlyReserved.ToHashSet(StringComparer.Ordinal);
        List<ApprovalDetail> details =
        [
            new("Master", $"{import.MasterVersion}, read from {import.FileName}"),
            new("File SHA-256", import.FileSha256),
            new("Procedures", Changes(import)),
        ];

        var onRetired = mapped.Where(p => retired.Contains(p.HbpCode)).Select(p => $"{p.Code} {p.Name} ({p.HbpCode})").ToList();
        if (onRetired.Count > 0)
        {
            details.Add(new("Packages on procedures it retires",
                $"{Names(onRetired)}: they keep the code until it is changed, and a claim naming a retired procedure may be refused"));
        }

        var onReserved = mapped.Where(p => reserved.Contains(p.HbpCode)).Select(p => $"{p.Code} {p.Name} ({p.HbpCode})").ToList();
        if (onReserved.Count > 0)
        {
            details.Add(new("Packages on procedures it reserves for public hospitals", $"{Names(onReserved)}: the scheme refuses a private hospital's claim for them"));
        }

        var onRates = mapped.Where(p => rates.ContainsKey(p.HbpCode))
            .Select(p => $"{p.Code} {p.Name} ({p.HbpCode} {Rate(rates[p.HbpCode].From)} → {Rate(rates[p.HbpCode].To)})").ToList();
        if (onRates.Count > 0)
        {
            details.Add(new("Packages whose scheme rate changes", Names(onRates)));
        }

        if (diff.Rates.Count > 0)
        {
            var largest = diff.Rates.OrderByDescending(r => Math.Abs((r.To ?? 0m) - (r.From ?? 0m))).ThenBy(r => r.Code, StringComparer.Ordinal).Take(3)
                .Select(r => $"{r.Code} {Rate(r.From)} → {Rate(r.To)}");
            details.Add(new("Scheme rates", $"{Count(diff.Rates.Count)} changed; the largest: {string.Join("; ", largest)}"));
        }

        if (diff.NewlyReserved.Count > 0)
        {
            details.Add(new("Reserved for public hospitals", $"{Count(diff.NewlyReserved.Count)} procedure(s) newly reserved"));
        }

        if (problems.Count > 0)
        {
            details.Add(new("Rows not read", Clip($"{Count(problems.Count)}; the first: {problems[0]}", 400)));
        }

        return details;
    }

    private async Task<Guid> FacilityAsync(CancellationToken ct) =>
        current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);

    private string Name() => current.DisplayName ?? current.UserName ?? "Unknown user";

    private static HbpImportDto ToDto(HbpMasterImport i, HbpImportStatus status, string? decidedBy, DateTimeOffset? decidedAt, string? note) => new(
        i.Id, i.MasterVersion, i.FileName, i.FileSha256, i.Read, i.Added, i.Updated, i.Retired, i.ProblemCount, i.Problems, status,
        i.AskedByName, i.AskedAt, decidedBy, decidedAt, note, i.ApprovalId);

    private static HbpProcedureDto ToDto(HbpProcedure p) => new(
        p.Code, p.PackageCode, p.SpecialtyCode, p.Specialty, p.PackageName, p.ProcedureName, p.Rate, p.LengthOfStayDays,
        p.ReservedForPublic, p.Stratification, p.MasterVersion, p.IsActive);

    private static string Changes(HbpMasterImport i) =>
        $"{Count(i.Read)} procedures read: {Count(i.Added)} new, {Count(i.Updated)} changed, {Count(i.Retired)} retired";

    private static string Names(IReadOnlyList<string> names) =>
        string.Join("; ", names.Take(Named)) + (names.Count > Named ? $"; and {names.Count - Named} more" : string.Empty);

    private static string Count(int count) => count.ToString("N0", CultureInfo.InvariantCulture);

    private static string Rate(decimal? rate) => rate is { } amount ? "₹" + Money.FormatInr(amount) : "no rate";

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>A PM-JAY HBP master imported: whoever keeps the tariffs asks, another who may approve prices puts it in force.</summary>
internal sealed class HbpMasterApproval(HbpService hbp) : IApprovalAction
{
    public string Kind => HbpService.ApprovalKind;

    public string Title => "PM-JAY HBP master";

    public string ApprovePermission => PlatformPermissions.PricesApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => hbp.ApplyAsync(request, ct);
}
