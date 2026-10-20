namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// First names for the patients tests register with the duplicate check on, as the front office does. Every test of a run
/// registers into the same database, and registration is refused as a possible duplicate when a patient there has the
/// same first name and date of birth, or the same first name and mobile number (FO-01, <see cref="PatientDuplicateCheckTests"/>).
/// A first name from <see cref="FirstName"/> is one no other patient of the run has, so the check runs and matches no one,
/// whatever date of birth the test gives. Only the same mobile number with an age within a year still matches, which the
/// tests' random mobile numbers make a chance of well under one in a million runs.
/// </summary>
internal static class TestPatients
{
    private static int _issued;

    /// <summary>
    /// <paramref name="name"/> and four letters that count up with every name given out: <c>Ravi</c> gives <c>Raviaaab</c>,
    /// the next call <c>Ashaaaac</c>, so no two first names from here are alike, even ignoring case.
    /// </summary>
    public static string FirstName(string name)
    {
        var number = Interlocked.Increment(ref _issued);
        var letters = new char[4];
        for (var i = letters.Length - 1; i >= 0; i--)
        {
            letters[i] = (char)('a' + (number % 26));
            number /= 26;
        }

        return name + new string(letters);
    }
}
