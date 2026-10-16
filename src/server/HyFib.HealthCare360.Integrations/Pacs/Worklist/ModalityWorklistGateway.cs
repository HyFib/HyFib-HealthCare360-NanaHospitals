using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using FellowOakDicom;
using FellowOakDicom.Network;
using HyFib.HealthCare360.Integrations.Laboratory;

namespace HyFib.HealthCare360.Integrations.Pacs.Worklist;

/// <summary>
/// Whether an association is accepted: for an allowed modality, as the server's own test, or refused (with the reason,
/// and its <see cref="Kind"/>, which throttles how often such refusals are logged).
/// </summary>
internal sealed record WorklistDecision(
    WorklistPeer? Peer,
    bool SelfTest,
    DicomRejectResult Result,
    DicomRejectReason? Reject,
    string? Reason,
    string? Kind = null)
{
    public static WorklistDecision Refuse(
        DicomRejectReason reason, string kind, string why, DicomRejectResult result = DicomRejectResult.Permanent) =>
        new(null, false, result, reason, why, kind);
}

/// <summary>The kinds of refusal; each is logged at most once per <c>RefusalLogInterval</c> and network.</summary>
internal static class WorklistRefusals
{
    public const string UnknownAddress = "unknown-address";
    public const string CalledAe = "called-ae";
    public const string CallingAe = "calling-ae";
    public const string PeerAddress = "peer-address";
    public const string Busy = "busy";
    public const string Withdrawn = "withdrawn";
}

/// <summary>The answers to one query, and the status that ends it.</summary>
internal sealed record WorklistAnswer(IReadOnlyList<DicomDataset> Datasets, DicomStatus Status);

/// <summary>
/// What the worklist server's connections share (one instance per server process): the settings in force, who may
/// connect, and answering queries. A query gets its own DI scope, where it reads the worklist through
/// <see cref="IModalityWorklistSource"/> (implemented by Diagnostics) for the modality's own facility, writes one line to
/// the integration log with the patient keys masked, and one PHI-access audit entry for the answer (who asked, from
/// where, how many entries) rather than one per patient. If that audit entry cannot be written, nothing is sent.
/// </summary>
internal sealed class ModalityWorklistGateway(
    IServiceScopeFactory scopes,
    ModalityWorklistState state,
    TimeProvider time,
    ILogger<ModalityWorklistGateway> logger)
{
    /// <summary>Most worklist entries read from the source for one query.</summary>
    internal const int MaxScanned = 5000;

    /// <summary>A refusal of the same kind is written to the integration log at most this often (the dashboard counts every one).</summary>
    internal static readonly TimeSpan RefusalLogInterval = TimeSpan.FromMinutes(10);

    /// <summary>Most networks whose refusals are throttled one by one; beyond that, by kind alone.</summary>
    private const int MaxRefusalKeys = 1000;

    private static readonly JsonSerializerOptions SummaryJson = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, RefusalLog> _refusals = new(StringComparer.Ordinal);
    private volatile WorklistConfig _config = WorklistConfig.Off;
    private int _associations;

    public WorklistConfig Config => _config;

    public ModalityWorklistState State => state;

    public void Apply(WorklistConfig config)
    {
        _config = config;
        state.Problems(config.Problems);
    }

    /// <summary>
    /// Whether a connection from <paramref name="remote"/> may speak DICOM at all: only allowed modalities' addresses,
    /// and this machine itself (the administrator's test). Others are closed before a byte is read.
    /// </summary>
    public bool Admits(IPAddress? remote, IPAddress? local) =>
        remote is not null && (IsSelf(remote, local) || _config.IsKnownAddress(WorklistConfig.Normalize(remote)));

    /// <summary>Accepts an association from an allowed modality (or the server's own test) and refuses everything else.</summary>
    public WorklistDecision Decide(string? callingAe, string? calledAe, IPAddress? remote, IPAddress? local)
    {
        var config = _config;
        var calling = callingAe?.Trim() ?? string.Empty;
        var called = calledAe?.Trim() ?? string.Empty;
        if (!string.Equals(called, config.AeTitle, StringComparison.OrdinalIgnoreCase))
        {
            return WorklistDecision.Refuse(DicomRejectReason.CalledAENotRecognized, WorklistRefusals.CalledAe,
                $"{Describe(calling)} at {remote} called the AE title \"{Clip(called)}\"; this server is {config.AeTitle}.");
        }

        if (remote is not null && IsSelf(remote, local) && string.Equals(calling, config.AeTitle, StringComparison.OrdinalIgnoreCase))
        {
            return new WorklistDecision(null, true, DicomRejectResult.Permanent, null, null);
        }

        if (config.Peer(calling) is not { } peer)
        {
            return WorklistDecision.Refuse(DicomRejectReason.CallingAENotRecognized, WorklistRefusals.CallingAe,
                $"{Describe(calling)} at {remote} is not on the list of modalities allowed to read the worklist.");
        }

        if (remote is null || !peer.Addresses.Contains(WorklistConfig.Normalize(remote)))
        {
            return WorklistDecision.Refuse(DicomRejectReason.CallingAENotRecognized, WorklistRefusals.PeerAddress,
                $"Modality {peer.AeTitle} called from {remote}, which is not one of its allowed addresses.");
        }

        return new WorklistDecision(peer, false, DicomRejectResult.Permanent, null, null);
    }

    /// <summary>
    /// The modality as the settings in force now describe it, for each query of an open association: null when it was
    /// removed from the list or no longer has the address it calls from. A modality moved to another facility reads
    /// that facility's worklist from its next query on.
    /// </summary>
    public WorklistPeer? CurrentPeer(string aeTitle, IPAddress? remote) =>
        remote is not null && _config.Peer(aeTitle) is { } peer
                           && peer.Addresses.Contains(WorklistConfig.Normalize(remote))
            ? peer
            : null;

    /// <summary>Counts an accepted association; false (and nothing counted) when the limit is reached.</summary>
    public bool TryOpen()
    {
        var limit = _config.Listener?.MaxAssociations ?? 0;
        if (Interlocked.Increment(ref _associations) > limit)
        {
            Interlocked.Decrement(ref _associations);
            return false;
        }

        state.Opened();
        return true;
    }

    public void Close()
    {
        Interlocked.Decrement(ref _associations);
        state.Closed();
    }

    /// <summary>
    /// Records a refused connection, association or query: on the dashboard always, in the service log at Debug always,
    /// and as a warning and in the integration log once per <see cref="RefusalLogInterval"/> for each kind of refusal
    /// and network (/24 for IPv4, /64 for IPv6), so a scan of many addresses cannot flood either log.
    /// </summary>
    public async Task RefuseAsync(string kind, string? callingAe, IPAddress? remote, string reason)
    {
        state.Refused(reason);
        logger.LogDebug("Worklist association refused: {Reason}", reason);
        var now = time.GetUtcNow();
        if (!FirstRefusalInInterval(kind, remote, now, out var suppressed))
        {
            return;
        }

        var note = suppressed > 0
            ? string.Create(CultureInfo.InvariantCulture, $" ({suppressed} similar refusals since the last one logged)")
            : string.Empty;
        logger.LogWarning("Worklist association refused: {Reason}{Suppressed}", reason, note);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var trace = scope.ServiceProvider.GetRequiredService<IntegrationTrace>();
            await trace.LogAsync(IntegrationNames.Mwl, Provider(callingAe), "associate", false, trace.Start(),
                Clip(callingAe), $"from {remote}", null, reason + note, IntegrationDirection.Inbound);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not log a refused worklist association ({ErrorType})", ex.GetType().Name);
        }
    }

    /// <summary>
    /// True when no refusal of this kind from this network was logged in the last <see cref="RefusalLogInterval"/>;
    /// <paramref name="suppressed"/> is how many were not logged since. When too many networks are being refused,
    /// refusals are throttled by kind alone.
    /// </summary>
    internal bool FirstRefusalInInterval(string kind, IPAddress? remote, DateTimeOffset now, out int suppressed)
    {
        if (_refusals.Count >= MaxRefusalKeys)
        {
            foreach (var (stale, entry) in _refusals)
            {
                if (now - entry.LoggedAt >= RefusalLogInterval)
                {
                    _refusals.TryRemove(stale, out _);
                }
            }
        }

        var key = _refusals.Count >= MaxRefusalKeys ? kind : $"{kind}|{Network(remote)}";
        var logged = false;
        var count = 0;
        _refusals.AddOrUpdate(
            key,
            _ =>
            {
                logged = true;
                return new RefusalLog(now, 0);
            },
            (_, entry) =>
            {
                if (now - entry.LoggedAt >= RefusalLogInterval)
                {
                    logged = true;
                    count = entry.Suppressed;
                    return new RefusalLog(now, 0);
                }

                logged = false;
                return entry with { Suppressed = entry.Suppressed + 1 };
            });
        suppressed = count;
        return logged;
    }

    /// <summary>The network an address belongs to for throttling: its /24 (IPv4) or /64 (IPv6).</summary>
    internal static string Network(IPAddress? remote)
    {
        if (remote is null)
        {
            return "unknown";
        }

        var bytes = WorklistConfig.Normalize(remote).GetAddressBytes();
        var keep = bytes.Length == 4 ? 3 : 8;
        Array.Clear(bytes, keep, bytes.Length - keep);
        return new IPAddress(bytes) + (bytes.Length == 4 ? "/24" : "/64");
    }

    /// <summary>Answers one C-FIND from an allowed modality (see the class summary).</summary>
    public async Task<WorklistAnswer> QueryAsync(WorklistPeer peer, IPAddress? remote, DicomDataset? identifier, CancellationToken cancellationToken)
    {
        var config = _config;
        var keys = WorklistDicom.Keys(identifier);
        var summary = Summary(keys);
        await using var scope = scopes.CreateAsyncScope();
        var trace = scope.ServiceProvider.GetRequiredService<IntegrationTrace>();
        var started = trace.Start();
        // Modalities query outside any request: the log rows count against the modality's facility.
        using var facility = IntegrationTrace.ForFacility(peer.FacilityId);
        try
        {
            var source = scope.ServiceProvider.GetService<IModalityWorklistSource>();
            if (source is null)
            {
                const string NoSource = "No worklist source is registered (the Diagnostics module is not loaded).";
                logger.LogWarning("Worklist query from {AeTitle} refused: {Reason}", peer.AeTitle, NoSource);
                await trace.LogAsync(IntegrationNames.Mwl, Provider(peer.AeTitle), "c-find", false, started, peer.AeTitle, summary, null, NoSource,
                    IntegrationDirection.Inbound);
                state.Refused(NoSource);
                return new WorklistAnswer([], DicomStatus.QueryRetrieveUnableToProcess);
            }

            var items = await FindAsync(source, peer, keys, config, cancellationToken);
            var presentation = new WorklistPresentation(config.CharacterSet, string.Empty, Exact(keys.StationAeTitle));
            var datasets = new List<DicomDataset>();
            var unreadable = 0;
            foreach (var item in items.Matched)
            {
                try
                {
                    datasets.Add(WorklistDicom.Response(item, identifier,
                        presentation with { Modality = WorklistMatcher.PresentedModality(item.Modality, keys.Modality, peer.Modalities) }));
                }
                catch (Exception ex) when (ex is DicomDataException or ArgumentException or FormatException or InvalidOperationException)
                {
                    // Only the type: a validation message would quote the value, which may be a patient's name.
                    unreadable++;
                    logger.LogWarning("A worklist entry for study {StudyId} could not be put into DICOM ({ErrorType})", item.StudyId, ex.GetType().Name);
                }
            }

            if (datasets.Count > 0)
            {
                // Written before anything is sent: no audit entry, no patient data.
                var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
                await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "worklist.queried",
                    string.Create(CultureInfo.InvariantCulture, $"The modality worklist sent {datasets.Count} {(datasets.Count == 1 ? "entry" : "entries")} to {peer.AeTitle} at {remote}"))
                {
                    EntityType = "ModalityWorklist",
                    EntityId = peer.AeTitle,
                    UserName = $"Modality {peer.AeTitle}",
                    Details = new { callingAeTitle = peer.AeTitle, modality = peer.Name, address = remote?.ToString(), facilityId = peer.FacilityId, entries = datasets.Count, query = summary },
                }, CancellationToken.None);
            }

            var answer = string.Create(CultureInfo.InvariantCulture, $"{datasets.Count} {(datasets.Count == 1 ? "entry" : "entries")}")
                         + (items.Limited ? " (more entries match; the answer was cut short)" : string.Empty)
                         + (unreadable > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {unreadable} could not be put into DICOM") : string.Empty);
            await trace.LogAsync(IntegrationNames.Mwl, Provider(peer.AeTitle), "c-find", true, started, peer.AeTitle, summary, answer, null, IntegrationDirection.Inbound);
            state.Answered();
            return new WorklistAnswer(datasets, DicomStatus.Success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Only the type is logged: exception messages can quote the query or the data.
            var error = $"The worklist could not be read ({ex.GetType().Name}).";
            logger.LogError("Worklist query from {AeTitle} failed ({ErrorType})", peer.AeTitle, ex.GetType().Name);
            await trace.LogAsync(IntegrationNames.Mwl, Provider(peer.AeTitle), "c-find", false, started, peer.AeTitle, summary, null, error, IntegrationDirection.Inbound);
            state.Refused(error);
            return new WorklistAnswer([], DicomStatus.QueryRetrieveUnableToProcess);
        }
    }

    /// <summary>The hospital's calendar day now (India Standard Time unless the zone is set otherwise).</summary>
    internal static DateOnly Today(TimeProvider time) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), LabTime.ResolveZone(null)).DateTime);

    /// <summary>
    /// Asks the source for the modality's facility, the days the query names within the days offered (today minus
    /// <c>daysBack</c> to today plus <c>daysAhead</c>), the start times it names, the kinds of study the modality does,
    /// and the keys the database can filter on (the station, the accession number and patient ID with their wildcards,
    /// the start of the family name); then applies every key, wildcards included. Entries nearest to now come first,
    /// and the source is read page by page until <c>maxResults</c> entries match, so entries the database could not
    /// rule out never crowd out matching ones. At most <see cref="MaxScanned"/> entries are read for one query.
    /// </summary>
    private async Task<(IReadOnlyList<ModalityWorklistItem> Matched, bool Limited)> FindAsync(
        IModalityWorklistSource source, WorklistPeer peer, WorklistKeys keys, WorklistConfig config, CancellationToken cancellationToken)
    {
        var today = Today(time);
        var (askedFrom, askedTo) = keys.Dates?.Days() ?? (null, null);
        var from = Max(today.AddDays(-config.DaysBack), askedFrom);
        var to = Min(today.AddDays(config.DaysAhead), askedTo);
        var modalities = peer.Modalities.Where(m => WorklistMatcher.ModalityMatches(keys.Modality, m)).ToList();
        if (from > to || modalities.Count == 0)
        {
            return ([], false);
        }

        if (TooLong(keys.AccessionNumber) || TooLong(keys.PatientId) || TooLong(keys.PatientName))
        {
            // Matches nothing (DicomWildcard.MaxPatternLength); the database need not be asked.
            return ([], false);
        }

        var (fromTime, toTime) = keys.Times?.Clock() ?? (null, null);
        var page = Math.Min(config.MaxResults + 1, ModalityWorklistQuery.MaxLimit);
        var query = new ModalityWorklistQuery(peer.FacilityId, from, to, modalities, Exact(keys.StationAeTitle),
            Pattern(keys.AccessionNumber), Pattern(keys.PatientId), page)
        {
            PatientNamePrefix = WorklistMatcher.FamilyNamePrefix(keys.PatientName),
            FromTime = fromTime,
            ToTime = toTime,
            Around = time.GetUtcNow(),
        };
        var matched = new List<ModalityWorklistItem>();
        for (var read = 0; read < MaxScanned;)
        {
            var items = await source.FindAsync(query with { Offset = read }, cancellationToken);
            matched.AddRange(items.Where(i => i.FacilityId == peer.FacilityId && WorklistMatcher.Matches(i, keys)));
            if (matched.Count > config.MaxResults)
            {
                return (matched.Take(config.MaxResults).ToList(), true);
            }

            if (items.Count < page)
            {
                return (matched, false);
            }

            read += items.Count;
        }

        return (matched, true);
    }

    /// <summary>A key the database can match, wildcards included; null for universal matching.</summary>
    private static string? Pattern(string? key) => DicomWildcard.IsUniversal(key) ? null : key!.Trim();

    private static bool TooLong(string? key) => key is not null && key.Trim().Length > DicomWildcard.MaxPatternLength;

    /// <summary>A key the database can match exactly (no wildcards), or null.</summary>
    private static string? Exact(string? key) =>
        DicomWildcard.IsUniversal(key) || key!.Contains('*', StringComparison.Ordinal) || key.Contains('?', StringComparison.Ordinal) ? null : key.Trim();

    private static DateOnly Max(DateOnly a, DateOnly? b) => b is { } other && other > a ? other : a;

    private static DateOnly Min(DateOnly a, DateOnly? b) => b is { } other && other < a ? other : a;

    /// <summary>The query's shape for the integration log: names left out and identifiers cut to their last four characters.</summary>
    private static string Summary(WorklistKeys keys) => JsonSerializer.Serialize(new
    {
        modality = Clip(keys.Modality),
        station = Clip(keys.StationAeTitle),
        dates = keys.Dates is { } dates ? $"{dates.From}-{dates.To}" : null,
        times = keys.Times is { } times ? $"{times.From}-{times.To}" : null,
        patientName = DicomWildcard.IsUniversal(keys.PatientName) ? null : "***",
        patientId = DicomWildcard.IsUniversal(keys.PatientId) ? null : Masking.MaskIdentifier(keys.PatientId),
        accession = Clip(keys.AccessionNumber),
        procedureId = Clip(keys.RequestedProcedureId),
    }, SummaryJson);

    private static bool IsSelf(IPAddress remote, IPAddress? local)
    {
        var address = WorklistConfig.Normalize(remote);
        return IPAddress.IsLoopback(address) || (local is not null && address.Equals(WorklistConfig.Normalize(local)));
    }

    private static string Provider(string? callingAe) => $"dicom:{Clip(callingAe) ?? "unknown"}";

    private static string Describe(string calling) => calling.Length == 0 ? "A modality without an AE title" : $"AE title \"{Clip(calling)}\"";

    private static string? Clip(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new string(value.Trim().Where(c => !char.IsControl(c)).Take(32).ToArray());

    private sealed record RefusalLog(DateTimeOffset LoggedAt, int Suppressed);
}
