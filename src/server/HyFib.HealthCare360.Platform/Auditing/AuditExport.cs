using System.Text.Json;

namespace HyFib.HealthCare360.Platform.Auditing;

/// <summary>An audit event as exported: every column, the chain position, the hash it was sealed with and its keyed seal.</summary>
public sealed record AuditExportEvent(
    long Id,
    DateTimeOffset OccurredAt,
    string Category,
    string Action,
    AuditSeverity Severity,
    string? EntityType,
    string? EntityId,
    Guid? PatientId,
    Guid? UserId,
    string? UserName,
    Guid? FacilityId,
    Guid? DeviceId,
    Guid? SessionId,
    string? IpAddress,
    string? CorrelationId,
    string? Summary,
    string? Details,
    long ChainIndex,
    string? PreviousHash,
    string Hash,
    string? Mac)
{
    public static AuditExportEvent From(AuditEvent e) => new(e.Id, e.OccurredAt, e.Category, e.Action, e.Severity, e.EntityType, e.EntityId, e.PatientId,
        e.UserId, e.UserName, e.FacilityId, e.DeviceId, e.SessionId, e.IpAddress, e.CorrelationId, e.Summary, e.Details, e.ChainIndex!.Value, e.PreviousHash,
        e.Hash!, e.Mac);

    internal AuditEvent ToEvent() => new()
    {
        Id = Id,
        OccurredAt = OccurredAt,
        Category = Category,
        Action = Action,
        Severity = Severity,
        EntityType = EntityType,
        EntityId = EntityId,
        PatientId = PatientId,
        UserId = UserId,
        UserName = UserName,
        FacilityId = FacilityId,
        DeviceId = DeviceId,
        SessionId = SessionId,
        IpAddress = IpAddress,
        CorrelationId = CorrelationId,
        Summary = Summary,
        Details = Details,
        ChainIndex = ChainIndex,
        PreviousHash = PreviousHash,
        Hash = Hash,
        Mac = Mac,
    };
}

/// <summary>
/// The audit trail for an auditor (NABH, CERT-In, a court): a contiguous part of the sealed chain, positions
/// <see cref="FromIndex"/> to <see cref="ToIndex"/>, with the anchors written outside the database that fall in it. Anyone
/// can check it without HC360's database or keys (<see cref="AuditExportVerifier"/>, the server's
/// <c>audit verify-export</c>): <see cref="Algorithm"/> says how each hash is made.
/// </summary>
public sealed record AuditExport(
    string Format,
    DateTimeOffset ExportedAt,
    string? ExportedBy,
    long FromIndex,
    long ToIndex,
    string Algorithm,
    IReadOnlyList<AuditAnchor> Anchors,
    IReadOnlyList<AuditExportEvent> Events)
{
    public const string FormatName = "hc360-audit-export/1";

    public const string AlgorithmText =
        "hash = upper-case hex of SHA-256(UTF-8 of (previousHash, or \"GENESIS\" for position 1) + U+001E + content). content joins, with " +
        "U+001F: id; occurredAt as ISO 8601 UTC with 7 decimal places; category; action; entityType; entityId; patientId; userId; facilityId; " +
        "summary; details; then severity (its name); userName; deviceId; sessionId; ipAddress; correlationId. A missing value is empty; a GUID " +
        "is written as 8-4-4-4-12 lower-case hex. Records sealed before the chain covered every column hash only the content up to details. mac = HMAC-SHA256 of the " +
        "hash under the hospital's audit seal key, which stays on the server: HC360's own verification checks it.";
}

/// <summary>The outcome of checking an export.</summary>
public sealed record AuditExportCheck(bool Valid, long EventsChecked, int AnchorsChecked, string Message);

public static class AuditExportVerifier
{
    private const string CutShort = "The export ends part-way: it was cut short.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Checks an export: positions follow each other from <see cref="AuditExport.FromIndex"/>, each record links to the one
    /// before and its hash is that of its content, and each anchor in the export, and in <paramref name="ownAnchors"/> (the
    /// auditor's copy of the anchor file, or anchors noted down earlier), matches the record at its position.
    /// </summary>
    public static AuditExportCheck Verify(AuditExport export, IReadOnlyList<AuditAnchor>? ownAnchors = null)
    {
        ArgumentNullException.ThrowIfNull(export);
        var walk = new Walk(export.Format, export.FromIndex, export.ToIndex, export.Anchors, ownAnchors);
        foreach (var exported in export.Events)
        {
            if (walk.Step(exported) is { } broken)
            {
                return broken;
            }
        }

        return walk.Finish();
    }

    /// <summary>
    /// Checks an export as <see cref="Verify(AuditExport, IReadOnlyList{AuditAnchor}?)"/> does, read from
    /// <paramref name="utf8Json"/> a record at a time, so an export of millions of records (a purge's) is checked without
    /// holding it in memory. Its header (format, positions and anchors) comes before its records, as an export writes it.
    /// </summary>
    public static async Task<AuditExportCheck> VerifyAsync(Stream utf8Json, IReadOnlyList<AuditAnchor>? ownAnchors = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        var reader = new StreamingExport(ownAnchors);
        var buffer = new byte[64 * 1024];
        var length = 0;
        var final = false;
        var state = new JsonReaderState();
        try
        {
            while (true)
            {
                if (!final)
                {
                    if (length == buffer.Length)
                    {
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }

                    var read = await utf8Json.ReadAsync(buffer.AsMemory(length), ct);
                    final = read == 0;
                    length += read;
                }

                if (reader.Process(buffer.AsSpan(0, length), final, ref state, out var consumed))
                {
                    return reader.Result ?? new AuditExportCheck(false, 0, 0, "The export has no records' header.");
                }

                if (final)
                {
                    return new AuditExportCheck(false, reader.EventsChecked, 0, CutShort);
                }

                buffer.AsSpan(consumed, length - consumed).CopyTo(buffer);
                length -= consumed;
            }
        }
        catch (JsonException ex)
        {
            // Incomplete JSON is told apart once the whole file is read: the reader then refuses what it had waited on.
            return new AuditExportCheck(false, reader.EventsChecked, 0,
                final ? CutShort : $"The export is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads an export's JSON from a buffer, one property or record at a time, each only once the buffer holds it whole:
    /// <see cref="Process"/> says how much it read, and is called again with the rest and more of the file.
    /// </summary>
    private sealed class StreamingExport(IReadOnlyList<AuditAnchor>? ownAnchors)
    {
        private enum Phase
        {
            Start,
            Properties,
            Events,
            Done,
        }

        private Phase _phase;
        private string? _format;
        private long? _fromIndex;
        private long? _toIndex;
        private IReadOnlyList<AuditAnchor> _anchors = [];
        private Walk? _walk;

        /// <summary>The outcome, once known: the first broken record, or the whole export's check at its end.</summary>
        public AuditExportCheck? Result { get; private set; }

        public long EventsChecked => _walk?.EventsChecked ?? 0;

        /// <summary>True when the export has been read to its end, or found broken; false when it needs more of the file.</summary>
        public bool Process(ReadOnlySpan<byte> data, bool final, ref JsonReaderState state, out int consumed)
        {
            var reader = new Utf8JsonReader(data, final, state);
            consumed = 0;
            while (true)
            {
                var probe = reader;
                switch (_phase)
                {
                    case Phase.Start:
                        if (!probe.Read())
                        {
                            return Need(reader, ref state, out consumed);
                        }

                        if (probe.TokenType != JsonTokenType.StartObject)
                        {
                            throw new JsonException("An export is a JSON object.");
                        }

                        _phase = Phase.Properties;
                        break;

                    case Phase.Properties:
                        if (!probe.Read())
                        {
                            return Need(reader, ref state, out consumed);
                        }

                        if (probe.TokenType == JsonTokenType.EndObject)
                        {
                            _phase = Phase.Done;
                            Result ??= Walker().Finish();
                            consumed = (int)probe.BytesConsumed;
                            state = probe.CurrentState;
                            return true;
                        }

                        var name = probe.GetString();
                        if (!probe.Read())
                        {
                            return Need(reader, ref state, out consumed);
                        }

                        if (string.Equals(name, "events", StringComparison.OrdinalIgnoreCase) && probe.TokenType == JsonTokenType.StartArray)
                        {
                            _phase = Phase.Events;
                            if (Walker().Problem is { } header)
                            {
                                Result = header;
                                return true;
                            }

                            break;
                        }

                        var value = probe;
                        if (!probe.TrySkip())
                        {
                            return Need(reader, ref state, out consumed);
                        }

                        Header(name, ref value);
                        break;

                    case Phase.Events:
                        if (!probe.Read())
                        {
                            return Need(reader, ref state, out consumed);
                        }

                        if (probe.TokenType == JsonTokenType.EndArray)
                        {
                            _phase = Phase.Properties;
                            break;
                        }

                        var item = probe;
                        if (!probe.TrySkip())
                        {
                            return Need(reader, ref state, out consumed);
                        }

                        var exported = JsonSerializer.Deserialize<AuditExportEvent>(ref item, Json)
                                       ?? throw new JsonException("A record of the export is empty.");
                        if (Walker().Step(exported) is { } broken)
                        {
                            Result = broken;
                            return true;
                        }

                        break;

                    default:
                        return true;
                }

                reader = probe;
            }
        }

        private static bool Need(Utf8JsonReader reader, ref JsonReaderState state, out int consumed)
        {
            consumed = (int)reader.BytesConsumed;
            state = reader.CurrentState;
            return false;
        }

        private void Header(string? name, ref Utf8JsonReader value)
        {
            switch (name?.ToLowerInvariant())
            {
                case "format":
                    _format = value.TokenType == JsonTokenType.String ? value.GetString() : null;
                    break;
                case "fromindex":
                    _fromIndex = value.TokenType == JsonTokenType.Number ? value.GetInt64() : null;
                    break;
                case "toindex":
                    _toIndex = value.TokenType == JsonTokenType.Number ? value.GetInt64() : null;
                    break;
                case "anchors":
                    _anchors = JsonSerializer.Deserialize<List<AuditAnchor>>(ref value, Json) ?? [];
                    break;
            }
        }

        private Walk Walker() => _walk ??= new Walk(_format ?? string.Empty, _fromIndex ?? 0, _toIndex ?? 0, _anchors, ownAnchors)
        {
            HeaderMissing = _format is null || _fromIndex is null || _toIndex is null,
        };
    }

    /// <summary>
    /// The check itself, a record at a time in chain order: each follows the one before and is sealed after it. An anchor
    /// in range is matched with the record at its position as the walk passes it; a chain broken anywhere is said before
    /// an anchor that does not match, as the whole export's check says them.
    /// </summary>
    private sealed class Walk
    {
        private readonly string _format;
        private readonly long _fromIndex;
        private readonly long _toIndex;
        private readonly List<AuditAnchor> _anchors;
        private readonly Dictionary<long, List<int>> _anchorsAt = [];
        private readonly bool?[] _anchorMatched;
        private long _expected;
        private string? _previous;
        private bool _started;

        public Walk(string format, long fromIndex, long toIndex, IReadOnlyList<AuditAnchor> anchors, IReadOnlyList<AuditAnchor>? ownAnchors)
        {
            _format = format;
            _fromIndex = fromIndex;
            _toIndex = toIndex;
            _expected = fromIndex;
            _anchors = [.. anchors.Concat(ownAnchors ?? []).Where(a => a.ChainIndex >= fromIndex && a.ChainIndex <= toIndex)];
            _anchorMatched = new bool?[_anchors.Count];
            for (var i = 0; i < _anchors.Count; i++)
            {
                if (!_anchorsAt.TryGetValue(_anchors[i].ChainIndex, out var at))
                {
                    at = [];
                    _anchorsAt[_anchors[i].ChainIndex] = at;
                }

                at.Add(i);
            }
        }

        public bool HeaderMissing { get; init; }

        public long EventsChecked { get; private set; }

        /// <summary>What is wrong before any record is read, or null.</summary>
        public AuditExportCheck? Problem =>
            _format != AuditExport.FormatName
                ? new AuditExportCheck(false, 0, 0, $"This is not an HC360 audit export ({_format}).")
                : HeaderMissing
                    ? new AuditExportCheck(false, 0, 0, "The export does not say which positions it holds before its records.")
                    : null;

        /// <summary>Checks the next record; the outcome when it breaks the chain, else null.</summary>
        public AuditExportCheck? Step(AuditExportEvent exported)
        {
            if (Problem is { } problem)
            {
                return problem;
            }

            if (!_started)
            {
                _started = true;
                _previous = exported.PreviousHash;
                if (_fromIndex == 1 && _previous is not null)
                {
                    return new AuditExportCheck(false, 0, 0, "The first record of the chain cannot link to an earlier one.");
                }
            }

            var e = exported.ToEvent();
            if (exported.ChainIndex != _expected || !string.Equals(exported.PreviousHash, _previous, StringComparison.Ordinal) || !e.IsSealedAfter(_previous))
            {
                return new AuditExportCheck(false, EventsChecked, 0,
                    $"The chain is broken at position {_expected}: the record there was changed, removed or moved.");
            }

            if (_anchorsAt.TryGetValue(exported.ChainIndex, out var at))
            {
                foreach (var i in at)
                {
                    _anchorMatched[i] = string.Equals(_anchors[i].Hash, exported.Hash, StringComparison.Ordinal);
                }
            }

            _previous = exported.Hash;
            _expected++;
            EventsChecked++;
            return null;
        }

        /// <summary>The outcome once every record has been read.</summary>
        public AuditExportCheck Finish()
        {
            if (Problem is { } problem)
            {
                return problem;
            }

            if (EventsChecked > 0 && _expected - 1 != _toIndex)
            {
                return new AuditExportCheck(false, EventsChecked, 0, $"The export says it ends at position {_toIndex}, but its records end at {_expected - 1}.");
            }

            for (var i = 0; i < _anchors.Count; i++)
            {
                if (_anchorMatched[i] != true)
                {
                    return new AuditExportCheck(false, EventsChecked, _anchors.Count,
                        $"The record at position {_anchors[i].ChainIndex} is not the one anchored on {_anchors[i].AnchoredAt:u}: the chain was made again.");
                }
            }

            return new AuditExportCheck(true, EventsChecked, _anchors.Count,
                $"Positions {_fromIndex:N0} to {_toIndex:N0}: all {EventsChecked:N0} records are intact and linked" +
                (_anchors.Count == 0 ? ". No anchor falls in this range." : $", and match the {_anchors.Count:N0} anchor(s) in range."));
        }
    }
}

/// <summary>
/// Writes an export (<see cref="AuditExport"/>, the same JSON as the auditor's export) a record at a time, so a purge of
/// millions of records is written without holding them in memory: the header, its anchors, then the records as they come.
/// </summary>
public static class AuditExportWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>Writes the export to <paramref name="utf8Json"/>; returns how many records it holds.</summary>
    public static async Task<long> WriteAsync(
        Stream utf8Json, DateTimeOffset exportedAt, string? exportedBy, long fromIndex, long toIndex, IReadOnlyList<AuditAnchor> anchors,
        IAsyncEnumerable<AuditExportEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(events);
        await using var writer = new Utf8JsonWriter(utf8Json);
        writer.WriteStartObject();
        writer.WriteString("format", AuditExport.FormatName);
        writer.WritePropertyName("exportedAt");
        JsonSerializer.Serialize(writer, exportedAt, Json);
        writer.WriteString("exportedBy", exportedBy);
        writer.WriteNumber("fromIndex", fromIndex);
        writer.WriteNumber("toIndex", toIndex);
        writer.WriteString("algorithm", AuditExport.AlgorithmText);
        writer.WritePropertyName("anchors");
        JsonSerializer.Serialize(writer, anchors, Json);
        writer.WritePropertyName("events");
        writer.WriteStartArray();
        long count = 0;
        await foreach (var exported in events.WithCancellation(ct))
        {
            JsonSerializer.Serialize(writer, exported, Json);
            if (++count % 1000 == 0)
            {
                await writer.FlushAsync(ct);
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(ct);
        return count;
    }
}
