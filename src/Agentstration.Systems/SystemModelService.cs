using System.Text.RegularExpressions;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Systems.Contracts;

namespace Agentstration.Systems;

public sealed class SystemModelException(string code, int statusCode) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

/// <summary>Owns authored logical systems. Does not author resource dependencies or renderer state.</summary>
public sealed partial class SystemModelService(IResourceStore resources, IResourceScopeOperations scopes,
    IResourceIdentityCatalog identities, ISystemGraphAuthorization authorization, ISystemGraphReadScope metadataScope)
{
    public async Task<IReadOnlyList<SystemView>> ListAsync(ResourceScopeRef owner, ResourceNamespace? ns, CancellationToken ct)
    {
        var context = await authorization.GetContextAsync(ct);
        if (!context.Scopes.Contains(owner)) throw Error("system_scope_unavailable", 403);
        var result = new List<SystemView>();
        await using var reader = ResourceSystemGraph.ReadAsync<SystemResource>(resources, owner, SystemResourceKinds.System, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool more;
            using (metadataScope.Enter()) more = await reader.MoveNextAsync();
            if (!more) break;
            var stored = reader.Current;
            if (ns is { } selected && stored.Value.Namespace != selected) continue;
            if (await CanDiscoverAsync(stored.Value, context, ct)) result.Add(await ProjectAsync(stored, context, ct));
            if (result.Count > 1000) throw Error("system_inventory_limit", 422);
        }
        return result.OrderBy(value => value.DisplayName, StringComparer.Ordinal).ThenBy(value => value.Uid).ToArray();
    }

    public async Task<SystemView> GetAsync(ResourceScopeRef owner, ResourceNamespace ns, string name, CancellationToken ct)
    {
        var context = await authorization.GetContextAsync(ct);
        return await ProjectAsync(await ReadAsync(owner, ns, name, context, ct), context, ct);
    }

    public async Task<SystemView> CreateAsync(CreateSystemRequest request, CancellationToken ct)
    {
        var context = await authorization.GetContextAsync(ct);
        Validate(request.DisplayName, request.Description);
        if (!NamePattern().IsMatch(request.Name)) throw Error("system_name_invalid", 400);
        var owner = request.ScopeRef ?? scopes.DefaultScopeRef(SystemResourceKinds.System);
        if (!context.Scopes.Contains(owner)) throw Error("system_scope_unavailable", 403);
        var resource = new SystemResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = SystemResourceKinds.System, ScopeRef = owner,
            Metadata = new() { Name = request.Name, Namespace = request.Namespace ?? ResourceNamespace.Default },
            Generation = 1, Definition = new(request.DisplayName.Trim(), request.Description?.Trim(), [])
        };
        var saved = await scopes.WriteAsync(resource, owner, AuthorizationPermissions.ResourcesWrite,
            token => resources.PutExactAsync(owner, resource, null, true, token), ct);
        return await ProjectAsync(saved, await authorization.GetContextAsync(ct), ct);
    }

    public Task<SystemView> RenameAsync(ResourceScopeRef owner, ResourceNamespace ns, string name, RenameSystemRequest request,
        string? etag, CancellationToken ct)
    {
        Validate(request.DisplayName, request.Description);
        return UpdateAsync(owner, ns, name, etag, (value, _, _) => Task.FromResult(value.Definition with
        { DisplayName = request.DisplayName.Trim(), Description = request.Description?.Trim() }), ct);
    }

    public Task<SystemView> AddMemberAsync(ResourceScopeRef owner, ResourceNamespace ns, string name, SystemMember member,
        string? etag, CancellationToken ct) => UpdateAsync(owner, ns, name, etag, async (value, context, token) =>
        {
            ArgumentNullException.ThrowIfNull(member);
            await EnsureMembershipEditableAsync(value, context, token);
            if (member.Uid == value.Uid || !await MemberVisibleAsync(member, context, token)) throw Error("system_member_unavailable", 422);
            if (value.Definition.Members.Any(existing => existing.Uid == member.Uid)) return value.Definition;
            if (value.Definition.Members.Count >= 128) throw Error("system_membership_unavailable", 422);
            return value.Definition with { Members = [.. value.Definition.Members, member] };
        }, ct);

    public Task<SystemView> RemoveMemberAsync(ResourceScopeRef owner, ResourceNamespace ns, string name, Guid memberUid,
        string? etag, CancellationToken ct) => UpdateAsync(owner, ns, name, etag, async (value, context, token) =>
        {
            await EnsureMembershipEditableAsync(value, context, token);
            var member = value.Definition.Members.FirstOrDefault(item => item.Uid == memberUid);
            if (member is null || !await MemberVisibleAsync(member, context, token)) throw Error("system_member_unavailable", 422);
            return value.Definition with { Members = value.Definition.Members.Where(item => item.Uid != memberUid).ToArray() };
        }, ct);

    public async Task DeleteAsync(ResourceScopeRef owner, ResourceNamespace ns, string name, string? etag, CancellationToken ct)
    {
        var context = await authorization.GetContextAsync(ct);
        var existing = await ReadAsync(owner, ns, name, context, ct);
        CheckETag(existing, etag);
        await scopes.WriteAsync(existing.Value, owner, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            await resources.DeleteExactAsync(new(owner, ns, SystemResourceKinds.System, name), etag, token);
            return true;
        }, ct);
    }

    private async Task<SystemView> UpdateAsync(ResourceScopeRef owner, ResourceNamespace ns, string name, string? etag,
        Func<SystemResource, SystemGraphReadContext, CancellationToken, Task<SystemProperties>> change, CancellationToken ct)
    {
        var context = await authorization.GetContextAsync(ct);
        var existing = await ReadAsync(owner, ns, name, context, ct);
        CheckETag(existing, etag);
        var definition = await change(existing.Value, context, ct);
        var saved = await scopes.WriteAsync(existing.Value, owner, AuthorizationPermissions.ResourcesWrite,
            token => resources.PutExactAsync(owner, existing.Value with
            { Generation = checked(existing.Value.Generation + 1), Definition = definition }, etag, false, token), ct);
        return await ProjectAsync(saved, await authorization.GetContextAsync(ct), ct);
    }

    private async Task<StoredResource<SystemResource>> ReadAsync(ResourceScopeRef owner, ResourceNamespace ns, string name,
        SystemGraphReadContext context, CancellationToken ct)
    {
        if (!context.Scopes.Contains(owner)) throw Error("system_not_found", 404);
        StoredResource<SystemResource>? resource;
        using (metadataScope.Enter()) resource = await resources.GetExactAsync<SystemResource>(new(owner, ns, SystemResourceKinds.System, name), ct);
        if (resource is null || !await CanDiscoverAsync(resource.Value, context, ct)) throw Error("system_not_found", 404);
        return resource;
    }

    private async Task<bool> CanDiscoverAsync(SystemResource value, SystemGraphReadContext context, CancellationToken ct)
    {
        using var trusted = metadataScope.Enter();
        return (await authorization.EvaluateAsync(new(value.Uid, value.ScopeRef!.Value, value.Namespace, value.Kind, value.Name), context, ct)).Discover;
    }

    private async Task<bool> MemberVisibleAsync(SystemMember member, SystemGraphReadContext context, CancellationToken ct)
    {
        if (!context.Scopes.Contains(member.ScopeRef)) return false;
        using var trusted = metadataScope.Enter();
        var identity = await identities.GetIdentityByUidAsync(member.Uid, ct);
        if (identity is null || identity.ScopeRef != member.ScopeRef || identity.Namespace != member.Namespace || identity.Kind != member.Kind || identity.Name != member.Name) return false;
        return (await authorization.EvaluateAsync(new(member.Uid, member.ScopeRef, member.Namespace, member.Kind, member.Name), context, ct)).Discover;
    }

    private async Task EnsureMembershipEditableAsync(SystemResource resource, SystemGraphReadContext context, CancellationToken ct)
    {
        // Refuse a partial membership edit uniformly, before any capacity-dependent decision.
        foreach (var member in resource.Definition.Members)
            if (!await MemberVisibleAsync(member, context, ct)) throw Error("system_membership_unavailable", 422);
    }

    private async Task<SystemView> ProjectAsync(StoredResource<SystemResource> resource, SystemGraphReadContext context, CancellationToken ct)
    {
        if (!await CanDiscoverAsync(resource.Value, context, ct)) throw Error("system_not_found", 404);
        var members = new List<SystemMember>();
        foreach (var member in resource.Value.Definition.Members)
            if (await MemberVisibleAsync(member, context, ct)) members.Add(member);
        var value = resource.Value;
        return new(value.Uid, value.ScopeRef!.Value, value.Namespace, value.Name, value.Definition.DisplayName,
            value.Definition.Description, value.Generation, resource.ETag, members);
    }

    private static void CheckETag(StoredResource<SystemResource> value, string? etag)
    {
        if (etag is null) throw Error("system_precondition_required", 428);
        if (etag != value.ETag) throw Error("system_changed", 412);
    }
    private static void Validate(string label, string? description)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 200 || description?.Length > 2000) throw Error("system_definition_invalid", 400);
    }
    private static SystemModelException Error(string code, int status) => new(code, status);
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
