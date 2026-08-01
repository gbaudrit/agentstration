using Agentstration.Management.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure.Events;

public sealed class InProcessManagementEventPublisher(IServiceProvider serviceProvider) : IManagementEventPublisher
{
    public async Task PublishAsync<TEvent>(TEvent managementEvent, CancellationToken cancellationToken) where TEvent : IManagementEvent
    {
        foreach (var handler in serviceProvider.GetServices<IManagementEventHandler<TEvent>>())
        {
            await handler.HandleAsync(managementEvent, cancellationToken);
        }
    }
}
