using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Management.Abstractions;
using Agentstration.Memory;
using Agentstration.Memory.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.Tools.Mcp;

namespace Agentstration.Infrastructure.Memory;

public sealed class ManagedMemoryRecordStoreResolver(
    IControlPlaneStore controlPlane,
    IMemoryRecordStore builtin,
    IAepExtensionEndpointResolver extensionEndpoints,
    IHttpClientFactory httpClients) : IMemoryRecordStoreResolver
{
    public async ValueTask<IMemoryRecordStore> ResolveAsync(WorkspaceId workspaceId, MemoryProviderReference provider, CancellationToken cancellationToken)
    {
        // The reserved fallback initializes the local store before a request-scoped
        // Workspace exists. Governed runtime calls use their explicit profile binding.
        if (provider == MemoryProviderReference.Local) return builtin;
        var @namespace = ResourceNamespace.Parse(provider.Namespace);
        var resource = await controlPlane.GetAsync<MemoryProviderResource>(new(ResourceKinds.MemoryProvider, provider.Name, @namespace), cancellationToken);
        if (resource is null)
        {
            throw new InvalidOperationException($"Memory provider '{@namespace}/{provider.Name}' was not found.");
        }
        if (resource.Value.Definition.IntegrationKind == MemoryProviderIntegrationKind.Builtin) return builtin;
        var configuration = resource.Value.Definition.Aep
            ?? throw new InvalidOperationException($"Memory provider '{resource.Value.Address}' has no AEP binding.");
        var endpoint = extensionEndpoints.Resolve(configuration.ExtensionId);
        var http = httpClients.CreateClient("agentstration-aep-memory");
        http.BaseAddress = endpoint;
        var client = new AepClient(http);
        var manifest = await client.DiscoverAsync(cancellationToken);
        if (!string.Equals(manifest.Extension.Id, configuration.ExtensionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected AEP extension '{configuration.ExtensionId}' but discovered '{manifest.Extension.Id}'.");
        if (!(manifest.Contributions.MemoryProviders ?? []).Any(value => string.Equals(value.Id, configuration.ProviderId, StringComparison.Ordinal)))
            throw new InvalidOperationException($"AEP extension '{configuration.ExtensionId}' does not provide Memory provider '{configuration.ProviderId}'.");
        return new AepMemoryRecordStore(client.CreateMemoryProvider(configuration.ProviderId));
    }
}

internal sealed class AepMemoryRecordStore(AepMemoryProviderClient client) : IMemoryRecordStore
{
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task AddAsync(MemoryRecord record, CancellationToken cancellationToken) => client.WriteAsync(ToAep(record), cancellationToken);

    public async Task<MemoryRecord?> GetAsync(WorkspaceId workspaceId, MemoryRecordId id, CancellationToken cancellationToken) =>
        FromAep(await client.GetAsync(new(workspaceId.Value, id.Value), cancellationToken));

    public async Task<IReadOnlyList<MemoryRecord>> ListAsync(WorkspaceId workspaceId, MemoryScope? scope, DateTimeOffset now, int skip, int take, CancellationToken cancellationToken) =>
        (await client.ListAsync(new(workspaceId.Value, scope is null ? null : ToAep(scope), now, skip, take), cancellationToken)).Select(value => FromAep(value)!).ToArray();

    public Task<bool> DeleteAsync(WorkspaceId workspaceId, MemoryRecordId id, CancellationToken cancellationToken) =>
        client.DeleteAsync(new(workspaceId.Value, id.Value), cancellationToken);

    public Task<int> ClearScopeAsync(WorkspaceId workspaceId, MemoryScope scope, CancellationToken cancellationToken) =>
        client.ClearScopeAsync(new(workspaceId.Value, ToAep(scope)), cancellationToken);

    public Task<int> PurgeExpiredAsync(WorkspaceId workspaceId, DateTimeOffset now, int take, CancellationToken cancellationToken) =>
        client.PurgeExpiredAsync(new(workspaceId.Value, now, take), cancellationToken);

    private static AepMemoryScope ToAep(MemoryScope value) => new(value.Kind.ToString(), value.Key);
    private static AepMemoryRecord ToAep(MemoryRecord value) => new(
        value.Id.Value, value.WorkspaceId.Value, ToAep(value.Scope), value.Content, value.Tags,
        new(value.Provenance.SourceKind.ToString(), value.Provenance.SourceId, value.Provenance.Reason, value.Provenance.CreatedByPrincipalId),
        value.CreatedAt, value.ExpiresAt);
    private static MemoryRecord? FromAep(AepMemoryRecord? value) => value is null ? null : new(
        new(value.Id), new(value.WorkspaceId), new(Enum.Parse<MemoryScopeKind>(value.Scope.Kind), value.Scope.Key), value.Content, value.Tags,
        new(Enum.Parse<MemorySourceKind>(value.Provenance.SourceKind), value.Provenance.SourceId, value.Provenance.Reason, value.Provenance.CreatedByPrincipalId),
        value.CreatedAt, value.ExpiresAt);
}
