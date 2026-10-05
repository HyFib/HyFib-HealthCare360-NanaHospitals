namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>
/// Which analysers HC360 sends a sample's orders to without being asked (INT-LAB order download), and under which
/// channel codes. Pure; unit-tested.
/// <list type="bullet">
/// <item>An analyser runs an analyte when the test maps the analyser's own channel (<c>INSTR:CH</c>), or maps a plain
/// code (<c>CH</c>) and the analyser works on the tube's bench.</item>
/// <item>Plain codes are never sent to an analyser on another bench, so a haematology analyser is not sent a chemistry
/// order just because both benches use plain codes.</item>
/// <item>Only active, bidirectional analysers are told (Bidirectional in Lab setup is the one switch for host queries
/// and order download).</item>
/// </list>
/// Host queries stay as they were (<see cref="LabTest.ChannelFor(string)"/>): there the analyser asks about a tube it
/// already holds.
/// </summary>
internal static class AnalyserRouting
{
    public static string? Channel(LabTest analyte, LabInstrument analyser, LabSection tubeSection) =>
        analyte.ChannelFor(analyser.Code, allowPlain: analyser.Section == tubeSection);

    public static IReadOnlyList<string> Channels(IEnumerable<LabTest> analytes, LabInstrument analyser, LabSection tubeSection) =>
        analytes.Select(analyte => Channel(analyte, analyser, tubeSection))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The analysers to tell about these analytes, each with its channel codes, in code order.
    /// <paramref name="except"/> leaves out the analyser that already has the sample (its results received the tube).
    /// </summary>
    public static IReadOnlyList<InstrumentOrderTarget> Targets(
        IEnumerable<LabInstrument> analysers, LabSection tubeSection, IReadOnlyCollection<LabTest> analytes, string? except = null) =>
        analysers
            .Where(a => a.IsActive && a.IsBidirectional && !string.Equals(a.Code, except, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Code, StringComparer.Ordinal)
            .Select(a => new InstrumentOrderTarget(a.Code, Channels(analytes, a, tubeSection)))
            .Where(target => target.ChannelCodes.Count > 0)
            .ToList();
}
