using Agentstration.Application;
using Agentstration.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure.Events;

public sealed class InProcessEventBus(IServiceProvider serviceProvider) : IEventBus
{
    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken) where TEvent : IDomainEvent
    {
        foreach (var handler in serviceProvider.GetServices<IEventHandler<TEvent>>())
        {
            await handler.HandleAsync(domainEvent, cancellationToken);
        }
    }
}
