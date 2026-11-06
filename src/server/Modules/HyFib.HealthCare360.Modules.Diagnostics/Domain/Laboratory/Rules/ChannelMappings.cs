namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>
/// How a test's channel mappings match what an analyser sends (pure; unit-tested). A plain code (<c>HGB</c>) maps that
/// channel from any analyser; an analyser's own code (<c>XN550:HGB</c>) maps it from that analyser only, and is preferred.
/// Codes are compared without regard to case.
/// </summary>
internal static class ChannelMappings
{
    /// <summary>0 = no match, 1 = a plain code matches, 2 = the analyser's own code matches (preferred).</summary>
    public static int Score(IEnumerable<string> mappings, string instrumentCode, string channelCode)
    {
        var best = 0;
        foreach (var mapping in mappings)
        {
            var separator = mapping.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                if (string.Equals(mapping, channelCode, StringComparison.OrdinalIgnoreCase))
                {
                    best = 1;
                }
            }
            else if (string.Equals(mapping[..separator], instrumentCode, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(mapping[(separator + 1)..], channelCode, StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
        }

        return best;
    }

    /// <summary>Whether any of the tests' mappings (one list per test) maps the analyser's channel.</summary>
    public static bool MapsAny(IEnumerable<IEnumerable<string>> tests, string instrumentCode, string channelCode) =>
        tests.Any(mappings => Score(mappings, instrumentCode, channelCode) > 0);
}
