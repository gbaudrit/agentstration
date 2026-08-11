using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed class ToolResourceValidationException(string message) : Exception(message);
public sealed class ToolProviderDiscoveryFailedException(string message, Exception? innerException = null) : Exception(message, innerException);
public sealed record ToolDiscoveryDiff(int New, int Changed, int Unchanged, int Unavailable, int Total);

public sealed class ToolManagementService(
    IControlPlaneStore store,
    IEnumerable<IToolProviderDiscovery> discoveries,
    TimeProvider timeProvider)
{
    public static string ToolProviderId(string resourceGroup, string name) =>
        ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.Tools, "toolProviders", name).Value;

    public static string ToolId(string resourceGroup, string name) =>
        ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.Tools, "tools", name).Value;

    public Task<StoredResource<ToolResource>?> GetToolAsync(string resourceId, CancellationToken cancellationToken) => store.GetAsync<ToolResource>(resourceId, cancellationToken);
    public Task<StoredResource<ToolProviderResource>?> GetProviderAsync(string resourceId, CancellationToken cancellationToken) => store.GetAsync<ToolProviderResource>(resourceId, cancellationToken);
    public Task<StoredResource<McpServerResource>?> GetMcpServerAsync(string resourceId, CancellationToken cancellationToken) => store.GetAsync<McpServerResource>(resourceId, cancellationToken);
    public Task<IReadOnlyList<StoredResource<ToolResource>>> ListToolsAsync(string? resourceGroup, CancellationToken cancellationToken) => store.ListAsync<ToolResource>(AgentstrationResourceTypes.Tools, resourceGroup, 0, 1000, cancellationToken);
    public Task<IReadOnlyList<StoredResource<ToolProviderResource>>> ListProvidersAsync(string? resourceGroup, CancellationToken cancellationToken) => store.ListAsync<ToolProviderResource>(AgentstrationResourceTypes.ToolProviders, resourceGroup, 0, 1000, cancellationToken);
    public Task<IReadOnlyList<StoredResource<McpServerResource>>> ListMcpServersAsync(string? resourceGroup, CancellationToken cancellationToken) => store.ListAsync<McpServerResource>(AgentstrationResourceTypes.McpServers, resourceGroup, 0, 1000, cancellationToken);

    public async Task<StoredResource<ToolProviderResource>> PutProviderAsync(ToolProviderResource resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        ValidateProvider(resource);
        var existing = await store.GetAsync<ToolProviderResource>(resource.Id, cancellationToken);
        var desired = resource with
        {
            Generation = existing is null ? 1 : checked(existing.Value.Generation + 1),
            Properties = resource.Properties with { Discovery = existing?.Value.Properties.Discovery ?? resource.Properties.Discovery },
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        };
        return await store.PutAsync(desired, ifMatch, ifNoneMatch, cancellationToken);
    }

    public async Task<ToolProviderDiscoveryResult> TestConnectionAsync(ToolProviderResource provider, CancellationToken cancellationToken)
    {
        ValidateProvider(provider);
        try { return await DiscoveryFor(provider).DiscoverAsync(provider, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { throw new ToolProviderDiscoveryFailedException(exception.Message, exception); }
    }

    public async Task<ToolDiscoveryDiff> RefreshDiscoveryAsync(string providerResourceId, CancellationToken cancellationToken)
    {
        var storedProvider = await store.GetAsync<ToolProviderResource>(providerResourceId, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(providerResourceId);
        var provider = storedProvider.Value;
        ToolProviderDiscoveryResult result;
        var now = timeProvider.GetUtcNow();
        try
        {
            result = await DiscoveryFor(provider).DiscoverAsync(provider, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await store.PutAsync(provider with
            {
                Generation = checked(provider.Generation + 1),
                Properties = provider.Properties with
                {
                    Discovery = provider.Properties.Discovery with
                    {
                        LastDiscoveryAt = now,
                        Status = "error",
                        ErrorCode = "discovery_failed",
                        ErrorMessage = exception.Message
                    }
                }
            }, storedProvider.ETag, false, cancellationToken);
            throw new ToolProviderDiscoveryFailedException(exception.Message, exception);
        }

        var all = await ListToolsAsync(provider.ResourceGroup, cancellationToken);
        var existing = all.Where(value => string.Equals(value.Value.Properties.Provider?.ResourceId, provider.Id, StringComparison.Ordinal))
            .ToDictionary(value => value.Value.Properties.ExternalId!, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var newCount = 0;
        var changed = 0;
        var unchanged = 0;

        foreach (var descriptor in result.Tools.OrderBy(value => value.ExternalId, StringComparer.Ordinal))
        {
            if (!seen.Add(descriptor.ExternalId)) throw new ToolResourceValidationException($"Provider returned duplicate tool '{descriptor.ExternalId}'.");
            if (existing.TryGetValue(descriptor.ExternalId, out var current))
            {
                var properties = current.Value.Properties with
                {
                    DisplayName = descriptor.DisplayName,
                    Description = descriptor.Description,
                    Metadata = descriptor.Metadata,
                    Schema = new ToolSchema { Input = descriptor.InputSchema, Output = descriptor.OutputSchema },
                    Discovery = current.Value.Properties.Discovery! with { Available = true, LastSeenAt = now }
                };
                var metadataChanged = !DiscoveryMetadataEquals(current.Value.Properties, properties);
                if (metadataChanged || current.Value.Properties.Discovery?.Available != true)
                    await store.PutAsync(current.Value with { Generation = checked(current.Value.Generation + 1), Properties = properties }, current.ETag, false, cancellationToken);
                if (metadataChanged) changed++; else unchanged++;
            }
            else
            {
                var name = ToolResourceName(provider.Name, descriptor.ExternalId);
                await store.PutAsync(new ToolResource
                {
                    Id = ToolId(provider.ResourceGroup!, name),
                    Name = name,
                    Type = AgentstrationResourceTypes.Tools,
                    ApiVersion = ManagementApiVersions.V20260801,
                    ResourceGroup = provider.ResourceGroup,
                    WorkspaceId = provider.WorkspaceId,
                    Generation = 1,
                    Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
                    Properties = new ToolResourceProperties
                    {
                        DisplayName = descriptor.DisplayName,
                        Description = descriptor.Description,
                        Provider = new ResourceReference(provider.Id),
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
        foreach (var missing in existing.Values.Where(value => !seen.Contains(value.Value.Properties.ExternalId!)))
        {
            if (missing.Value.Properties.Discovery?.Available == true)
            {
                await store.PutAsync(missing.Value with
                {
                    Generation = checked(missing.Value.Generation + 1),
                    Properties = missing.Value.Properties with { Discovery = missing.Value.Properties.Discovery with { Available = false } }
                }, missing.ETag, false, cancellationToken);
                unavailable++;
            }
        }

        await store.PutAsync(provider with
        {
            Generation = checked(provider.Generation + 1),
            Properties = provider.Properties with
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

    public async Task<StoredResource<ToolResource>> SetToolEnabledAsync(string resourceId, bool enabled, string? ifMatch, CancellationToken cancellationToken)
    {
        var stored = await store.GetAsync<ToolResource>(resourceId, cancellationToken) ?? throw new ControlPlaneResourceNotFoundException(resourceId);
        return await store.PutAsync(stored.Value with
        {
            Generation = checked(stored.Value.Generation + 1),
            Properties = stored.Value.Properties with { Enabled = enabled }
        }, ifMatch ?? stored.ETag, false, cancellationToken);
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
        ValidateIdentity(resource, AgentstrationProviderNamespaces.Tools, "toolProviders", AgentstrationResourceTypes.ToolProviders);
        if (string.IsNullOrWhiteSpace(resource.Properties.DisplayName)) throw new ToolResourceValidationException("Tool provider displayName is required.");
        if (resource.Properties.ProviderType == ToolProviderType.Aep)
        {
            if (resource.Properties.Aep is null || resource.Properties.Mcp is not null || string.IsNullOrWhiteSpace(resource.Properties.Aep.ExtensionId))
                throw new ToolResourceValidationException("An AEP provider requires only a valid aep configuration.");
        }
        else
        {
            var mcp = resource.Properties.Mcp;
            if (mcp is null || resource.Properties.Aep is not null) throw new ToolResourceValidationException("An MCP provider requires only an mcp configuration.");
            if (mcp.Transport == McpToolProviderTransport.Stdio && string.IsNullOrWhiteSpace(mcp.Command)) throw new ToolResourceValidationException("STDIO MCP command is required.");
            if (mcp.Transport == McpToolProviderTransport.StreamableHttp && (mcp.Endpoint is null || !mcp.Endpoint.IsAbsoluteUri || mcp.Endpoint.Scheme is not ("http" or "https")))
                throw new ToolResourceValidationException("Streamable HTTP MCP endpoint must be an absolute HTTP(S) URI.");
            if (mcp.EnvironmentReferences.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
                throw new ToolResourceValidationException("Environment variable names and configuration references are required.");
        }
    }

    public static void ValidateTool(ToolResource resource)
    {
        ValidateIdentity(resource, AgentstrationProviderNamespaces.Tools, "tools", AgentstrationResourceTypes.Tools);
        if (string.IsNullOrWhiteSpace(resource.Properties.DisplayName)) throw new ToolResourceValidationException("Tool displayName is required.");
        if (resource.Properties.Provider is not null)
        {
            var provider = ResourceIdentifier.Parse(resource.Properties.Provider.ResourceId);
            if (provider.ProviderNamespace != AgentstrationProviderNamespaces.Tools || provider.ResourceType != "toolProviders" || string.IsNullOrWhiteSpace(resource.Properties.ExternalId) || resource.Properties.Discovery is null || resource.Properties.Schema is null)
                throw new ToolResourceValidationException("A discovered tool requires a ToolProvider reference, externalId, discovery state and schema.");
            return;
        }
        if ((resource.Properties.ToolType is null) == (resource.Properties.Mcp is null)) throw new ToolResourceValidationException("A legacy tool must define exactly one of toolType or mcp.");
        if (resource.Properties.ToolType is { } type && (string.IsNullOrWhiteSpace(type.Extension) || string.IsNullOrWhiteSpace(type.Id)))
            throw new ToolResourceValidationException("Tool type extension and id are required.");
        if (resource.Properties.Mcp is { } mcp)
        {
            if (string.IsNullOrWhiteSpace(mcp.Tool)) throw new ToolResourceValidationException("MCP tool name is required.");
            var server = ResourceIdentifier.Parse(mcp.Server.ResourceId);
            if (server.ProviderNamespace != AgentstrationProviderNamespaces.Integrations || server.ResourceType != "mcpServers")
                throw new ToolResourceValidationException("The MCP server reference must target an Agentstration.Integrations/mcpServers resource.");
        }
    }

    public static void ValidateMcpServer(McpServerResource resource)
    {
        ValidateIdentity(resource, AgentstrationProviderNamespaces.Integrations, "mcpServers", AgentstrationResourceTypes.McpServers);
        if (!resource.Properties.Endpoint.IsAbsoluteUri || resource.Properties.Endpoint.Scheme is not ("http" or "https")) throw new ToolResourceValidationException("MCP server endpoint must be an absolute HTTP(S) URI.");
    }

    private IToolProviderDiscovery DiscoveryFor(ToolProviderResource provider) => discoveries.FirstOrDefault(value => value.Supports(provider.Properties.ProviderType))
        ?? throw new ToolResourceValidationException($"No discovery adapter supports provider type '{provider.Properties.ProviderType}'.");

    private static bool DiscoveryMetadataEquals(ToolResourceProperties left, ToolResourceProperties right) =>
        left.DisplayName == right.DisplayName && left.Description == right.Description
        && Json(left.Schema?.Input) == Json(right.Schema?.Input) && Json(left.Schema?.Output) == Json(right.Schema?.Output)
        && JsonSerializer.Serialize(left.Metadata) == JsonSerializer.Serialize(right.Metadata);

    private static string Json(JsonElement? value) => value?.GetRawText() ?? string.Empty;

    private static string ToolResourceName(string providerName, string externalId)
    {
        var normalized = new string(externalId.Select(value => char.IsLetterOrDigit(value) || value is '.' or '-' or '_' ? value : '-').ToArray());
        if (normalized.Length == 0) normalized = "tool";
        if (!string.Equals(normalized, externalId, StringComparison.Ordinal))
            normalized += "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(externalId)))[..8].ToLowerInvariant();
        return $"{providerName}.{normalized}";
    }

    private static void ValidateIdentity(Resource resource, string providerNamespace, string resourceType, string fullType)
    {
        if (resource.Type != fullType) throw new ToolResourceValidationException($"Type must be '{fullType}'.");
        if (resource.ApiVersion != ManagementApiVersions.V20260801) throw new ToolResourceValidationException($"ApiVersion must be '{ManagementApiVersions.V20260801}'.");
        if (string.IsNullOrWhiteSpace(resource.ResourceGroup)) throw new ToolResourceValidationException("Resource group is required.");
        var expected = resource.WorkspaceId == Guid.Empty ? ResourceIdentifier.Create(resource.ResourceGroup, providerNamespace, resourceType, resource.Name).Value : ResourceIdentifier.Create(resource.WorkspaceId, resource.ResourceGroup, providerNamespace, resourceType, resource.Name).Value;
        if (resource.Id != expected) throw new ToolResourceValidationException("The resource identity is invalid.");
    }
}
