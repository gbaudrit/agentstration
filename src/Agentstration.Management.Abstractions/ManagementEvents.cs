namespace Agentstration.Management.Abstractions;

public interface IManagementEvent
{
    DateTimeOffset OccurredAt { get; }
}

public sealed record AgentCreated(string ResourceId, long Generation, DateTimeOffset OccurredAt) : IManagementEvent;
public sealed record AgentUpdated(string ResourceId, long Generation, DateTimeOffset OccurredAt) : IManagementEvent;
public sealed record AgentDeleted(string ResourceId, DateTimeOffset OccurredAt) : IManagementEvent;

public interface IManagementEventPublisher
{
    Task PublishAsync<TEvent>(TEvent managementEvent, CancellationToken cancellationToken) where TEvent : IManagementEvent;
}

public interface IManagementEventHandler<in TEvent> where TEvent : IManagementEvent
{
    Task HandleAsync(TEvent managementEvent, CancellationToken cancellationToken);
}
