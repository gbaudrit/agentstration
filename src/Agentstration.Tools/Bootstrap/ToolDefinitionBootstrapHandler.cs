using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Tools.Bootstrap;

/// <summary>Creates an immutable protected relay through the owning Tool lifecycle.</summary>
public sealed class ToolDefinitionBootstrapHandler(ToolDefinitionService tools, IResourceStore resources, IResourceReferenceResolver references,
    ICurrentRequestContext current) : IBootstrapResourceHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Kind => ToolResourceKinds.ToolDefinition;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;

    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument document,
        BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken token)
    {
        var resource = await ResolveAsync(document, operation, token);
        var disposition = await DispositionAsync(resource, token);
        if (disposition != BootstrapResourceDisposition.Conflict)
            planning.Register(ToolResourceKinds.Tool, PublicName(resource), resource.Namespace.Value, document);
        return new(disposition);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument document,
        BootstrapResourceOperationContext operation, CancellationToken token)
    {
        var resource = await ResolveAsync(document, operation, token);
        var disposition = await DispositionAsync(resource, token);
        if (disposition == BootstrapResourceDisposition.Conflict) return BootstrapResourceApplyResult.Conflict;
        if (disposition == BootstrapResourceDisposition.Skip) return BootstrapResourceApplyResult.Skipped;
        var existing = await tools.GetExactAsync(resource.Name, resource.Namespace, resource.ScopeRef!.Value, token);
        if (existing is null) _ = await tools.PutAsync(resource, null, true, token);
        else _ = await tools.PublishAsync(resource.ScopeRef.Value, resource.Name, resource.Namespace,
            resource.Definition.Version, token, existing.ETag);
        return BootstrapResourceApplyResult.Created;
    }

    private async Task<BootstrapResourceDisposition> DispositionAsync(ToolDefinitionResource resource, CancellationToken token)
    {
        var expected = await tools.ValidateRelayPublicationAsync(resource, token);
        var existing = await tools.GetExactAsync(resource.Name, resource.Namespace, resource.ScopeRef!.Value, token);
        var publication = await tools.GetPublicationAsync(ToolDefinitionService.VersionName(resource.Name, resource.Definition.Version),
            resource.Namespace, resource.ScopeRef!.Value, token);
        if (publication is not null)
        {
            if (existing is null || existing.Value.Uid != publication.Value.DefinitionUid || !Same(publication.Value.Definition, expected))
                return BootstrapResourceDisposition.Conflict;
            var materialized = await resources.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(resource.ScopeRef.Value,
                resource.Namespace, ToolResourceKinds.Tool, PublicName(resource)), token);
            return materialized is null ? BootstrapResourceDisposition.Create : BootstrapResourceDisposition.Skip;
        }
        return existing is null || Same(await tools.ValidateRelayPublicationAsync(existing.Value, token), expected)
            ? BootstrapResourceDisposition.Create : BootstrapResourceDisposition.Conflict;
    }

    private async Task<ToolDefinitionResource> ResolveAsync(BootstrapResourceDocument document,
        BootstrapResourceOperationContext operation, CancellationToken token)
    {
        if (operation.ProfileScope != Scope || operation.Target?.WorkspaceId is not { } workspace
            || current.AccessMode != ControlPlaneAccessMode.Workspace || current.Current.WorkspaceId != workspace
            || operation.ActorPrincipalId is { } actor && actor != current.Current.PrincipalId
            || document.Kind != Kind || document.ApiVersion != ResourceApiVersions.CoreV1)
            throw Invalid("bootstrap_tool_scope_invalid");
        var root = JsonNode.Parse(document.Definition.GetRawText()) as JsonObject ?? throw Invalid("bootstrap_tool_definition_invalid");
        if (root["toolKind"]?.GetValue<string>() != "relay" || root["target"] is not JsonObject target)
            throw Invalid("bootstrap_tool_relay_required");
        if (target.Any(property => property.Key is not ("name" or "namespace" or "scopeRef")))
            throw Invalid("bootstrap_tool_target_semantic_required");
        var reference = target.Deserialize<ResourceReference>(Json) ?? throw Invalid("bootstrap_tool_target_required");
        var scope = ResourceScopeRef.Workspace(workspace);
        var source = await references.ResolveAsync<ToolResource>(reference, document.Metadata.Namespace,
            ToolResourceKinds.Tool, scope, token) ?? throw Invalid("bootstrap_tool_target_unavailable");
        root["target"] = JsonSerializer.SerializeToNode(new ToolRelayTarget
        {
            Name = source.Value.Name,
            Namespace = source.Value.Namespace,
            ScopeRef = source.Value.ScopeRef!.Value,
            Uid = source.Value.Uid,
            Generation = source.Value.Generation
        }, Json);
        var definition = root.Deserialize<ToolDefinitionProperties>(Json) ?? throw Invalid("bootstrap_tool_definition_invalid");
        if (!definition.Publish || !definition.Enabled) throw Invalid("bootstrap_tool_publication_required");
        return new() { ApiVersion = document.ApiVersion, Kind = Kind, Metadata = document.Metadata, ScopeRef = scope, Definition = definition };
    }

    private static bool Same(ToolDefinitionProperties left, ToolDefinitionProperties right) =>
        ToolDefinitionService.SameSchema(JsonSerializer.SerializeToElement<ToolDefinitionProperties>(left, Json),
            JsonSerializer.SerializeToElement<ToolDefinitionProperties>(right, Json));
    private static string PublicName(ToolDefinitionResource resource) =>
        AgentstrationToolProvider.ToolResourceName(ToolDefinitionService.VersionName(resource.Name, resource.Definition.Version));
    private static ToolDefinitionValidationException Invalid(string code) => new(code, code);
}
