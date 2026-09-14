using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Tools;

public sealed class ToolDefinitionValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ToolDefinitionService(
    IResourceStore store,
    IToolDefinitionFlowResolver flows,
    TimeProvider timeProvider,
    IEnumerable<IInternalMcpToolDefinitionProvider>? internalTools = null)
{
    public Task<IReadOnlyList<StoredResource<ToolDefinitionResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<ToolDefinitionResource>(ToolResourceKinds.ToolDefinition, cancellationToken);

    public Task<StoredResource<ToolDefinitionResource>?> GetAsync(
        string name,
        ResourceNamespace @namespace,
        CancellationToken cancellationToken) =>
        store.GetAsync<ToolDefinitionResource>(new ResourceKey(ToolResourceKinds.ToolDefinition, name, @namespace), cancellationToken);

    public async Task<StoredResource<ToolDefinitionResource>> PutAsync(
        ToolDefinitionResource resource,
        string? ifMatch,
        bool ifNoneMatch,
        CancellationToken cancellationToken)
    {
        Validate(resource);
        if ((internalTools ?? []).Any(value => string.Equals(value.Definition.Name, resource.Name, StringComparison.Ordinal)))
            throw new ToolDefinitionValidationException("tool_definition_name_reserved", $"ToolDefinition name '{resource.Name}' is reserved by an internal Agentstration MCP Tool.");
        var scopeRef = resource.ScopeRef!.Value;
        var resolved = await flows.ResolveAsync(scopeRef, resource.Namespace, resource.Definition.Flow, cancellationToken);
        ValidateContract(resource.Definition, resolved);
        var existing = await GetAsync(resource.Name, resource.Namespace, cancellationToken);
        var value = resource with
        {
            Uid = existing?.Value.Uid ?? Guid.Empty,
            Generation = existing is null ? 1 : checked(existing.Value.Generation + 1),
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        };

        await EnsureProviderAsync(scopeRef, resource.Namespace, cancellationToken);
        var stored = await store.PutAsync(value, ifMatch, ifNoneMatch, cancellationToken);
        await MaterializeAsync(stored.Value, cancellationToken);
        return stored;
    }

    public async Task<StoredResource<ToolDefinitionResource>> SetEnabledAsync(
        string name,
        ResourceNamespace @namespace,
        bool enabled,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var current = await GetAsync(name, @namespace, cancellationToken)
            ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolDefinition, name, @namespace));
        return await PutAsync(
            current.Value with { Definition = current.Value.Definition with { Enabled = enabled } },
            ifMatch ?? current.ETag,
            false,
            cancellationToken);
    }

    public async Task DeleteAsync(string name, ResourceNamespace @namespace, string? ifMatch, CancellationToken cancellationToken)
    {
        var current = await GetAsync(name, @namespace, cancellationToken)
            ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolDefinition, name, @namespace));
        var tool = new ResourceKey(ToolResourceKinds.Tool, AgentstrationToolProvider.ToolResourceName(name), @namespace);
        if (await store.GetAsync<ToolResource>(tool, cancellationToken) is { } materialized)
            await store.DeleteAsync(tool, materialized.ETag, cancellationToken);
        await store.DeleteAsync(new ResourceKey(ToolResourceKinds.ToolDefinition, name, @namespace), ifMatch ?? current.ETag, cancellationToken);
    }

    public static void ValidateContract(ToolDefinitionProperties definition, ResolvedToolDefinitionFlowContract flow)
    {
        if (!SameSchema(definition.InputSchema, flow.InputSchema))
            throw new ToolDefinitionValidationException("tool_definition_input_schema_incompatible", "The Tool input schema must match the published Flow input schema.");
        if (!SameSchema(definition.OutputSchema, flow.OutputSchema))
            throw new ToolDefinitionValidationException("tool_definition_output_schema_incompatible", "The Tool output schema must match the published Flow output schema.");
    }

    public static bool SameSchema(JsonElement? left, JsonElement? right)
    {
        if (left is null || left.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return right is null || right.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
        if (right is null || right.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        return JsonNode.DeepEquals(JsonNode.Parse(left.Value.GetRawText()), JsonNode.Parse(right.Value.GetRawText()));
    }

    private static void Validate(ToolDefinitionResource resource)
    {
        if (resource.Kind != ToolResourceKinds.ToolDefinition || resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw new ToolDefinitionValidationException("tool_definition_identity_invalid", "ToolDefinition kind and apiVersion are required.");
        if (resource.ScopeRef is not { Kind: ResourceScopeKind.Workspace })
            throw new ToolDefinitionValidationException("tool_definition_scope_invalid", "A ToolDefinition must belong to a Workspace scope.");
        if (string.IsNullOrWhiteSpace(resource.Name) || resource.Name.Length > 128
            || resource.Name.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new ToolDefinitionValidationException("tool_definition_name_invalid", "ToolDefinition names must contain only letters, digits, '.', '-' or '_'.");
        if (string.IsNullOrWhiteSpace(resource.Definition.DisplayName))
            throw new ToolDefinitionValidationException("tool_definition_display_name_required", "ToolDefinition displayName is required.");
        if (string.IsNullOrWhiteSpace(resource.Definition.Flow.Name))
            throw new ToolDefinitionValidationException("tool_definition_flow_required", "A ToolDefinition requires a Flow implementation.");
        if ((resource.Definition.Flow.UseActiveVersion && !string.IsNullOrWhiteSpace(resource.Definition.Flow.Version))
            || (!resource.Definition.Flow.UseActiveVersion && string.IsNullOrWhiteSpace(resource.Definition.Flow.Version)))
            throw new ToolDefinitionValidationException("tool_definition_flow_reference_invalid", "Select either the active Flow version or one exact version.");
        if (resource.Definition.InputSchema.ValueKind != JsonValueKind.Object)
            throw new ToolDefinitionValidationException("tool_definition_input_schema_invalid", "The Tool input schema must be a JSON object.");
        if (resource.Definition.InvocationTimeoutSeconds is < 1 or > 900)
            throw new ToolDefinitionValidationException("tool_definition_timeout_invalid", "Tool invocation timeout must be between 1 and 900 seconds.");
    }

    private async Task EnsureProviderAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, CancellationToken cancellationToken)
    {
        var key = new ResourceKey(ToolResourceKinds.ToolProvider, AgentstrationToolProvider.Name, @namespace);
        if (await store.GetAsync<ToolProviderResource>(key, cancellationToken) is { } existing)
        {
            if (existing.Value.Definition.Mcp?.Internal != true || existing.Value.ScopeRef != scopeRef)
                throw new ToolDefinitionValidationException("tool_definition_provider_conflict", "The reserved Agentstration ToolProvider identity is already in use.");
            return;
        }
        await store.PutAsync(new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            Metadata = new ResourceMetadata { Name = AgentstrationToolProvider.Name, Namespace = @namespace },
            ScopeRef = scopeRef,
            Generation = 1,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
            Definition = new ToolProviderProperties
            {
                DisplayName = "Agentstration",
                ProviderType = ToolProviderType.Mcp,
                Mcp = new McpToolProviderConfiguration { Internal = true },
                Discovery = new ToolProviderDiscoveryState { Status = "connected", Capabilities = new Dictionary<string, bool> { ["tools"] = true } }
            }
        }, null, true, cancellationToken);
    }

    private async Task MaterializeAsync(ToolDefinitionResource definition, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var name = AgentstrationToolProvider.ToolResourceName(definition.Name);
        var key = new ResourceKey(ToolResourceKinds.Tool, name, definition.Namespace);
        var existing = await store.GetAsync<ToolResource>(key, cancellationToken);
        var firstSeen = existing?.Value.Definition.Discovery?.FirstSeenAt ?? now;
        var resource = new ToolResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.Tool,
            Metadata = new ResourceMetadata { Name = name, Namespace = definition.Namespace },
            ScopeRef = definition.ScopeRef,
            Uid = existing?.Value.Uid ?? Guid.Empty,
            Generation = existing is null ? 1 : checked(existing.Value.Generation + 1),
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
            Definition = new ToolResourceProperties
            {
                DisplayName = definition.Definition.DisplayName,
                Description = definition.Definition.Description,
                Enabled = definition.Definition.Enabled,
                RequiresApproval = definition.Definition.RequiresApproval,
                Provider = new ResourceReference(AgentstrationToolProvider.Name, definition.ScopeRef, definition.Namespace),
                ExternalId = definition.Name,
                Discovery = new ToolDiscoveryState { Available = true, FirstSeenAt = firstSeen, LastSeenAt = now },
                Schema = new ToolSchema { Input = definition.Definition.InputSchema.Clone(), Output = definition.Definition.OutputSchema?.Clone() },
                Metadata = new Dictionary<string, JsonElement>
                {
                    ["agentstration.toolDefinitionUid"] = JsonSerializer.SerializeToElement(definition.Uid),
                    ["agentstration.toolDefinitionGeneration"] = JsonSerializer.SerializeToElement(definition.Generation)
                }
            }
        };
        await store.PutAsync(resource, existing?.ETag, existing is null, cancellationToken);
    }
}
