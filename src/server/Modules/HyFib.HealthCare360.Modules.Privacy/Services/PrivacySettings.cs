using System.Text.Json;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Privacy.Services;

/// <summary>
/// The person people can contact about their personal data: the grievance officer, or the Data Protection Officer
/// where the hospital has one (DPDP Act s8(9); DPDP Rules 2025, rule 9). Published on the portal's privacy notice and
/// given in every reply to a privacy request.
/// </summary>
public sealed record PrivacyContact(string Title, string? Name, string? Email, string? Phone, string? Address);

/// <summary>
/// The privacy settings: the contact, and the published period for answering requests (at most 90 days). Each is a
/// setting of its own, with its version (REL-04): none until it is first saved.
/// </summary>
public sealed record PrivacySettingsDto(PrivacyContact Contact, int ResponseDays, uint? Version = null, uint? ResponseDaysVersion = null);

/// <summary>Reads and saves the privacy settings. Values are read leniently, so a malformed setting falls back to the default.</summary>
public sealed class PrivacySettings(ISettingsService settings, HospitalDbContext db)
{
    public const string ContactSetting = "privacy.contact";
    public const string ResponseDaysSetting = "privacy.responseDays";
    public const int DefaultResponseDays = 30;
    public const string DefaultTitle = "Grievance Officer";

    public async Task<PrivacySettingsDto> GetAsync(CancellationToken ct)
    {
        var contact = await settings.GetAsync<JsonElement?>(ContactSetting, null, ct);
        var days = await settings.GetAsync<JsonElement?>(ResponseDaysSetting, null, ct);
        return new PrivacySettingsDto(
            ReadContact(contact), ReadDays(days), await SettingVersions.OfAsync(db, ContactSetting, null, ct), await SettingVersions.OfAsync(db, ResponseDaysSetting, null, ct));
    }

    public async Task<int> ResponseDaysAsync(CancellationToken ct) => ReadDays(await settings.GetAsync<JsonElement?>(ResponseDaysSetting, null, ct));

    /// <summary>
    /// The contact as published. Until an officer is named, the hospital's own e-mail, phone and address stand in, so
    /// there is always someone to reach.
    /// </summary>
    public async Task<PrivacyContact> PublishedContactAsync(CancellationToken ct)
    {
        var contact = (await GetAsync(ct)).Contact;
        if (contact.Email is not null && contact.Phone is not null && contact.Address is not null)
        {
            return contact;
        }

        var hospital = await db.Set<Facility>().AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.CreatedAt)
            .Select(f => new { f.Email, f.Phone, f.Address })
            .FirstOrDefaultAsync(ct);
        return contact with
        {
            Email = contact.Email ?? hospital?.Email,
            Phone = contact.Phone ?? hospital?.Phone,
            Address = contact.Address ?? (hospital?.Address.ToString() is { Length: > 0 } address ? address : null),
        };
    }

    /// <summary>Saves both, or neither if either changed since <paramref name="value"/> was loaded (409).</summary>
    public async Task SaveAsync(PrivacySettingsDto value, CancellationToken ct)
    {
        var contact = value.Contact;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await settings.SetAsync(ContactSetting, new PrivacyContact(
            Clean(contact.Title) ?? DefaultTitle, Clean(contact.Name), Clean(contact.Email), Phone.Normalize(contact.Phone), Clean(contact.Address)), null, false,
            value.Version, ct);
        await settings.SetAsync(ResponseDaysSetting, Math.Clamp(value.ResponseDays, 1, DataRequest.MaxResponseDays), null, false, value.ResponseDaysVersion, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>One line naming whom to contact, for messages: "Grievance Officer, privacy@hospital.in".</summary>
    public static string ContactLine(PrivacyContact contact) =>
        string.Join(", ", new[] { contact.Name ?? contact.Title, contact.Email, contact.Phone }.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static PrivacyContact ReadContact(JsonElement? json)
    {
        if (json is not { ValueKind: JsonValueKind.Object } value)
        {
            return new PrivacyContact(DefaultTitle, null, null, null, null);
        }

        return new PrivacyContact(Text(value, "title") ?? DefaultTitle, Text(value, "name"), Text(value, "email"), Text(value, "phone"), Text(value, "address"));
    }

    private static int ReadDays(JsonElement? json)
    {
        var days = json switch
        {
            { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var d) => d,
            { ValueKind: JsonValueKind.String } s when int.TryParse(s.GetString(), out var d) => d,
            _ => DefaultResponseDays,
        };
        return Math.Clamp(days, 1, DataRequest.MaxResponseDays);
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? Clean(property.GetString()) : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
