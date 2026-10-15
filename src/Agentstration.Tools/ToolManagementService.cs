using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Tools;

public sealed class ToolResourceValidationException(string message) : Exception(message);
public sealed class ToolProviderDiscoveryFailedException(string message, Exception? innerException = null) : Exception(message, innerException);
public sealed record ToolDiscoveryDiff(int New, int Changed, int Unchanged, int Unavailable, int Total);

public sealed class ToolManagementService(
    IResourceStore store,
    IResourceReferenceResolver references,
    IResourceScopeOperations scopeOperations,
    IEnumerable<IToolProviderDiscovery> discoveries,
    TimeProvider timeProvider)
{
    public static string ToolProviderId(string name) => name;
    public static string ToolId(string name) => name;
    public Task<StoredResource<ToolResource>?> GetToolAsync(string name, CancellationToken cancellationToken) => GetToolAsync(name, default, cancellationToken);
    public Task<StoredResource<ToolResource>?> GetToolAsync(string name, ResourceNamespace @namespace, CancellationToken cancellationToken) => store.GetAsync<ToolResource>(new ResourceKey(ToolResourceKinds.Tool, name, @namespace), cancellationToken);
    public Task<StoredResource<ToolProviderResource>?> GetProviderAsync(string name, CancellationToken cancellationToken) => GetProviderAsync(name, default, cancellationToken);
    public Task<StoredResource<ToolProviderResource>?> GetProviderAsync(string name, ResourceNamespace @namespace, CancellationToken cancellationToken) => store.GetAsync<ToolProviderResource>(new ResourceKey(ToolResourceKinds.ToolProvider, name, @namespace), cancellationToken);
    public Task<StoredResource<ToolResource>?> GetToolExactAsync(ResourceScopeRef scopeRef, string name, ResourceNamespace @namespace, CancellationToken cancellationToken) =>
        store.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(scopeRef, @namespace, ToolResourceKinds.Tool, name), cancellationToken);
    public Task<StoredResource<ToolProviderResource>?> GetProviderExactAsync(ResourceScopeRef scopeRef, string name, ResourceNamespace @namespace, CancellationToken cancellationToken) =>
        store.GetExactAsync<ToolProviderResource>(ScopedResourceAddress.Create(scopeRef, @namespace, ToolResourceKinds.ToolProvider, name), cancellationToken);
    public Task<IReadOnlyList<StoredResource<ToolResource>>> ListToolsAsync(CancellationToken cancellationToken) => store.ListAllAsync<ToolResource>(ToolResourceKinds.Tool, cancellationToken);
    public Task<IReadOnlyList<StoredResource<ToolProviderResource>>> ListProvidersAsync(CancellationToken cancellationToken) => store.ListAllAsync<ToolProviderResource>(ToolResourceKinds.ToolProvider, cancellationToken);

    public async Task<StoredResource<ToolProviderResource>> PutProviderAsync(ToolProviderResource resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        if (string.Equals(resource.Name, AgentstrationToolProvider.Name, StringComparison.Ordinal))
            throw new ToolResourceValidationException("The Agentstration MCP provider is managed by ToolDefinitions.");
        ValidateProvider(resource);
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ToolResourceKinds.ToolProvider);
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        if (scopeRef.Kind != ResourceScopeKind.Workspace && resource.Definition.ProviderType != ToolProviderType.Aep)
            throw new ToolResourceValidationException("Only AEP ToolProviders may be owned by an Instance or Tenant scope.");
        return await scopeOperations.WriteAsync(resource, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var existing = await GetProviderExactAsync(scopeRef, resource.Name, resource.Namespace, token);
            var definition = await ValidateAndNormalizeProviderAsync(resource.Namespace, resource.Definition, scopeRef, token);
            return await store.PutExactAsync(scopeRef, resource with
            {
                ScopeRef = scopeRef,
                Uid = existing?.Value.Uid ?? Guid.Empty,
                Generation = existing is null ? 1 : checked(existing.Value.Generation + 1),
                Definition = definition with { Discovery = existing?.Value.Definition.Discovery ?? definition.Discovery },
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            }, ifMatch, ifNoneMatch, token);
        }, cancellationToken);
    }

    public async Task<ToolProviderDiscoveryResult> TestConnectionAsync(ToolProviderResource provider, CancellationToken cancellationToken)
    {
        ValidateProvider(provider);
        if (provider.Definition.Mcp?.Internal == true)
            throw new ToolResourceValidationException("The Agentstration MCP provider is managed by ToolDefinitions.");
        try { return await DiscoveryFor(provider).DiscoverAsync(provider, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { throw new ToolProviderDiscoveryFailedException(exception.Message, exception); }
    }

    public async Task<ToolDiscoveryDiff> RefreshDiscoveryAsync(string providerName, CancellationToken cancellationToken)
    {
        var storedProvider = await GetProviderAsync(providerName, cancellationToken)
            ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolProvider, providerName));
        return await RefreshDiscoveryAsync(storedProvider, cancellationToken);
    }

    public async Task<ToolDiscoveryDiff> RefreshDiscoveryExactAsync(
        ResourceScopeRef scopeRef,
        string providerName,
        ResourceNamespace @namespace,
        CancellationToken cancellationToken)
    {
        var storedProvider = await GetProviderExactAsync(scopeRef, providerName, @namespace, cancellationToken)
            ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolProvider, providerName, @namespace));
        return await RefreshDiscoveryAsync(storedProvider, cancellationToken);
    }

    private async Task<ToolDiscoveryDiff> RefreshDiscoveryAsync(
        StoredResource<ToolProviderResource> storedProvider,
        CancellationToken cancellationToken)
    {
        var provider = storedProvider.Value;
        var providerScope = provider.ScopeRef
            ?? throw new ToolResourceValidationException("The ToolProvider has no ownership scope.");
        if (provider.Definition.Mcp?.Internal == true)
            throw new ToolResourceValidationException("The Agentstration MCP provider is materialized by ToolDefinitions and cannot be refreshed.");
        return await scopeOperations.WriteAsync(provider, providerScope, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var now = timeProvider.GetUtcNow();
            ToolProviderDiscoveryResult result;
            try { result = await DiscoveryFor(provider).DiscoverAsync(provider, token); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var discovery = provider.Definition.Discovery with { LastDiscoveryAt = now, Status = "error", ErrorCode = "discovery_failed", ErrorMessage = exception.Message };
                await store.PutExactAsync(providerScope, provider with
                {
                    Generation = DiscoveryStateEquals(provider.Definition.Discovery, discovery) ? provider.Generation : checked(provider.Generation + 1),
                    Definition = provider.Definition with { Discovery = discovery }
                }, storedProvider.ETag, false, token);
                throw new ToolProviderDiscoveryFailedException(exception.Message, exception);
            }

            var existing = (await store.ListExactAsync<ToolResource>(providerScope, ToolResourceKinds.Tool, 0, int.MaxValue, token))
                .Where(value => value.Value.Namespace == provider.Namespace
                    && value.Value.Definition.Provider?.Name == provider.Name
                    && value.Value.Definition.Provider.ScopeRef == providerScope)
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
                        await store.PutExactAsync(providerScope, current.Value with { Generation = checked(current.Value.Generation + 1), Definition = definition }, current.ETag, false, token);
                    if (metadataChanged) changed++; else unchanged++;
                }
                else
                {
                    var name = ToolResourceName(provider.Name, descriptor.ExternalId);
                    await store.PutExactAsync(providerScope, new ToolResource
                    {
                        ApiVersion = ResourceApiVersions.CoreV1,
                        Kind = ToolResourceKinds.Tool,
                        Metadata = new ResourceMetadata { Name = name, Namespace = provider.Namespace },
                        ScopeRef = providerScope,
                        Generation = 1,
                        Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
                        Definition = new ToolResourceProperties
                        {
                            DisplayName = descriptor.DisplayName,
                            Description = descriptor.Description,
                            Provider = new ResourceReference(provider.Name, providerScope, provider.Namespace),
                            ExternalId = descriptor.ExternalId,
                            Enabled = false,
                            Discovery = new ToolDiscoveryState { Available = true, FirstSeenAt = now, LastSeenAt = now },
                            Schema = new ToolSchema { Input = descriptor.InputSchema, Output = descriptor.OutputSchema },
                            Metadata = descriptor.Metadata
                        }
                    }, null, true, token);
                    newCount++;
                }
            }

            var unavailable = 0;
            foreach (var missing in existing.Values.Where(value => !seen.Contains(value.Value.Definition.ExternalId!)))
            {
                if (missing.Value.Definition.Discovery?.Available != true) continue;
                await store.PutExactAsync(providerScope, missing.Value with
                {
                    Generation = checked(missing.Value.Generation + 1),
                    Definition = missing.Value.Definition with { Discovery = missing.Value.Definition.Discovery with { Available = false } }
                }, missing.ETag, false, token);
                unavailable++;
            }

            var providerDiscovery = new ToolProviderDiscoveryState
            {
                LastDiscoveryAt = now,
                Status = "connected",
                ToolCount = result.Tools.Count,
                Capabilities = result.Capabilities,
                ServerMetadata = result.ServerMetadata
            };
            await store.PutExactAsync(providerScope, provider with
            {
                Generation = DiscoveryStateEquals(provider.Definition.Discovery, providerDiscovery) ? provider.Generation : checked(provider.Generation + 1),
                Definition = provider.Definition with { Discovery = providerDiscovery }
            }, storedProvider.ETag, false, token);
            return new ToolDiscoveryDiff(newCount, changed, unchanged, unavailable, result.Tools.Count);
        }, cancellationToken);
    }

    private static bool DiscoveryStateEquals(ToolProviderDiscoveryState left, ToolProviderDiscoveryState right) =>
        string.Equals(left.Status, right.Status, StringComparison.Ordinal)
        && string.Equals(left.ErrorCode, right.ErrorCode, StringComparison.Ordinal)
        && string.Equals(left.ErrorMessage, right.ErrorMessage, StringComparison.Ordinal)
        && left.ToolCount == right.ToolCount
        && DictionaryEquals(left.Capabilities, right.Capabilities)
        && DictionaryEquals(left.ServerMetadata, right.ServerMetadata);

    private static bool DictionaryEquals<T>(IReadOnlyDictionary<string, T> left, IReadOnlyDictionary<string, T> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value) && EqualityComparer<T>.Default.Equals(pair.Value, value));

    public async Task<StoredResource<ToolResource>> SetToolEnabledAsync(string name, bool enabled, string? ifMatch, CancellationToken cancellationToken)
        => await SetToolEnabledAsync(name, default, enabled, ifMatch, cancellationToken);

    public async Task<StoredResource<ToolResource>> SetToolEnabledAsync(string name, ResourceNamespace @namespace, bool enabled, string? ifMatch, CancellationToken cancellationToken)
    {
        var stored = await GetToolAsync(name, @namespace, cancellationToken) ?? throw new ResourceNotFoundException(new(ToolResourceKinds.Tool, name, @namespace));
        var scopeRef = stored.Value.ScopeRef
            ?? throw new ToolResourceValidationException("The Tool has no ownership scope.");
        return await scopeOperations.WriteAsync(stored.Value, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
            await store.PutExactAsync(scopeRef, stored.Value with { Generation = checked(stored.Value.Generation + 1), Definition = stored.Value.Definition with { Enabled = enabled } }, ifMatch ?? stored.ETag, false, token), cancellationToken);
    }

    public async Task<StoredResource<ToolResource>> PutToolAsync(ToolResource resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        ValidateTool(resource);
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ToolResourceKinds.Tool);
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        if (scopeRef.Kind != ResourceScopeKind.Workspace)
            throw new ToolResourceValidationException("Instance- and Tenant-owned Tools must be materialized by AEP discovery.");
        return await scopeOperations.WriteAsync(resource, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
            await store.PutExactAsync(scopeRef, resource with { ScopeRef = scopeRef, Generation = Math.Max(1, resource.Generation), Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded } }, ifMatch, ifNoneMatch, token), cancellationToken);
    }

    public static void ValidateProvider(ToolProviderResource resource)
    {
        ValidateIdentity(resource, ToolResourceKinds.ToolProvider);
        if (string.IsNullOrWhiteSpace(resource.Definition.DisplayName)) throw new ToolResourceValidationException("Tool provider displayName is required.");
        if (resource.Definition.ProviderType == ToolProviderType.Aep)
        {
            if (resource.Definition.Aep is null || resource.Definition.Mcp is not null || string.IsNullOrWhiteSpace(resource.Definition.Aep.Extension.Name))
                throw new ToolResourceValidationException("An AEP provider requires only a valid aep configuration.");
        }
        else
        {
            var mcp = resource.Definition.Mcp;
            if (mcp is null || resource.Definition.Aep is not null) throw new ToolResourceValidationException("An MCP provider requires only an mcp configuration.");
            if (mcp.Internal)
            {
                if (mcp.Endpoint is not null || !string.IsNullOrWhiteSpace(mcp.Command) || mcp.Arguments.Count > 0 || mcp.EnvironmentReferences.Count > 0)
                    throw new ToolResourceValidationException("An internal MCP provider cannot configure an external transport.");
                return;
            }
            if (mcp.Transport == McpToolProviderTransport.Stdio && string.IsNullOrWhiteSpace(mcp.Command)) throw new ToolResourceValidationException("STDIO MCP command is required.");
            if (mcp.Transport == McpToolProviderTransport.StreamableHttp && (mcp.Endpoint is null || !mcp.Endpoint.IsAbsoluteUri || mcp.Endpoint.Scheme is not ("http" or "https")))
                throw new ToolResourceValidationException("Streamable HTTP MCP endpoint must be an absolute HTTP(S) URI.");
        }
    }

    public static void ValidateTool(ToolResource resource)
    {
        ValidateIdentity(resource, ToolResourceKinds.Tool);
        if (string.IsNullOrWhiteSpace(resource.Definition.DisplayName)) throw new ToolResourceValidationException("Tool displayName is required.");
        if (resource.Definition.Provider is not null)
        {
            if (string.IsNullOrWhiteSpace(resource.Definition.Provider.Name) || string.IsNullOrWhiteSpace(resource.Definition.ExternalId) || resource.Definition.Discovery is null || resource.Definition.Schema is null)
                throw new ToolResourceValidationException("A discovered tool requires a ToolProvider reference, externalId, discovery state and schema.");
            return;
        }
        if (resource.Definition.ToolType is null) throw new ToolResourceValidationException("A manually configured tool requires a toolType source.");
    }

    private async Task<ToolProviderProperties> ValidateAndNormalizeProviderAsync(
        ResourceNamespace ownerNamespace,
        ToolProviderProperties definition,
        ResourceScopeRef ownerScopeRef,
        CancellationToken cancellationToken)
    {
        if (definition.ProviderType != ToolProviderType.Aep) return definition;
        var reference = definition.Aep!.Extension;
        StoredResource<ExtensionRegistrationResource>? extension;
        try
        {
            extension = await references.ResolveAsync<ExtensionRegistrationResource>(
                reference,
                ownerNamespace,
                ExtensionKinds.ExtensionRegistration,
                ownerScopeRef,
                cancellationToken);
        }
        catch (ResourceReferenceAmbiguousException exception)
        {
            throw new ToolResourceValidationException(exception.Message);
        }
        catch (ResourceReferenceOutsideScopeException exception)
        {
            throw new ToolResourceValidationException(exception.Message);
        }
        if (extension is null)
            throw new ToolResourceValidationException($"Referenced Extension Registration '{reference.Name}' does not exist or is not visible from '{ownerScopeRef}'.");
        var extensionScopeRef = extension.Value.ScopeRef
            ?? throw new ToolResourceValidationException("The referenced Extension Registration has no ownership scope.");
        if (extensionScopeRef != ownerScopeRef)
            throw new ToolResourceValidationException("An AEP ToolProvider and its Extension Registration must belong to the same scope.");
        if (!extension.Value.Definition.Enabled)
            throw new ToolResourceValidationException($"Referenced Extension Registration '{extension.Value.Address}' is disabled.");
        if (string.IsNullOrWhiteSpace(extension.Value.Definition.ExpectedExtensionId))
            throw new ToolResourceValidationException($"Referenced Extension Registration '{extension.Value.Address}' must declare its expected AEP identity.");
        return definition with
        {
            Aep = definition.Aep with
            {
                Extension = reference with
                {
                    ScopeRef = extensionScopeRef,
                    Namespace = extension.Value.Namespace
                }
            }
        };
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
        if (resource.ApiVersion != ResourceApiVersions.CoreV1) throw new ToolResourceValidationException($"ApiVersion must be '{ResourceApiVersions.CoreV1}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name);
    }
}
