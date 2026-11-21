using System.Text.RegularExpressions;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Agents;

public sealed record AgentDeploymentAssignmentRequest(string Name, Guid RevisionUid, string Environment,
    AgentDeploymentTarget Target, ResourceScopeRef ScopeRef, ResourceNamespace Namespace);
public sealed record AgentDeploymentView(Guid Uid, ResourceScopeRef ScopeRef, ResourceNamespace Namespace, string Name,
    string ETag, Guid? CurrentConfigurationId, IReadOnlyList<AgentDeploymentConfiguration> Configurations,
    string BindingCoverage, DateTimeOffset UpdatedAt);
public sealed record AgentDefinitionCatalog(SystemGraphNode Agent, IReadOnlyList<SystemGraphNode> Revisions);
public sealed class AgentDeploymentAssignmentException(string code, int status) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

/// <summary>Owns durable assignment values; never provisions a process, routes work or adopts a revision.</summary>
public sealed partial class AgentDeploymentAssignmentService(IResourceStore resources, IResourceScopeOperations scopes,
    ISystemGraphAuthorization authorization, ISystemGraphReadScope metadata, TimeProvider clock)
{
    public async Task<AgentDefinitionCatalog?> DefinitionsAsync(Guid uid, CancellationToken ct)
    {
        var context = await authorization.GetContextAsync(ct);
        var catalog = await ReadDefinitionsAsync(uid, context, ct);
        await VerifyContextAsync(context, ct);
        if (catalog is not null)
            foreach (var node in catalog.Revisions.Prepend(catalog.Agent))
            {
                using var trusted = metadata.Enter();
                if (await authorization.EvaluateAsync(node.Identity, context, ct) is not { Discover: true, InspectBindings: true })
                    throw Error("agent_deployment_unavailable", 403);
            }
        return catalog;
    }
    private async Task<AgentDefinitionCatalog?> ReadDefinitionsAsync(Guid uid, SystemGraphReadContext context, CancellationToken ct)
    {
        using var trusted = metadata.Enter();
        var identity = await resources.GetIdentityByUidAsync(uid, ct);
        if (identity is null || !context.Scopes.Contains(identity.ScopeRef)) return null;
        if (await authorization.EvaluateAsync(new(uid, identity.ScopeRef, identity.Namespace, identity.Kind, identity.Name), context, ct)
            is not { Discover: true, InspectBindings: true }) return null;
        if (identity.Kind == AgentResourceKinds.AgentRevision)
        {
            var revision = await resources.GetExactAsync<AgentRevision>(new(identity.ScopeRef, identity.Namespace, identity.Kind, identity.Name), ct);
            if (revision is null) return null;
            identity = await resources.GetIdentityByUidAsync(revision.Value.AgentUid, ct);
        }
        if (identity?.Kind != AgentResourceKinds.Agent || !context.Scopes.Contains(identity.ScopeRef)) return null;
        var agent = await resources.GetExactAsync<AgentResource>(new(identity.ScopeRef, identity.Namespace, identity.Kind, identity.Name), ct);
        if (agent is null) return null;
        if (await authorization.EvaluateAsync(ResourceSystemGraph.Node(agent.Value).Identity, context, ct)
            is not { Discover: true, InspectBindings: true }) return null;
        var revisions = new List<SystemGraphNode>();
        await foreach (var stored in ResourceSystemGraph.ReadAsync<AgentRevision>(resources, identity.ScopeRef, AgentResourceKinds.AgentRevision, ct))
        {
            if (stored.Value.AgentUid != agent.Value.Uid || stored.Value.Namespace != identity.Namespace) continue;
            if (await authorization.EvaluateAsync(ResourceSystemGraph.Node(stored.Value).Identity, context, ct)
                is not { Discover: true, InspectBindings: true }) continue;
            if (revisions.Count == 128) throw Error("agent_definition_history_limit", 422);
            revisions.Add(ResourceSystemGraph.Node(stored.Value, state: "publication", publication: stored.Value.AgentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), hash: stored.Value.DefinitionHash));
        }
        return new(ResourceSystemGraph.Node(agent.Value) with { DisplayName = agent.Value.Definition.DisplayName },
            revisions.OrderByDescending(node => long.Parse(node.Evidence!.Publication!, System.Globalization.CultureInfo.InvariantCulture)).ToArray());
    }

    public async Task<IReadOnlyList<AgentDeploymentView>> ListAsync(ResourceNamespace ns, Guid? agentUid, CancellationToken ct)
    {
        var context = await authorization.GetContextAsync(ct);
        var result = new List<AgentDeploymentView>();
        foreach (var owner in (context.ReadableScopes ?? context.Scopes).Where(scope => scope.Kind == ResourceScopeKind.Workspace).Distinct())
        {
            await foreach (var stored in ReadAsync(owner, ct))
            {
                // An Agent identity is independent of the namespace of its deployments.
                // Namespace remains the inventory filter when no Agent is selected.
                if (agentUid is null && stored.Value.Namespace != ns) continue;
                var view = await ProjectAsync(stored, context, ct);
                if (view is null || agentUid is { } uid && !view.Configurations.Any(config => config.AgentUid == uid)) continue;
                if (result.Count == 1000) throw Error("agent_deployment_limit_exceeded", 422);
                result.Add(view);
            }
        }
        await VerifyContextAsync(context, ct);
        foreach (var view in result)
        {
            using var trusted = metadata.Enter();
            if (await authorization.EvaluateAsync(new(view.Uid, view.ScopeRef, view.Namespace, AgentResourceKinds.AgentDeployment, view.Name), context, ct)
                is not { Discover: true }) throw Error("agent_deployment_unavailable", 403);
            foreach (var config in view.Configurations)
                if (await RevisionAsync(config.RevisionUid, context, ct) is null) throw Error("agent_deployment_unavailable", 403);
        }
        return result.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<AgentDeploymentView> AssignAsync(AgentDeploymentAssignmentRequest request, string? etag, bool create, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request); Validate(request);
        var context = await authorization.GetContextAsync(ct);
        if (!(context.ReadableScopes ?? context.Scopes).Contains(request.ScopeRef)) throw Error("agent_deployment_unavailable", 403);
        StoredResource<AgentDeployment>? stored;
        using (metadata.Enter()) stored = await resources.GetExactAsync<AgentDeployment>(new(request.ScopeRef, request.Namespace, AgentResourceKinds.AgentDeployment, request.Name), ct);
        if (create && stored is not null) throw Error("agent_deployment_precondition_failed", 412);
        if (!create && (stored is null || string.IsNullOrWhiteSpace(etag) || stored.ETag != etag)) throw Error("agent_deployment_precondition_failed", 412);
        if (stored is not null)
        {
            var visible = await ProjectAsync(stored, context, ct);
            if (visible is null || visible.Configurations.Count != stored.Value.Configurations.Count) throw Error("agent_deployment_unavailable", 403);
            if (stored.Value.Configurations.Count >= 64) throw Error("agent_deployment_history_limit", 422);
        }
        var revision = await RevisionAsync(request.RevisionUid, context, ct);
        if (revision is null) throw Error("agent_deployment_revision_unavailable", 422);
        if (stored?.Value.AgentUid is { } agentUid && agentUid != revision.AgentUid) throw Error("agent_deployment_revision_unavailable", 422);
        var now = clock.GetUtcNow();
        var config = new AgentDeploymentConfiguration(Guid.NewGuid(), revision.AgentUid, revision.Uid,
            revision.ScopeRef!.Value, revision.Namespace, revision.Name, revision.AgentVersion, revision.DefinitionHash,
            request.Environment.Trim(), request.Target with { Id = request.Target.Id.Trim(), Name = request.Target.Name.Trim() }, now);
        var value = new AgentDeployment
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = AgentResourceKinds.AgentDeployment,
            Metadata = new() { Name = request.Name, Namespace = request.Namespace },
            ScopeRef = request.ScopeRef,
            Uid = stored?.Value.Uid ?? Guid.Empty,
            Generation = stored is null ? 1 : checked(stored.Value.Generation + 1),
            AgentUid = revision.AgentUid,
            RevisionUid = revision.Uid,
            AgentName = revision.AgentName,
            AgentNamespace = revision.Namespace,
            RevisionName = revision.Name,
            Environment = config.Environment,
            RuntimeProfileName = revision.Definition.RuntimeProfileName,
            RuntimeProfileNamespace = revision.Definition.RuntimeProfileNamespace,
            HostingMode = AgentHostingMode.SharedHost,
            DesiredState = stored?.Value.DesiredState ?? DesiredAgentState.Running,
            ProvisioningState = ProvisioningState.Succeeded,
            OperationalState = OperationalState.Unavailable,
            Configurations = [.. stored?.Value.Configurations ?? [], config],
            UpdatedAt = now
        };
        await VerifyContextAsync(context, ct);
        var saved = await scopes.WriteAsync(value, request.ScopeRef, AuthorizationPermissions.ResourcesWrite,
            token => resources.PutExactAsync(request.ScopeRef, value, etag, create, token), ct);
        return await ProjectAsync(saved, await authorization.GetContextAsync(ct), ct) ?? throw Error("agent_deployment_unavailable", 403);
    }

    private async Task<AgentDeploymentView?> ProjectAsync(StoredResource<AgentDeployment> stored, SystemGraphReadContext context, CancellationToken ct)
    {
        SystemGraphAccess allowed;
        using (metadata.Enter()) allowed = await authorization.EvaluateAsync(ResourceSystemGraph.Node(stored.Value).Identity, context, ct);
        if (!allowed.Discover) return null;
        var configurations = new List<AgentDeploymentConfiguration>();
        if (allowed.InspectBindings)
            foreach (var config in stored.Value.Configurations)
            {
                var revision = await RevisionAsync(config.RevisionUid, context, ct);
                if (revision?.AgentUid == config.AgentUid && revision.ScopeRef == config.RevisionOwner && revision.Namespace == config.RevisionNamespace
                    && revision.Name == config.RevisionName && revision.DefinitionHash == config.DefinitionHash) configurations.Add(config);
            }
        var current = stored.Value.Configurations.LastOrDefault()?.Id;
        return new(stored.Value.Uid, stored.Value.ScopeRef!.Value, stored.Value.Namespace, stored.Value.Name, stored.ETag,
            configurations.Any(config => config.Id == current) ? current : null, configurations,
            configurations.Count == 0 ? "unavailable" : "recorded", stored.Value.UpdatedAt);
    }

    private async Task<AgentRevision?> RevisionAsync(Guid uid, SystemGraphReadContext context, CancellationToken ct)
    {
        using var trusted = metadata.Enter();
        var identity = await resources.GetIdentityByUidAsync(uid, ct);
        if (identity is null || identity.Kind != AgentResourceKinds.AgentRevision || !context.Scopes.Contains(identity.ScopeRef)) return null;
        var graphIdentity = new SystemGraphIdentity(uid, identity.ScopeRef, identity.Namespace, identity.Kind, identity.Name);
        if (await authorization.EvaluateAsync(graphIdentity, context, ct) is not { Discover: true, InspectBindings: true }) return null;
        var stored = await resources.GetExactAsync<AgentRevision>(new(identity.ScopeRef, identity.Namespace, identity.Kind, identity.Name), ct);
        return stored?.Value.Uid == uid ? stored.Value : null;
    }

    private async IAsyncEnumerable<StoredResource<AgentDeployment>> ReadAsync(ResourceScopeRef owner,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var offset = 0; ; offset += 200)
        {
            IReadOnlyList<StoredResource<AgentDeployment>> page;
            using (metadata.Enter()) page = await resources.ListExactAsync<AgentDeployment>(owner, AgentResourceKinds.AgentDeployment, offset, 200, ct);
            foreach (var value in page) yield return value;
            if (page.Count < 200) yield break;
        }
    }
    private async Task VerifyContextAsync(SystemGraphReadContext expected, CancellationToken ct)
    {
        var current = await authorization.GetContextAsync(ct);
        if (System.Text.Json.JsonSerializer.Serialize(current) != System.Text.Json.JsonSerializer.Serialize(expected)) throw Error("agent_deployment_unavailable", 403);
    }
    private static void Validate(AgentDeploymentAssignmentRequest value)
    {
        static bool Text(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Any(char.IsControl);
        if (value.ScopeRef.Kind != ResourceScopeKind.Workspace || value.RevisionUid == Guid.Empty || value.Name is null || !NamePattern().IsMatch(value.Name)
            || !Text(value.Environment, 100) || value.Target is null || !Text(value.Target.Id, 200) || !Text(value.Target.Name, 200)
            || value.Target.Kind is not ("worker-pool" or "host" or "cluster" or "remote-endpoint")
            || value.Target.Runtime is not ("awp-worker" or "agentstration-server" or "remote-runtime")) throw Error("agent_deployment_assignment_invalid", 400);
    }
    private static AgentDeploymentAssignmentException Error(string code, int status) => new(code, status);
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,62}$", RegexOptions.CultureInvariant)] private static partial Regex NamePattern();
}
