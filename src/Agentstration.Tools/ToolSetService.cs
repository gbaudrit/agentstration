using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;

namespace Agentstration.Tools;

public sealed class ToolSetService(
    IResourceStore store,
    IResourceReferenceResolver references,
    IResourceScopeOperations scopeOperations,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider,
    IEnumerable<IToolSetDeletionGuard>? deletionGuards = null)
{
    public Task<IReadOnlyList<StoredResource<ToolSetResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<ToolSetResource>(ToolResourceKinds.ToolSet, cancellationToken);

    public Task<StoredResource<ToolSetResource>?> GetAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        store.GetAsync<ToolSetResource>(new(ToolResourceKinds.ToolSet, name, @namespace), cancellationToken);

    public Task<StoredResource<ToolSetResource>?> GetExactAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        store.GetExactAsync<ToolSetResource>(ScopedResourceAddress.Create(scopeRef, @namespace, ToolResourceKinds.ToolSet, name), cancellationToken);

    public async Task<IReadOnlyList<StoredResource<ToolSetVersionResource>>> ListVersionsAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        (await store.ListAsync<ToolSetVersionResource>(@namespace, ToolResourceKinds.ToolSetVersion, 0, 1000, cancellationToken))
        .Where(value => string.Equals(value.Value.ToolSetName, name, StringComparison.Ordinal))
        .OrderBy(value => value.Value.Version, StringComparer.Ordinal)
        .ToArray();

    public async Task<StoredResource<ToolSetVersionResource>?> GetVersionAsync(
        ResourceNamespace @namespace,
        string name,
        string version,
        CancellationToken cancellationToken)
    {
        var key = new ResourceKey(ToolResourceKinds.ToolSetVersion, VersionResourceName(name, version), @namespace);
        var stored = await store.GetAsync<ToolSetVersionResource>(key, cancellationToken);
        return stored is not null
            && string.Equals(stored.Value.ToolSetName, name, StringComparison.Ordinal)
            && string.Equals(stored.Value.Version, version, StringComparison.Ordinal)
                ? stored
                : null;
    }

    public async Task<StoredResource<ToolSetVersionResource>?> GetVersionExactAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace @namespace,
        string name,
        string version,
        CancellationToken cancellationToken)
    {
        var stored = await store.GetExactAsync<ToolSetVersionResource>(ScopedResourceAddress.Create(scopeRef,
            @namespace, ToolResourceKinds.ToolSetVersion, VersionResourceName(name, version)), cancellationToken);
        return stored is not null
            && string.Equals(stored.Value.ToolSetName, name, StringComparison.Ordinal)
            && string.Equals(stored.Value.Version, version, StringComparison.Ordinal)
                ? stored
                : null;
    }

    public async Task<StoredResource<ToolSetResource>> CreateAsync(
        ToolSetResource resource,
        CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ToolResourceKinds.ToolSet);
        var desired = resource with { ScopeRef = scopeRef, Generation = 1, Status = Succeeded() };
        Validate(desired);
        ResourceScopePolicy.EnsureAllowed(desired, scopeRef);
        await ResolveMembersAsync(desired, scopeRef, cancellationToken);
        var stored = await scopeOperations.WriteAsync(desired, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, desired, null, true, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.ToolSetCreated, scopeRef, cancellationToken);
        if (stored.Value.Definition.Publish)
            _ = await PublishAsync(stored.Value.Namespace, stored.Value.Name, stored.Value.Definition.Version, cancellationToken);
        return stored;
    }

    public async Task<StoredResource<ToolSetResource>> PutAsync(
        ResourceNamespace @namespace,
        string name,
        ToolSetProperties definition,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolSet, name, @namespace));
        var scopeRef = RequiredScope(existing.Value);
        var desired = existing.Value with
        {
            Definition = definition,
            Generation = checked(existing.Value.Generation + 1),
            Status = Succeeded()
        };
        Validate(desired);
        await ResolveMembersAsync(desired, scopeRef, cancellationToken);
        var stored = await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, desired, ifMatch, false, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.ToolSetUpdated, scopeRef, cancellationToken);
        if (stored.Value.Definition.Publish)
            _ = await PublishAsync(stored.Value.Namespace, stored.Value.Name, stored.Value.Definition.Version, cancellationToken);
        return stored;
    }

    public async Task DeleteAsync(
        ResourceNamespace @namespace,
        string name,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolSet, name, @namespace));
        var scopeRef = RequiredScope(existing.Value);
        foreach (var guard in deletionGuards ?? [])
            await guard.ValidateDeleteAsync(scopeRef, @namespace, name, cancellationToken);
        var versions = await ListVersionsAsync(@namespace, name, cancellationToken);
        await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            foreach (var version in versions)
                await store.DeleteExactAsync(ScopedResourceAddress.Create(scopeRef, @namespace,
                    ToolResourceKinds.ToolSetVersion, version.Value.Name), version.ETag, token);
            await store.DeleteExactAsync(ScopedResourceAddress.Create(scopeRef, @namespace, ToolResourceKinds.ToolSet, name), ifMatch, token);
            return true;
        }, cancellationToken);
        await AuditAsync(SecurityAuditActions.ToolSetDeleted, scopeRef, cancellationToken);
    }

    public async Task<StoredResource<ToolSetVersionResource>> PublishAsync(
        ResourceNamespace @namespace,
        string name,
        string version,
        CancellationToken cancellationToken)
    {
        ValidateVersion(version);
        var source = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolSet, name, @namespace));
        if (!source.Value.Definition.Enabled)
            throw new ToolSetValidationException("tool_set_disabled", "A disabled ToolSet cannot be published.");
        if (!string.Equals(source.Value.Definition.Version, version, StringComparison.Ordinal))
            throw new ToolSetValidationException("tool_set_version_mismatch", "The requested version must match the ToolSet draft version.");
        var scopeRef = RequiredScope(source.Value);
        var members = await ResolveMembersAsync(source.Value, scopeRef, cancellationToken);
        var hash = DefinitionHash(source.Value, members);
        if (await GetVersionAsync(@namespace, name, version, cancellationToken) is { } existing)
        {
            if (string.Equals(existing.Value.DefinitionHash, hash, StringComparison.Ordinal)) return existing;
            throw new ToolSetValidationException("tool_set_version_immutable", $"Published ToolSet version '{version}' is immutable.");
        }

        var published = new ToolSetVersionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolSetVersion,
            Metadata = new ResourceMetadata
            {
                Name = VersionResourceName(name, version),
                Namespace = @namespace,
                Tags = source.Value.Metadata.Tags,
                Annotations = source.Value.Metadata.Annotations
            },
            ScopeRef = scopeRef,
            ToolSetUid = source.Value.Uid,
            ToolSetName = name,
            ToolSetGeneration = source.Value.Generation,
            Version = version,
            DefinitionHash = hash,
            PublishedAt = timeProvider.GetUtcNow(),
            Members = members,
            Generation = 1,
            Status = Succeeded()
        };
        var stored = await scopeOperations.WriteAsync(source.Value, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.CreateImmutableAsync(published, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.ToolSetVersionPublished, scopeRef, cancellationToken);
        return stored;
    }

    public async Task<ToolSetRouteSelection> ResolveRouteAsync(
        ResourceNamespace @namespace,
        string name,
        string version,
        string capability,
        string? route,
        CancellationToken cancellationToken)
    {
        var published = await GetVersionAsync(@namespace, name, version, cancellationToken)
            ?? throw new ToolSetValidationException("tool_set_version_not_found", $"Published ToolSet '{@namespace}/{name}:{version}' was not found.");
        var candidates = published.Value.Members.Where(member =>
            string.Equals(member.Capability, capability, StringComparison.Ordinal)
            && (route is null || string.Equals(member.Route, route, StringComparison.Ordinal))).ToArray();
        if (candidates.Length == 0)
            throw new ToolSetValidationException("tool_route_not_found", $"ToolSet '{@namespace}/{name}:{version}' has no compatible route for capability '{capability}'.");
        if (candidates.Length > 1)
            throw new ToolSetValidationException("tool_route_ambiguous", $"ToolSet '{@namespace}/{name}:{version}' has multiple compatible routes for capability '{capability}'.");
        var selected = candidates[0];
        var current = await store.GetAsync<ToolResource>(new(ToolResourceKinds.Tool, selected.ToolName, selected.ToolNamespace), cancellationToken);
        if (current is null || current.Value.Uid != selected.ToolUid || current.Value.Generation != selected.ToolGeneration)
            throw new ToolSetValidationException("tool_set_member_stale", $"Published Tool member '{selected.ToolNamespace}/{selected.ToolName}' no longer matches its pinned revision.");
        if (!current.Value.Definition.Enabled || current.Value.Definition.Discovery?.Available != true)
            throw new ToolSetValidationException("tool_set_member_unavailable", $"Published Tool member '{selected.ToolNamespace}/{selected.ToolName}' is unavailable.");
        return new(name, @namespace, version, selected);
    }

    public async Task<ToolSetRouteSelection> ResolveRouteExactAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace @namespace,
        string name,
        string version,
        string capability,
        string? route,
        CancellationToken cancellationToken)
    {
        var published = await GetVersionExactAsync(scopeRef, @namespace, name, version, cancellationToken)
            ?? throw new ToolSetValidationException("tool_set_version_not_found", $"Published ToolSet '{@namespace}/{name}:{version}' was not found in scope '{scopeRef}'.");
        var candidates = published.Value.Members.Where(member =>
            string.Equals(member.Capability, capability, StringComparison.Ordinal)
            && (route is null || string.Equals(member.Route, route, StringComparison.Ordinal))).ToArray();
        if (candidates.Length == 0)
            throw new ToolSetValidationException("tool_route_not_found", $"ToolSet '{@namespace}/{name}:{version}' has no compatible route for capability '{capability}'.");
        if (candidates.Length > 1)
            throw new ToolSetValidationException("tool_route_ambiguous", $"ToolSet '{@namespace}/{name}:{version}' has multiple compatible routes for capability '{capability}'.");
        var selected = candidates[0];
        var current = await store.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(
            scopeRef, selected.ToolNamespace, ToolResourceKinds.Tool, selected.ToolName), cancellationToken);
        if (current is null || current.Value.Uid != selected.ToolUid || current.Value.Generation != selected.ToolGeneration)
            throw new ToolSetValidationException("tool_set_member_stale", $"Published Tool member '{selected.ToolNamespace}/{selected.ToolName}' no longer matches its pinned revision.");
        if (!current.Value.Definition.Enabled || current.Value.Definition.Discovery?.Available != true)
            throw new ToolSetValidationException("tool_set_member_unavailable", $"Published Tool member '{selected.ToolNamespace}/{selected.ToolName}' is unavailable.");
        return new(name, @namespace, version, selected);
    }

    private async Task<IReadOnlyList<PublishedToolSetMember>> ResolveMembersAsync(
        ToolSetResource resource,
        ResourceScopeRef scopeRef,
        CancellationToken cancellationToken)
    {
        var members = new List<PublishedToolSetMember>(resource.Definition.Members.Count);
        foreach (var member in resource.Definition.Members)
        {
            var stored = await references.ResolveAsync<ToolResource>(member.Tool, resource.Namespace,
                ToolResourceKinds.Tool, scopeRef, cancellationToken)
                ?? throw new ToolSetValidationException("tool_set_member_not_found", $"Tool member '{member.Tool.Name}' was not found.");
            var tool = stored.Value;
            if (!tool.Definition.Enabled || tool.Definition.Discovery?.Available != true)
                throw new ToolSetValidationException("tool_set_member_unavailable", $"Tool member '{tool.Address}' is unavailable.");
            var provider = tool.Definition.Provider?.Resolve(tool.Namespace, ToolResourceKinds.ToolProvider)
                ?? throw new ToolSetValidationException("tool_set_member_provider_missing", $"Tool member '{tool.Address}' has no provider mapping.");
            var schema = tool.Definition.Schema
                ?? throw new ToolSetValidationException("tool_set_member_schema_missing", $"Tool member '{tool.Address}' has no schema.");
            members.Add(new PublishedToolSetMember
            {
                Capability = member.Capability,
                Route = member.Route,
                ToolName = tool.Name,
                ToolNamespace = tool.Namespace,
                ToolUid = tool.Uid,
                ToolGeneration = tool.Generation,
                ProviderName = provider.Name,
                ProviderNamespace = provider.Namespace,
                ExternalToolId = tool.Definition.ExternalId
                    ?? throw new ToolSetValidationException("tool_set_member_external_id_missing", $"Tool member '{tool.Address}' has no external identity."),
                InputSchema = schema.Input.Clone(),
                OutputSchema = schema.Output?.Clone(),
                RequiresApproval = tool.Definition.RequiresApproval
            });
        }
        return members;
    }

    public static void Validate(ToolSetResource resource)
    {
        if (resource.Kind != ToolResourceKinds.ToolSet || resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw new ToolSetValidationException("tool_set_identity_invalid", "ToolSet kind and apiVersion are required.");
        if (resource.ScopeRef is not { Kind: ResourceScopeKind.Workspace })
            throw new ToolSetValidationException("tool_set_scope_invalid", "A ToolSet must belong to a Workspace scope.");
        ValidateToken(resource.Name, "tool_set_name_invalid", "ToolSet name");
        if (string.IsNullOrWhiteSpace(resource.Definition?.DisplayName) || resource.Definition.DisplayName.Length > 200)
            throw new ToolSetValidationException("tool_set_display_name_invalid", "ToolSet display names must contain 1 to 200 characters.");
        if (resource.Definition.Description?.Length > 2_000)
            throw new ToolSetValidationException("tool_set_description_too_long", "ToolSet descriptions cannot exceed 2000 characters.");
        ValidateVersion(resource.Definition.Version);
        if (resource.Definition.Members.Count == 0)
            throw new ToolSetValidationException("tool_set_members_required", "A ToolSet requires at least one Tool member.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in resource.Definition.Members)
        {
            ValidateToken(member.Tool.Name, "tool_set_member_invalid", "Tool member name");
            ValidateToken(member.Capability, "tool_set_capability_invalid", "Tool capability");
            ValidateToken(member.Route, "tool_set_route_invalid", "Tool route");
            var ns = member.Tool.Namespace ?? resource.Namespace;
            if (!identities.Add($"{ns.Value}/{member.Tool.Name}"))
                throw new ToolSetValidationException("tool_set_member_duplicate", $"Tool member '{member.Tool.Name}' is duplicated.");
            if (!routes.Add($"{member.Capability}|{member.Route}"))
                throw new ToolSetValidationException("tool_set_route_duplicate", $"Route '{member.Route}' is duplicated for capability '{member.Capability}'.");
        }
    }

    private static void ValidateToken(string value, string code, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new ToolSetValidationException(code, $"{label} must contain only letters, digits, '.', '-' or '_' and be at most 128 characters.");
    }

    private static void ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 64
            || version.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-'))
            throw new ToolSetValidationException("tool_set_version_invalid", "ToolSet versions must contain only letters, digits, '.' or '-' and be at most 64 characters.");
    }

    private static string DefinitionHash(ToolSetResource source, IReadOnlyList<PublishedToolSetMember> members)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            source.Name,
            Namespace = source.Namespace.Value,
            source.Definition.DisplayName,
            source.Definition.Description,
            source.Definition.Version,
            Members = members
        });
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string VersionResourceName(string name, string version)
    {
        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(version)))[..16];
        return $"{name}--{suffix}";
    }

    private static ResourceScopeRef RequiredScope(Resource resource) => resource.ScopeRef
        ?? throw new ToolSetValidationException("tool_set_scope_invalid", "A ToolSet requires an ownership scope.");
    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
    private Task AuditAsync(string action, ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
        audit.WriteAsync(new(action, WorkspaceId: scopeRef.TargetId), cancellationToken);
}
