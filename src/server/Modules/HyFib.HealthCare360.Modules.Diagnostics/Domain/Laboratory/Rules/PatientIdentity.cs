using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>Whose patient id an analyser sent, as the LIS found it among the registered patients.</summary>
public enum AnalyserPatientId
{
    /// <summary>
    /// No registered patient's, or none sent: the analyser's own number, a sample, visit or accession number, a
    /// placeholder, a typo. It says nothing about who the patient is, so the name decides.
    /// </summary>
    NoPatient = 0,

    /// <summary>The UHID of the tube's patient, or of a record merged with theirs.</summary>
    TubePatient,

    /// <summary>The UHID of another registered patient.</summary>
    AnotherPatient,
}

/// <summary>How the patient an analyser named for a sample compares with the tube's patient.</summary>
public enum PatientComparison
{
    /// <summary>Nothing to compare by: no registered patient's id, and no name on one side or the other.</summary>
    NotCompared = 0,

    /// <summary>The tube patient's UHID.</summary>
    SameId,

    /// <summary>No UHID to go by, and a name that can be the tube patient's.</summary>
    SameName,

    /// <summary>Another registered patient's UHID, whatever the name.</summary>
    OtherId,

    /// <summary>No UHID to go by, and a name that cannot be the tube patient's.</summary>
    OtherName,
}

/// <summary>
/// Whether the patient an analyser named for a sample can be the tube's patient (pure; unit-tested). Only evidence of
/// another patient counts against the tube, never the lack of evidence, since every false alarm teaches the bench to
/// wave the next one through:
/// <list type="bullet">
/// <item>The UHID decides when the analyser sent one: the tube patient's (or a record merged with theirs) is the same
/// patient whatever the name, as names get corrected after an order is sent; another registered patient's is another
/// patient, even when the names agree, since two patients of one name are exactly the mix-up this check is for. An id
/// no registered patient has is not a UHID (the analyser's own number, a visit or sample number) and says nothing.</item>
/// <item>Without a UHID to go by, the names are compared part by part (<see cref="NamesAgree"/>), and only a name that
/// cannot be the tube patient's counts against it.</item>
/// </list>
/// </summary>
internal static class PatientIdentity
{
    /// <summary>Name parts compared at most: more is not a name, and the comparison stays quick.</summary>
    private const int MaxParts = 6;

    /// <summary>Short parts at most read as initials written together, on each side.</summary>
    private const int MaxJoinedInitials = 3;

    /// <summary>
    /// Titles, the "baby of" a newborn is registered under, and the words analysers fill an empty name field with: not
    /// parts of the name. Leaving a word out can only make two names agree, never disagree.
    /// </summary>
    private static readonly HashSet<string> NotNameParts = new(StringComparer.Ordinal)
    {
        "MR", "MRS", "MS", "MISS", "MASTER", "MSTR", "MX", "DR", "PROF", "SHRI", "SRI", "SMT", "KUM", "BABY", "OF",
        "UNKNOWN", "UNK", "NONE", "NIL", "NULL", "NA", "NAME", "PATIENT", "TEST",
    };

    /// <summary>
    /// How the analyser's patient compares with the tube's: by the UHID when the analyser sent a registered patient's
    /// (<paramref name="id"/>, looked up by the caller), else by the names (<see cref="NamesAgree"/>).
    /// </summary>
    public static PatientComparison Compare(AnalyserPatientId id, string? sentName, string? tubeName) => id switch
    {
        AnalyserPatientId.TubePatient => PatientComparison.SameId,
        AnalyserPatientId.AnotherPatient => PatientComparison.OtherId,
        _ => NamesAgree(sentName, tubeName) switch
        {
            true => PatientComparison.SameName,
            false => PatientComparison.OtherName,
            null => PatientComparison.NotCompared,
        },
    };

    /// <summary>
    /// A patient id as compared and looked up: without spaces (analysers pad their fields), in capitals (UHIDs are
    /// issued in capitals, and analysers may change the case).
    /// </summary>
    public static string NormaliseId(string? id) =>
        id is null ? string.Empty : string.Concat(id.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    /// <summary>
    /// Whether the patient who has the id the analyser sent is the tube's patient: the same record, one merged into the
    /// other, or both merged into a third. A merged record is never merged again, so one step either way is all there
    /// is, and lab tubes stay on the record they were drawn for.
    /// </summary>
    public static bool SamePerson(Guid owner, Guid? ownerMergedInto, Guid tubePatient, Guid? tubePatientMergedInto) =>
        owner == tubePatient
        || ownerMergedInto == tubePatient
        || tubePatientMergedInto == owner
        || (ownerMergedInto is { } survivor && survivor == tubePatientMergedInto);

    /// <summary>
    /// Whether two names can be one person's; null when either has no part to compare (<see cref="NameParts"/>). Each
    /// part of the name with fewer parts must match a part of its own in the other, in any order: the same part, an
    /// initial of it, or a shortening of it of three letters or more (analysers cut a long name at the length of their
    /// field). Short parts may be initials written together ("KV" for "K V"). A name that only lacks parts, or has more,
    /// is not another person's: "SHARMA^RAVI", "R. Sharma", "Ravi K Sharma" and "Ravi Kumar Sharm" can all be Ravi Kumar
    /// Sharma's, while "Ravi Verma" and "Meera Sharma" cannot.
    /// </summary>
    public static bool? NamesAgree(string? sent, string? registered)
    {
        var sentParts = NameParts(sent);
        var registeredParts = NameParts(registered);
        if (sentParts.Count == 0 || registeredParts.Count == 0)
        {
            return null;
        }

        var registeredReadings = Readings(registeredParts).ToList();
        return Readings(sentParts).Any(a => registeredReadings.Any(b => Fits(a, b)));
    }

    /// <summary>
    /// The parts of a name as compared: letters only, in capitals and without accents (José is JOSE), split at anything
    /// else (spaces, the delimiters of the analyser's name fields, dots, hyphens, digits), titles (Mr, Mrs, Dr, Smt,
    /// Shri, "Baby of" a newborn) and the placeholders of an empty name field (Unknown, None, Patient) left out. A name in
    /// another script than the Latin one has no parts to compare. At most six.
    /// </summary>
    public static IReadOnlyList<string> NameParts(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        var letters = new StringBuilder(name.Length);
        foreach (var c in Decomposed(name))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var upper = char.ToUpperInvariant(c);
            letters.Append(upper is >= 'A' and <= 'Z' ? upper : ' ');
        }

        return [.. letters.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(part => !NotNameParts.Contains(part)).Take(MaxParts)];
    }

    /// <summary>
    /// The name with its accents as marks of their own (é as e and an acute accent); as it is when it is not well-formed
    /// Unicode, which an analyser's text can be, so the comparison never fails a message.
    /// </summary>
    private static string Decomposed(string name)
    {
        try
        {
            return name.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            return name;
        }
    }

    /// <summary>
    /// The ways to read a name's parts: as written, and with some of its two- or three-letter parts read as initials
    /// written together ("KV RAO" as "K V RAO").
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> Readings(IReadOnlyList<string> parts)
    {
        var joined = Enumerable.Range(0, parts.Count).Where(i => parts[i].Length is 2 or 3).Take(MaxJoinedInitials).ToList();
        for (var mask = 0; mask < (1 << joined.Count); mask++)
        {
            var split = joined.Where((_, bit) => (mask & (1 << bit)) != 0).ToHashSet();
            yield return [.. parts.SelectMany((part, i) => split.Contains(i) ? part.Select(letter => letter.ToString()) : Enumerable.Repeat(part, 1))];
        }
    }

    /// <summary>Whether each part of the name with fewer parts matches a part of the other of its own (one to one).</summary>
    private static bool Fits(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var (fewer, more) = a.Count <= b.Count ? (a, b) : (b, a);

        // A matching found by augmenting paths (Kuhn's algorithm): owner[j] is the part of the shorter name matched to
        // part j of the longer one.
        var owner = new int[more.Count];
        Array.Fill(owner, -1);
        for (var i = 0; i < fewer.Count; i++)
        {
            if (!Augment(i, new bool[more.Count]))
            {
                return false;
            }
        }

        return true;

        bool Augment(int i, bool[] seen)
        {
            for (var j = 0; j < more.Count; j++)
            {
                if (seen[j] || !SamePart(fewer[i], more[j]))
                {
                    continue;
                }

                seen[j] = true;
                if (owner[j] < 0 || Augment(owner[j], seen))
                {
                    owner[j] = i;
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The same part, an initial of it, or a shortening of it of three letters or more.</summary>
    private static bool SamePart(string a, string b)
    {
        if (a == b)
        {
            return true;
        }

        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length == 1 ? longer[0] == shorter[0] : shorter.Length >= 3 && longer.StartsWith(shorter, StringComparison.Ordinal);
    }
}
