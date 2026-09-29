using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Integrations.WhatsApp;

/// <summary>Outbox payload: confirm a STOP (<paramref name="OptedOut"/>) or START to the number that sent it.</summary>
internal sealed record WhatsAppReplyConfirmation(Guid MessageId, string Phone, bool OptedOut, Guid? FacilityId);

/// <summary>
/// Tells the sender in plain words what their STOP or START did. It is a free-form text, which WhatsApp allows only
/// within 24 hours of the person's message, so the outbox message expires with that window. It goes straight to the
/// WhatsApp sender, not through the notification pipeline, so the opt-out it confirms does not stop it.
/// </summary>
internal sealed class WhatsAppReplyConfirmationHandler(HospitalDbContext db, IWhatsAppSender whatsApp) : IOutboxHandler
{
    public const string MessageType = "whatsapp.reply_confirmation";

    /// <summary>WhatsApp's 24-hour customer-service window, less a margin for clock differences.</summary>
    public static readonly TimeSpan ReplyWindow = TimeSpan.FromHours(23);

    public string Type => MessageType;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var confirmation = message.Read<WhatsAppReplyConfirmation>();
        var hospital = await HospitalNameAsync(confirmation.FacilityId, cancellationToken);
        var result = await whatsApp.SendTextAsync(
            new WhatsAppTextMessage(confirmation.Phone, Text(confirmation.OptedOut, hospital), confirmation.MessageId.ToString("N"), confirmation.FacilityId),
            cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error ?? "The WhatsApp confirmation was not sent.");
        }
    }

    internal static string Text(bool optedOut, string hospital) => optedOut
        ? $"You won't get WhatsApp messages or offers from {hospital} any more, except sign-in codes. Reply START to get them again."
        : $"You'll get WhatsApp messages from {hospital} again. Reply STOP to stop them.";

    private async Task<string> HospitalNameAsync(Guid? facilityId, CancellationToken cancellationToken)
    {
        var facilities = db.Set<Facility>().AsNoTracking();
        var name = facilityId is { } id
            ? await facilities.Where(f => f.Id == id).Select(f => f.Name).FirstOrDefaultAsync(cancellationToken)
            : await facilities.OrderBy(f => f.CreatedAt).Select(f => f.Name).FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(name) ? "the hospital" : name.Trim();
    }
}
