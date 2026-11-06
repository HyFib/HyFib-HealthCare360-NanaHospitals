using System.Data.Common;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Keeps the list of channels each analyser sends that no active test maps (<see cref="AnalyserChannel"/>), from the
/// readings of a message that went on no test of the tube. A channel that maps a test the tube did not need is not
/// listed: analysers send whole profiles. The first time an analyser sends a channel, whoever manages the lab's masters
/// is told, since its values go on no report until the channel is mapped or ignored in Lab setup.
/// </summary>
internal sealed class AnalyserChannelSightings(
    HospitalDbContext db,
    INotificationService notifications,
    TimeProvider time,
    ILogger<AnalyserChannelSightings> logger)
{
    public async Task NoteAsync(LabInstrument instrument, IReadOnlyCollection<InstrumentResult> readings, CancellationToken ct)
    {
        // The last reading of each channel, as the catalogue's mappings are written: trimmed and in upper case.
        var sent = new Dictionary<string, InstrumentResult>(StringComparer.Ordinal);
        foreach (var reading in readings)
        {
            var channel = reading.ChannelCode.Trim().ToUpperInvariant();
            if (channel.Length > 0 && channel.Length <= AnalyserChannel.ChannelCodeLength)
            {
                sent[channel] = reading;
            }
        }

        if (sent.Count == 0)
        {
            return;
        }

        var catalogue = await db.Set<LabTest>().AsNoTracking()
            .Where(t => t.IsActive && t.InstrumentChannelCodes.Count > 0)
            .Select(t => t.InstrumentChannelCodes)
            .ToListAsync(ct);
        var unmapped = sent.Keys.Where(channel => !ChannelMappings.MapsAny(catalogue, instrument.Code, channel)).Order(StringComparer.Ordinal).ToList();
        if (unmapped.Count == 0)
        {
            return;
        }

        var values = unmapped.Select(c => TextLimits.ClipOrNull(sent[c].Value.Trim(), AnalyserChannel.ValueLength)).ToArray();
        var units = unmapped.Select(c => TextLimits.ClipOrNull(sent[c].Units?.Trim(), AnalyserChannel.UnitLength)).ToArray();
        List<string> first;
        try
        {
            first = await UpsertAsync(instrument, unmapped, values, units, ct);
        }
        catch (DbException ex)
        {
            // The results are saved already; the list is filled again by the analyser's next message.
            logger.LogWarning(ex, "Could not note the channels {Channels} that analyser {Instrument} sent and no test maps",
                string.Join(", ", unmapped), instrument.Code);
            return;
        }

        if (first.Count > 0)
        {
            await notifications.NotifyPermissionHoldersAsync(
                DiagnosticsPermissions.LabMastersManage,
                instrument.FacilityId,
                $"{instrument.Code} sends {(first.Count == 1 ? "a channel" : "channels")} no test maps",
                $"{instrument.Name} ({instrument.Code}) sent {string.Join(", ", first)}, which no test in the catalogue maps. "
                + "Its values go on no report until each channel is mapped to a test, or ignored, in Lab setup › Analysers.",
                "/lab/setup?tab=analysers",
                "diagnostics",
                "warning",
                ct);
        }
    }

    /// <summary>
    /// Counts the channels against the analyser in one statement and returns those it inserted, sent for the first time.
    /// The row lock decides between two messages at once: the second counts the channel twice, and only the first finds
    /// it new, so whoever manages the masters is told once.
    /// </summary>
    private async Task<List<string>> UpsertAsync(
        LabInstrument instrument, IReadOnlyList<string> channels, string?[] values, string?[] units, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO diagnostics.lab_analyser_channels
                    (id, facility_id, instrument_id, channel_code, first_seen_at, last_seen_at, times_seen, last_value, last_unit)
                SELECT u.id, @facility, @instrument, u.channel_code, @now, @now, 1, u.last_value, u.last_unit
                FROM unnest(@ids, @codes, @values, @units) AS u (id, channel_code, last_value, last_unit)
                ON CONFLICT (instrument_id, channel_code) DO UPDATE
                SET last_seen_at = EXCLUDED.last_seen_at, times_seen = lab_analyser_channels.times_seen + 1,
                    last_value = EXCLUDED.last_value, last_unit = EXCLUDED.last_unit
                RETURNING channel_code, times_seen
                """;
            Add(command, "facility", instrument.FacilityId);
            Add(command, "instrument", instrument.Id);
            Add(command, "now", time.GetUtcNow());
            Add(command, "ids", channels.Select(_ => Guid.CreateVersion7()).ToArray());
            Add(command, "codes", channels.ToArray());
            Add(command, "values", values);
            Add(command, "units", units);

            var first = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.GetInt32(1) == 1)
                {
                    first.Add(reader.GetString(0));
                }
            }

            return [.. first.Order(StringComparer.Ordinal)];
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
