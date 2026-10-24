namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// A patient's contact details as a member of staff may see them (SRS PRV-03, minimum necessary): in full with
/// <see cref="PatientsPermissions.ContactRead"/>, held by those who contact or bill patients; masked for everyone else, who
/// keep what they work with (the city, the last digits of a number to confirm it with the patient).
/// </summary>
public static class PatientContacts
{
    /// <summary>A phone number: in full, or with all but its last four digits hidden (+91******3210).</summary>
    public static string? Phone(string? e164, bool full) => full || e164 is null ? e164 : SharedKernel.Phone.Mask(e164);

    /// <summary>An email address: in full, or with its name hidden but its first letter (a***@example.org).</summary>
    public static string? Email(string? email, bool full)
    {
        if (full || string.IsNullOrEmpty(email))
        {
            return email;
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : string.Concat(email.AsSpan(0, 1), "***", email.AsSpan(at));
    }

    /// <summary>An address: in full, or the town, district, state and country without the street and PIN code.</summary>
    public static Address Address(Address address, bool full) =>
        full ? address : address with { Line1 = null, Line2 = null, PinCode = null };
}

/// <summary>Whether the signed-in member of staff sees patients' contact details in full (<see cref="PatientContacts"/>).</summary>
public sealed class PatientContactAccess(ICurrentUser currentUser, IPermissionService permissions)
{
    private bool? _full;

    public async ValueTask<bool> FullAsync(CancellationToken ct) =>
        _full ??= currentUser.UserId is { } userId && await permissions.HasPermissionAsync(userId, PatientsPermissions.ContactRead, ct);
}
