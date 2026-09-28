namespace HyFib.HealthCare360.Modules.Engagement.Domain;

/// <summary>
/// Decides whether an open lead and a patient who just booked are the same person. Leads are found by mobile number,
/// but Indian families often share one number, so the number alone would mark a son's enquiry as converted by his
/// mother's appointment. The first names must also agree: either the same word, or the same initial ("R. Kumar")
/// plus another shared word such as the surname. Honorifics and punctuation are ignored.
/// </summary>
public static class LeadMatching
{
    public static bool IsSamePerson(string? leadName, string? patientName)
    {
        var lead = Tokens(leadName);
        var patient = Tokens(patientName);
        if (lead.Length == 0 || patient.Length == 0)
        {
            return false;
        }

        if (lead[0].Length > 1 && patient[0].Length > 1)
        {
            return lead[0] == patient[0];
        }

        return lead[0][0] == patient[0][0] && lead.Skip(1).Intersect(patient.Skip(1)).Any();
    }

    private static string[] Tokens(string? name) =>
        ReferralNameMatcher.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
