using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>
/// Tells people when the way they sign in changes (ASVS V6.4.3): a password changed or reset, two-step verification turned
/// on, off or reset. Someone who did not make the change learns of it and can tell IT. The notice goes by e-mail when the
/// account has an address, and in the app.
/// </summary>
internal static class CredentialNotices
{
    public const string TemplateKey = "auth.credentials_changed";

    public const string PasswordChanged = "Your password was changed.";
    public const string PasswordReset = "An administrator reset your password.";
    public const string MfaEnabled = "Two-step verification was turned on for your account.";
    public const string MfaDisabled = "Two-step verification was turned off for your account.";
    public const string MfaReset = "An administrator reset your two-step verification.";

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

    /// <summary>The in-app notice, written on its own: call it once the change is saved.</summary>
    public static Task SendInAppAsync(INotificationService notifications, User user, string change, CancellationToken ct) =>
        notifications.NotifyUsersAsync([user.Id], change, WhatToDo, "/account", "security", "warning", ct);
}
