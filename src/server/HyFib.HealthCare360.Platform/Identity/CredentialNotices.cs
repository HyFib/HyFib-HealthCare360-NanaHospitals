using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>
/// Tells people when the way they sign in changes (ASVS V6.4.3): a password changed or reset, two-step verification turned
/// on, off or reset, the mobile number or e-mail address on the account changed. Someone who did not make the change
/// learns of it and can tell IT. The notice goes by e-mail when the account has an address, and in the app.
/// </summary>
internal static class CredentialNotices
{
    public const string TemplateKey = "auth.credentials_changed";

    /// <summary>A temporary password an administrator made, sent to the account's own mobile number and e-mail address.</summary>
    public const string TemporaryPasswordTemplateKey = "auth.temporary_password";

    /// <summary>The account's mobile number or e-mail address changed, told at the ones it had before.</summary>
    public const string ContactChangedTemplateKey = "auth.contact_changed";

    public const string PasswordChanged = "Your password was changed.";
    public const string PasswordReset = "An administrator reset your password.";
    public const string MfaEnabled = "Two-step verification was turned on for your account.";
    public const string MfaDisabled = "Two-step verification was turned off for your account.";
    public const string MfaReset = "An administrator reset your two-step verification.";
    public const string ContactChanged = "The mobile number or e-mail address on your account changed.";

    private const string WhatToDo = "If you did not do this, tell your IT administrator at once.";

    /// <summary>Queues the e-mail with the caller's other changes, so it goes only if they are saved.</summary>
    public static void QueueEmail(INotificationService notifications, User user, string change, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return;
        }

        notifications.Queue(
            TemplateKey,
            new NotificationRecipient(null, user.Email, null, user.DisplayName),
            new Dictionary<string, string>
            {
                ["user"] = user.UserName,
                ["change"] = change,
                ["time"] = DocumentFormat.DateTime(at),
            },
            [NotificationChannel.Email],
            facilityId: user.DefaultFacilityId);
    }

    /// <summary>
    /// Sends a temporary password to the account's own mobile number by SMS and to its e-mail address, whichever it has,
    /// with the caller's other changes, so it goes only if they are saved; a message not delivered by the time the password
    /// stops working is not sent. Returns where it went, masked, for the administrator, who never sees the password: an
    /// administrator who could read it could sign in as the account's holder.
    /// </summary>
    public static IReadOnlyList<string> QueueTemporaryPassword(INotificationService notifications, User user, string password, DateTimeOffset expiresAt, DateTimeOffset at)
    {
        List<NotificationChannel> channels = [];
        List<string> sentTo = [];
        if (!string.IsNullOrWhiteSpace(user.Phone))
        {
            channels.Add(NotificationChannel.Sms);
            sentTo.Add($"mobile {Phone.Mask(user.Phone)}");
        }

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            channels.Add(NotificationChannel.Email);
            sentTo.Add($"e-mail {Masking.MaskEmail(user.Email)}");
        }

        notifications.Queue(
            TemporaryPasswordTemplateKey,
            new NotificationRecipient(user.Phone, user.Email, null, user.DisplayName),
            new Dictionary<string, string>
            {
                ["user"] = user.UserName,
                ["password"] = password,
                ["expires"] = DocumentFormat.DateTime(expiresAt),
            },
            channels,
            expiresIn: expiresAt - at,
            facilityId: user.DefaultFacilityId);
        return sentTo;
    }

    /// <summary>
    /// Tells the mobile number and e-mail address the account had before that they changed, with the caller's other changes:
    /// whoever had them changed to take the account over cannot keep it from its holder. The new ones are named masked.
    /// </summary>
    public static void QueueContactChanged(INotificationService notifications, User user, string? emailBefore, string? phoneBefore, DateTimeOffset at)
    {
        List<string> changes = [];
        if (!string.Equals(emailBefore, user.Email, StringComparison.Ordinal))
        {
            changes.Add(user.Email is null
                ? "The e-mail address on your account was removed."
                : $"The e-mail address on your account was changed to {Masking.MaskEmail(user.Email)}.");
        }

        if (!string.Equals(phoneBefore, user.Phone, StringComparison.Ordinal))
        {
            changes.Add(user.Phone is null
                ? "The mobile number on your account was removed."
                : $"The mobile number on your account was changed to {Phone.Mask(user.Phone)}.");
        }

        List<NotificationChannel> channels = [];
        if (!string.IsNullOrWhiteSpace(phoneBefore))
        {
            channels.Add(NotificationChannel.Sms);
        }

        if (!string.IsNullOrWhiteSpace(emailBefore))
        {
            channels.Add(NotificationChannel.Email);
        }

        if (changes.Count == 0 || channels.Count == 0)
        {
            return;
        }

        notifications.Queue(
            ContactChangedTemplateKey,
            new NotificationRecipient(phoneBefore, emailBefore, null, user.DisplayName),
            new Dictionary<string, string>
            {
                ["user"] = user.UserName,
                ["change"] = string.Join(" ", changes),
                ["time"] = DocumentFormat.DateTime(at),
            },
            channels,
            facilityId: user.DefaultFacilityId);
    }

    /// <summary>The in-app notice, written on its own: call it once the change is saved.</summary>
    public static Task SendInAppAsync(INotificationService notifications, User user, string change, CancellationToken ct) =>
        notifications.NotifyUsersAsync([user.Id], change, WhatToDo, "/account", "security", "warning", ct);

    /// <summary>
    /// The in-app notice, added with the caller's other changes, for a change made in a transaction already open (an
    /// approval's). It is not pushed to the user's screens: the change ends their sessions, or they see it when they next
    /// open their notifications.
    /// </summary>
    public static void AddInApp(HospitalDbContext db, User user, string change, DateTimeOffset at) =>
        db.Add(new UserNotification
        {
            UserId = user.Id,
            Title = change,
            Body = WhatToDo,
            Link = "/account",
            Category = "security",
            Severity = "warning",
            CreatedAt = at,
        });
}
