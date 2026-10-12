using System.Globalization;

namespace HyFib.HealthCare360.WindowsInterop.Updates;

/// <summary>
/// A release version as HC360 numbers them: <c>1.2.3</c>, a fourth number allowed, and a pre-release label after a dash
/// (<c>1.3.0-rc.1</c>), which comes before the release itself. Build metadata after a plus (<c>+abc123</c>) is ignored.
/// Missing numbers count as 0, so <c>1.2</c> and <c>1.2.0</c> are the same version.
/// </summary>
public readonly record struct ReleaseVersion : IComparable<ReleaseVersion>
{
    private ReleaseVersion(Version numbers, string? label)
    {
        Numbers = numbers;
        Label = label;
    }

    public Version Numbers { get; }

    /// <summary>The pre-release label (<c>rc.1</c>), or null for a release.</summary>
    public string? Label { get; }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a release version.");

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
        {
            return false;
        }

        var value = text.Trim();
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            value = value[..plus];
        }

        string? label = null;
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            label = value[(dash + 1)..];
            value = value[..dash];
            if (label.Length == 0 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            {
                return false;
            }
        }

        var parts = value.Split('.');
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length is 0 or > 9 || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new ReleaseVersion(new Version(numbers[0], numbers[1], numbers[2], numbers[3]), label);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var numbers = (Numbers ?? new Version()).CompareTo(other.Numbers ?? new Version());
        if (numbers != 0)
        {
            return numbers;
        }

        // A release comes after its pre-releases.
        return (Label, other.Label) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => CompareLabels(Label, other.Label),
        };
    }

    public override string ToString()
    {
        var numbers = Numbers ?? new Version();
        var text = numbers.Revision > 0 ? numbers.ToString(4) : numbers.ToString(3);
        return Label is null ? text : $"{text}-{Label}";
    }

    // As semantic versioning orders them: identifier by identifier, numbers numerically and before words.
    private static int CompareLabels(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumber = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var x);
            var bNumber = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var y);
            var order = (aNumber, bNumber) switch
            {
                (true, true) => x.CompareTo(y),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (order != 0)
            {
                return order;
            }
        }

        return a.Length.CompareTo(b.Length);
    }
}
