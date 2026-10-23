using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Billing.Services.EventHandlers;

// In-patient lifecycle events from the Inpatient module. Each runs inside the admission transaction (before commit).

/// <summary>Opens the IP billing account (tariff by payer, policy, bed class) when a patient is admitted (IPB-01).</summary>
internal sealed class PatientAdmittedHandler(IpBillingService ip) : IDomainEventHandler<PatientAdmitted>
{
    public Task HandleAsync(PatientAdmitted domainEvent, CancellationToken cancellationToken) => ip.OpenAccountAsync(domainEvent, cancellationToken);
}

/// <summary>Follows bed transfers so later services are priced for the new room category.</summary>
internal sealed class PatientTransferredHandler(IpBillingService ip) : IDomainEventHandler<PatientTransferred>
{
    public Task HandleAsync(PatientTransferred domainEvent, CancellationToken cancellationToken) => ip.MoveBedAsync(domainEvent, cancellationToken);
}

/// <summary>Drafts the final bill (package vs actuals, payer split) as soon as discharge is initiated (IPB-07).</summary>
internal sealed class DischargeInitiatedHandler(IpBillingService ip) : IDomainEventHandler<DischargeInitiated>
{
    public Task HandleAsync(DischargeInitiated domainEvent, CancellationToken cancellationToken) => ip.DischargeInitiatedAsync(domainEvent, cancellationToken);
}

/// <summary>Takes the account back to open when the discharge is revoked: the stay goes on.</summary>
internal sealed class DischargeRevokedHandler(IpBillingService ip) : IDomainEventHandler<DischargeRevoked>
{
    public Task HandleAsync(DischargeRevoked domainEvent, CancellationToken cancellationToken) => ip.DischargeRevokedAsync(domainEvent, cancellationToken);
}

/// <summary>Records the discharge; closes a settled account or alerts billing about dues.</summary>
internal sealed class PatientDischargedHandler(IpBillingService ip) : IDomainEventHandler<PatientDischarged>
{
    public Task HandleAsync(PatientDischarged domainEvent, CancellationToken cancellationToken) => ip.DischargedAsync(domainEvent, cancellationToken);
}
