using HyFib.HealthCare360.Integrations.Domain;

namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>The training gateway's disputes, shaped like Razorpay's: answer within <see cref="AnswerDays"/> days of being raised.</summary>
internal static class SimulatorDisputes
{
    public const int AnswerDays = 7;

    public static GatewayDispute ToGateway(SimulatorTransaction dispute)
    {
        var (status, word) = dispute.Status switch
        {
            SimulatorTransactionStatus.UnderReview => (GatewayDisputeStatus.UnderReview, "under_review"),
            SimulatorTransactionStatus.Won => (GatewayDisputeStatus.Won, "won"),
            SimulatorTransactionStatus.Lost => (GatewayDisputeStatus.Lost, "lost"),
            _ => (GatewayDisputeStatus.Open, "open"),
        };
        return new GatewayDispute(
            dispute.ExternalId,
            dispute.Reference ?? string.Empty,
            dispute.Amount,
            dispute.Description,
            "chargeback",
            status,
            word,
            dispute.CreatedAt.AddDays(AnswerDays),
            dispute.CreatedAt,
            status is GatewayDisputeStatus.UnderReview or GatewayDisputeStatus.Won ? dispute.CompletedAt : null);
    }
}
