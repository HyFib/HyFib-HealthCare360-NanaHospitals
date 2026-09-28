using System.Collections.Concurrent;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Messaging;

/// <summary>
/// Handles a domain event inside the same unit of work that raised it (before commit). Handlers may add
/// or change entities; everything commits or rolls back together.
/// </summary>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}

public sealed class DomainEventDispatcher(IServiceProvider serviceProvider)
{
    private static readonly ConcurrentDictionary<Type, IInvoker> Invokers = new();

    public Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var invoker = Invokers.GetOrAdd(domainEvent.GetType(),
            static type => (IInvoker)Activator.CreateInstance(typeof(Invoker<>).MakeGenericType(type))!);
        return invoker.InvokeAsync(serviceProvider, domainEvent, cancellationToken);
    }

    private interface IInvoker
    {
        Task InvokeAsync(IServiceProvider provider, IDomainEvent domainEvent, CancellationToken cancellationToken);
    }

    private sealed class Invoker<TEvent> : IInvoker
        where TEvent : IDomainEvent
    {
        public async Task InvokeAsync(IServiceProvider provider, IDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            foreach (var handler in provider.GetServices<IDomainEventHandler<TEvent>>())
            {
                await handler.HandleAsync((TEvent)domainEvent, cancellationToken);
            }
        }
    }
}
