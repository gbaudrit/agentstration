using Agentstration.Flow;

namespace Agentstration.Flow.Storage.Abstractions;

public sealed record StoredFlow(FlowDefinition Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record StoredFlowVersion(FlowVersion Value, string ETag, DateTimeOffset UpdatedAt);
public sealed record FlowPage(IReadOnlyList<StoredFlow> Items, bool HasMore);

public interface IFlowRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredFlow> CreateAsync(FlowDefinition definition, CancellationToken cancellationToken);
    Task<StoredFlow?> GetAsync(FlowId id, CancellationToken cancellationToken);
    Task<FlowPage> ListAsync(int skip, int take, CancellationToken cancellationToken);
    Task<StoredFlow> UpdateAsync(FlowDefinition definition, string expectedETag, CancellationToken cancellationToken);
    Task DeleteAsync(FlowId id, string? expectedETag, CancellationToken cancellationToken);
    Task<StoredFlowVersion> CreateVersionAsync(FlowVersion version, CancellationToken cancellationToken);
    Task<StoredFlowVersion?> GetVersionAsync(FlowId id, string version, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredFlowVersion>> ListVersionsAsync(FlowId id, CancellationToken cancellationToken);
}

public sealed class FlowConcurrencyException(string message) : Exception(message);
public sealed class FlowNotFoundException(FlowId id) : KeyNotFoundException($"Flow '{id}' was not found.");
