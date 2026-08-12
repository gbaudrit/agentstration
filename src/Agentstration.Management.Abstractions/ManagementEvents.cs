namespace Agentstration.Management.Abstractions;

public interface IManagementEvent
{
    DateTimeOffset OccurredAt { get; }
}

public sealed record AgentCreated(Guid Uid, string Name, long Generation, DateTimeOffset OccurredAt) : IManagementEvent;
public sealed record AgentUpdated(Guid Uid, string Name, long Generation, DateTimeOffset OccurredAt) : IManagementEvent;
public sealed record AgentDeleted(Guid Uid, string Name, DateTimeOffset OccurredAt) : IManagementEvent;

public interface IManagementEventPublisher
{
    Task PublishAsync<TEvent>(TEvent managementEvent, CancellationToken cancellationToken) where TEvent : IManagementEvent;
}

public interface IManagementEventHandler<in TEvent> where TEvent : IManagementEvent
{
    Task HandleAsync(TEvent managementEvent, CancellationToken cancellationToken);
}
