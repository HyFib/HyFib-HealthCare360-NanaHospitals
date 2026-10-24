namespace HyFib.HealthCare360.Modules.Patients.Domain;

/// <summary>How a patient's name shows to someone who may not open the record.</summary>
public static class PatientNames
{
    /// <summary>
    /// A restricted patient's name as search shows it to staff who may not open the record: the initials, so the front
    /// desk can tell two results apart, and that it is restricted. "Ravi Kumar Sharma" becomes "R. K. S. (restricted)".
    /// </summary>
    public static string Restricted(string fullName)
    {
        var initials = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => char.IsLetter(part[0]))
            .Select(part => $"{char.ToUpperInvariant(part[0])}.");
        var shown = string.Join(' ', initials);
        return shown.Length == 0 ? "Restricted patient" : $"{shown} (restricted)";
    }
}
