using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed class ToolResourceValidationException(string message) : Exception(message);
public sealed class ToolProviderDiscoveryFailedException(string message, Exception? innerException = null) : Exception(message, innerException);
public sealed record ToolDiscoveryDiff(int New, int Changed, int Unchanged, int Unavailable, int Total);

public sealed class ToolManagementService(IControlPlaneStore store, IEnumerable<IToolProviderDiscovery> discoveries, TimeProvider timeProvider)
{
    public static string ToolProviderId(string name) => name;
    public static string ToolId(string name) => name;
    public Task<StoredResource<ToolResource>?> GetToolAsync(string name, CancellationToken cancellationToken) => store.GetAsync<ToolResource>(name, cancellationToken);
    public Task<StoredResource<ToolProviderResource>?> GetProviderAsync(string name, CancellationToken cancellationToken) => store.GetAsync<ToolProviderResource>(name, cancellationToken);
    public Task<StoredResource<McpServerResource>?> GetMcpServerAsync(string name, CancellationToken cancellationToken) => store.GetAsync<McpServerResource>(name, cancellationToken);
    public Task<IReadOnlyList<StoredResource<ToolResource>>> ListToolsAsync(CancellationToken cancellationToken) => store.ListAsync<ToolResource>(ResourceKinds.Tool, 0, 1000, cancellationToken);
    public Task<IReadOnlyList<StoredResource<ToolProviderResource>>> ListProvidersAsync(CancellationToken cancellationToken) => store.ListAsync<ToolProviderResource>(ResourceKinds.ToolProvider, 0, 1000, cancellationToken);
    public Task<IReadOnlyList<StoredResource<McpServerResource>>> ListMcpServersAsync(CancellationToken cancellationToken) => store.ListAsync<McpServerResource>(ResourceKinds.McpServer, 0, 1000, cancellationToken);

    public async Task<StoredResource<ToolProviderResource>> PutProviderAsync(ToolProviderResource resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        ValidateProvider(resource);
        var existing = await GetProviderAsync(resource.Metadata.Name, cancellationToken);
        return await store.PutAsync(resource with
        {
            Uid = existing?.Value.Uid ?? Guid.Empty,
            Generation = existing is null ? 1 : checked(existing.Value.Generation + 1),
            Definition = resource.Definition with { Discovery = existing?.Value.Definition.Discovery ?? resource.Definition.Discovery },
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, ifMatch, ifNoneMatch, cancellationToken);
    }

    public async Task<ToolProviderDiscoveryResult> TestConnectionAsync(ToolProviderResource provider, CancellationToken cancellationToken)
    {
        ValidateProvider(provider);
        try { return await DiscoveryFor(provider).DiscoverAsync(provider, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { throw new ToolProviderDiscoveryFailedException(exception.Message, exception); }
    }

    public async Task<ToolDiscoveryDiff> RefreshDiscoveryAsync(string providerName, CancellationToken cancellationToken)
    {
        var storedProvider = await GetProviderAsync(providerName, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ToolProvider, providerName));
        var provider = storedProvider.Value;
        var now = timeProvider.GetUtcNow();
        ToolProviderDiscoveryResult result;
        try { result = await DiscoveryFor(provider).DiscoverAsync(provider, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await store.PutAsync(provider with
            {
                Generation = checked(provider.Generation + 1),
                Definition = provider.Definition with
                {
                    Discovery = provider.Definition.Discovery with { LastDiscoveryAt = now, Status = "error", ErrorCode = "discovery_failed", ErrorMessage = exception.Message }
                }
            }, storedProvider.ETag, false, cancellationToken);
            throw new ToolProviderDiscoveryFailedException(exception.Message, exception);
        }

        var existing = (await ListToolsAsync(cancellationToken))
            .Where(value => value.Value.Definition.Provider?.Name == providerName)
            .ToDictionary(value => value.Value.Definition.ExternalId!, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var newCount = 0;
        var changed = 0;
        var unchanged = 0;
        foreach (var descriptor in result.Tools.OrderBy(value => value.ExternalId, StringComparer.Ordinal))
        {
            if (!seen.Add(descriptor.ExternalId)) throw new ToolResourceValidationException($"Provider returned duplicate tool '{descriptor.ExternalId}'.");
            if (existing.TryGetValue(descriptor.ExternalId, out var current))
            {
                var definition = current.Value.Definition with
                {
                    DisplayName = descriptor.DisplayName,
                    Description = descriptor.Description,
                    Metadata = descriptor.Metadata,
                    Schema = new ToolSchema { Input = descriptor.InputSchema, Output = descriptor.OutputSchema },
                    Discovery = current.Value.Definition.Discovery! with { Available = true, LastSeenAt = now }
                };
                var metadataChanged = !DiscoveryMetadataEquals(current.Value.Definition, definition);
                if (metadataChanged || current.Value.Definition.Discovery?.Available != true)
                    await store.PutAsync(current.Value with { Generation = checked(current.Value.Generation + 1), Definition = definition }, current.ETag, false, cancellationToken);
                if (metadataChanged) changed++; else unchanged++;
            }
            else
            {
                var name = ToolResourceName(providerName, descriptor.ExternalId);
                await store.PutAsync(new ToolResource
                {
                    ApiVersion = ManagementApiVersions.CoreV1,
                    Kind = ResourceKinds.Tool,
                    Metadata = new ResourceMetadata { Name = name },
                    WorkspaceId = provider.WorkspaceId,
                    Generation = 1,
                    Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
                    Definition = new ToolResourceProperties
                    {
                        DisplayName = descriptor.DisplayName,
                        Description = descriptor.Description,
                        Provider = new ResourceReference(providerName),
                        ExternalId = descriptor.ExternalId,
                        Enabled = false,
                        Discovery = new ToolDiscoveryState { Available = true, FirstSeenAt = now, LastSeenAt = now },
                        Schema = new ToolSchema { Input = descriptor.InputSchema, Output = descriptor.OutputSchema },
                        Metadata = descriptor.Metadata
                    }
                }, null, true, cancellationToken);
                newCount++;
            }
        }

        var unavailable = 0;
        foreach (var missing in existing.Values.Where(value => !seen.Contains(value.Value.Definition.ExternalId!)))
        {
            if (missing.Value.Definition.Discovery?.Available != true) continue;
            await store.PutAsync(missing.Value with
            {
                Generation = checked(missing.Value.Generation + 1),
                Definition = missing.Value.Definition with { Discovery = missing.Value.Definition.Discovery with { Available = false } }
            }, missing.ETag, false, cancellationToken);
            unavailable++;
        }

        await store.PutAsync(provider with
        {
            Generation = checked(provider.Generation + 1),
            Definition = provider.Definition with
            {
                Discovery = new ToolProviderDiscoveryState
                {
                    LastDiscoveryAt = now,
                    Status = "connected",
                    ToolCount = result.Tools.Count,
                    Capabilities = result.Capabilities,
                    ServerMetadata = result.ServerMetadata
                }
            }
        }, storedProvider.ETag, false, cancellationToken);
        return new ToolDiscoveryDiff(newCount, changed, unchanged, unavailable, result.Tools.Count);
    }

    public async Task<StoredResource<ToolResource>> SetToolEnabledAsync(string name, bool enabled, string? ifMatch, CancellationToken cancellationToken)
    {
        var stored = await GetToolAsync(name, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Tool, name));
        return await store.PutAsync(stored.Value with { Generation = checked(stored.Value.Generation + 1), Definition = stored.Value.Definition with { Enabled = enabled } }, ifMatch ?? stored.ETag, false, cancellationToken);
    }

    public async Task<StoredResource<ToolResource>> PutToolAsync(ToolResource resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        ValidateTool(resource);
        return await store.PutAsync(resource with { Generation = Math.Max(1, resource.Generation), Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded } }, ifMatch, ifNoneMatch, cancellationToken);
    }

    public async Task<StoredResource<McpServerResource>> PutMcpServerAsync(McpServerResource resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        ValidateMcpServer(resource);
        return await store.PutAsync(resource with { Generation = Math.Max(1, resource.Generation), Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded } }, ifMatch, ifNoneMatch, cancellationToken);
    }

    public static void ValidateProvider(ToolProviderResource resource)
    {
        ValidateIdentity(resource, ResourceKinds.ToolProvider);
        if (string.IsNullOrWhiteSpace(resource.Definition.DisplayName)) throw new ToolResourceValidationException("Tool provider displayName is required.");
        if (resource.Definition.ProviderType == ToolProviderType.Aep)
        {
            if (resource.Definition.Aep is null || resource.Definition.Mcp is not null || string.IsNullOrWhiteSpace(resource.Definition.Aep.ExtensionId))
                throw new ToolResourceValidationException("An AEP provider requires only a valid aep configuration.");
        }
        else
        {
            var mcp = resource.Definition.Mcp;
            if (mcp is null || resource.Definition.Aep is not null) throw new ToolResourceValidationException("An MCP provider requires only an mcp configuration.");
            if (mcp.Transport == McpToolProviderTransport.Stdio && string.IsNullOrWhiteSpace(mcp.Command)) throw new ToolResourceValidationException("STDIO MCP command is required.");
            if (mcp.Transport == McpToolProviderTransport.StreamableHttp && (mcp.Endpoint is null || !mcp.Endpoint.IsAbsoluteUri || mcp.Endpoint.Scheme is not ("http" or "https")))
                throw new ToolResourceValidationException("Streamable HTTP MCP endpoint must be an absolute HTTP(S) URI.");
        }
    }

    public static void ValidateTool(ToolResource resource)
    {
        ValidateIdentity(resource, ResourceKinds.Tool);
        if (string.IsNullOrWhiteSpace(resource.Definition.DisplayName)) throw new ToolResourceValidationException("Tool displayName is required.");
        if (resource.Definition.Provider is not null)
        {
            if (string.IsNullOrWhiteSpace(resource.Definition.Provider.Name) || string.IsNullOrWhiteSpace(resource.Definition.ExternalId) || resource.Definition.Discovery is null || resource.Definition.Schema is null)
                throw new ToolResourceValidationException("A discovered tool requires a ToolProvider reference, externalId, discovery state and schema.");
            return;
        }
        if ((resource.Definition.ToolType is null) == (resource.Definition.Mcp is null)) throw new ToolResourceValidationException("A tool must define exactly one source.");
        if (resource.Definition.Mcp is { } mcp && (string.IsNullOrWhiteSpace(mcp.Tool) || string.IsNullOrWhiteSpace(mcp.Server.Name)))
            throw new ToolResourceValidationException("MCP server and tool names are required.");
    }

    public static void ValidateMcpServer(McpServerResource resource)
    {
        ValidateIdentity(resource, ResourceKinds.McpServer);
        if (!resource.Definition.Endpoint.IsAbsoluteUri || resource.Definition.Endpoint.Scheme is not ("http" or "https")) throw new ToolResourceValidationException("MCP server endpoint must be an absolute HTTP(S) URI.");
    }

    private IToolProviderDiscovery DiscoveryFor(ToolProviderResource provider) => discoveries.FirstOrDefault(value => value.Supports(provider.Definition.ProviderType))
        ?? throw new ToolResourceValidationException($"No discovery adapter supports provider type '{provider.Definition.ProviderType}'.");

    private static bool DiscoveryMetadataEquals(ToolResourceProperties left, ToolResourceProperties right) =>
        left.DisplayName == right.DisplayName && left.Description == right.Description
        && Json(left.Schema?.Input) == Json(right.Schema?.Input) && Json(left.Schema?.Output) == Json(right.Schema?.Output)
        && JsonSerializer.Serialize(left.Metadata) == JsonSerializer.Serialize(right.Metadata);

    private static string Json(JsonElement? value) => value?.GetRawText() ?? string.Empty;

    private static string ToolResourceName(string providerName, string externalId)
    {
        var normalized = new string(externalId.Select(value => char.IsLetterOrDigit(value) || value is '.' or '-' or '_' ? value : '-').ToArray());
        if (normalized.Length == 0) normalized = "tool";
        if (normalized != externalId) normalized += "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(externalId)))[..8].ToLowerInvariant();
        return $"{providerName}.{normalized}";
    }

    private static void ValidateIdentity(Resource resource, string kind)
    {
        if (resource.Kind != kind) throw new ToolResourceValidationException($"Kind must be '{kind}'.");
        if (resource.ApiVersion != ManagementApiVersions.CoreV1) throw new ToolResourceValidationException($"ApiVersion must be '{ManagementApiVersions.CoreV1}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name);
    }
}
