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

        var known = await db.Set<AnalyserChannel>().AsNoTracking()
            .Where(c => c.InstrumentId == instrument.Id && unmapped.Contains(c.ChannelCode))
            .Select(c => c.ChannelCode)
            .ToListAsync(ct);
        var ids = unmapped.Select(_ => Guid.CreateVersion7()).ToArray();
        var codes = unmapped.ToArray();
        var values = unmapped.Select(c => TextLimits.ClipOrNull(sent[c].Value.Trim(), AnalyserChannel.ValueLength)).ToArray();
        var units = unmapped.Select(c => TextLimits.ClipOrNull(sent[c].Units?.Trim(), AnalyserChannel.UnitLength)).ToArray();
        var now = time.GetUtcNow();
        try
        {
            // One statement for the message, so two messages of one analyser at once count each channel twice, never fail.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO diagnostics.lab_analyser_channels
                    (id, facility_id, instrument_id, channel_code, first_seen_at, last_seen_at, times_seen, last_value, last_unit)
                SELECT u.id, {instrument.FacilityId}, {instrument.Id}, u.channel_code, {now}, {now}, 1, u.last_value, u.last_unit
                FROM unnest({ids}, {codes}, {values}, {units}) AS u (id, channel_code, last_value, last_unit)
                ON CONFLICT (instrument_id, channel_code) DO UPDATE
                SET last_seen_at = EXCLUDED.last_seen_at, times_seen = lab_analyser_channels.times_seen + 1,
                    last_value = EXCLUDED.last_value, last_unit = EXCLUDED.last_unit
                """, ct);
        }
        catch (DbException ex)
        {
            // The results are saved already; the list is filled again by the analyser's next message.
            logger.LogWarning(ex, "Could not note the channels {Channels} that analyser {Instrument} sent and no test maps",
                string.Join(", ", unmapped), instrument.Code);
            return;
        }

        var first = unmapped.Except(known, StringComparer.Ordinal).ToList();
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
}
