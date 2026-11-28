using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Security.Contracts;

namespace Agentstration.ResourceManagement;

public sealed record StoredResourceAccessPolicy(
    ResourceAccessPolicy Policy,
    string ETag,
    DateTimeOffset UpdatedAt);

public interface IResourceAccessPolicyStore
{
    Task<IReadOnlyList<StoredResourceAccessPolicy>> ListAsync(ResourceScopeRef ownerScopeRef,
        CancellationToken cancellationToken);
    Task<StoredResourceAccessPolicy?> GetAsync(Guid resourceUid, ResourceScopeRef ownerScopeRef,
        CancellationToken cancellationToken);
    Task<StoredResourceAccessPolicy> PutAsync(ResourceAccessPolicy policy, string? ifMatch,
        CancellationToken cancellationToken);
    Task DeleteAsync(Guid resourceUid, ResourceScopeRef ownerScopeRef, string? ifMatch,
        CancellationToken cancellationToken);
}

public sealed class ResourceAccessPolicyException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ResourceAccessPolicyService(
    IResourceIdentityCatalog resources,
    IResourceAccessPolicyStore policies,
    IResourceScopeResolver scopes,
    IResourceScopeOperations scopeOperations,
    ISecurityAuditWriter audit) : IResourceAccessPolicyService
{
    private const int MaximumGrants = 256;
    private const string ResourcesWritePermission = "resources/write";

    public async Task<IReadOnlyList<ResourceAccessPolicyResponse>> ListAsync(
        ResourceScopeRef ownerScopeRef,
        CancellationToken cancellationToken) =>
        (await policies.ListAsync(ownerScopeRef, cancellationToken)).Select(ToResponse).ToArray();

    public async Task<ResourceAccessPolicyResponse?> GetAsync(Guid resourceUid,
        CancellationToken cancellationToken)
    {
        var resource = await resources.GetIdentityByUidAsync(resourceUid, cancellationToken);
        if (resource is null) return null;
        var stored = await policies.GetAsync(resourceUid, resource.ScopeRef, cancellationToken);
        return stored is null ? null : ToResponse(stored);
    }

    public async Task<ResourceAccessPolicyResponse> PutAsync(ResourceAccessPolicy policy, string? ifMatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var current = await RequireCurrentIdentityAsync(policy.Resource, cancellationToken);
        await ValidateGrantsAsync(current.ScopeRef, policy.Grants, cancellationToken);
        ValidateActions(policy.AllDescendantActions, allowEmpty: true);
        if (current.ScopeRef.Kind == ResourceScopeKind.Workspace && policy.AllDescendantActions.Count != 0)
            throw Invalid("access_descendants_unavailable", "A Workspace has no descendant scopes.");
        var canonical = policy with { Resource = CanonicalIdentity(policy.Resource, current) };
        var stored = await scopeOperations.WriteAsync(current.Kind, current.ScopeRef,
            ResourcesWritePermission,
            token => policies.PutAsync(canonical, ifMatch, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.ResourceAccessPolicySet, current.ScopeRef, null, cancellationToken);
        return ToResponse(stored);
    }

    public async Task DeleteAsync(Guid resourceUid, string? ifMatch, CancellationToken cancellationToken)
    {
        var current = await resources.GetIdentityByUidAsync(resourceUid, cancellationToken)
            ?? throw Invalid("resource_not_found", $"Resource '{resourceUid:D}' was not found.");
        await scopeOperations.WriteAsync(current.Kind, current.ScopeRef, ResourcesWritePermission,
            async token =>
            {
                await policies.DeleteAsync(resourceUid, current.ScopeRef, ifMatch, token);
                return true;
            }, cancellationToken);
        await AuditAsync(SecurityAuditActions.ResourceAccessPolicyDeleted, current.ScopeRef, null, cancellationToken);
    }

    public async Task<ResourceAccessDecision> EvaluateAsync(ResourceAccessIdentity resource,
        ResourceScopeRef consumerScopeRef, ResourceAccessAction action, CancellationToken cancellationToken)
    {
        if (!IsValidIdentity(resource) || consumerScopeRef == default || !Enum.IsDefined(action))
            return await DecisionAsync(false, ResourceAccessDecisionReasons.RequestInvalid,
                resource.OwnerScopeRef, cancellationToken);
        if (consumerScopeRef == resource.OwnerScopeRef)
        {
            var owned = await resources.GetIdentityByUidAsync(resource.Uid, cancellationToken);
            // Identity discovery is filtered by the ambient caller. Private service dependencies
            // can use a different trusted authorization scope and still require graph validation.
            if (owned is not null)
            {
                var ownerScope = await scopes.ResolveAsync(consumerScopeRef, cancellationToken);
                var reason = !Matches(resource, owned) ? ResourceAccessDecisionReasons.ResourceIdentityStale
                    : ownerScope is null ? ResourceAccessDecisionReasons.ConsumerOutsideOwnerTree
                    : ResourceAccessDecisionReasons.SameScope;
                return await DecisionAsync(reason == ResourceAccessDecisionReasons.SameScope, reason,
                    resource.OwnerScopeRef, cancellationToken);
            }
        }
        if (resources is IResourceAccessGraphReader reader
            && await reader.GetAccessGraphAsync(resource.Uid, cancellationToken) is { } graph)
        {
            var graphConsumer = await scopes.ResolveAsync(consumerScopeRef, cancellationToken);
            if (graphConsumer is null) return await DecisionAsync(false, ResourceAccessDecisionReasons.ConsumerOutsideOwnerTree,
                resource.OwnerScopeRef, cancellationToken);
            var evaluated = graph.Evaluate(resource, consumerScopeRef,
                graphConsumer.Ancestors.Select(value => value.Ref).ToArray(), action);
            return await DecisionAsync(evaluated.Allowed, evaluated.ReasonCode, resource.OwnerScopeRef, cancellationToken);
        }
        var current = await resources.GetIdentityByUidAsync(resource.Uid, cancellationToken);
        if (current is null)
            return await DecisionAsync(false, ResourceAccessDecisionReasons.ResourceNotFound,
                resource.OwnerScopeRef, cancellationToken);
        if (!Matches(resource, current))
            return await DecisionAsync(false, ResourceAccessDecisionReasons.ResourceIdentityStale,
                current.ScopeRef, cancellationToken);
        if (consumerScopeRef == current.ScopeRef)
            return await DecisionAsync(true, ResourceAccessDecisionReasons.SameScope,
                current.ScopeRef, cancellationToken);

        var owner = await scopes.ResolveAsync(current.ScopeRef, cancellationToken);
        var consumer = await scopes.ResolveAsync(consumerScopeRef, cancellationToken);
        if (owner is null || consumer is null || !consumer.Ancestors.Any(value => value.Id == owner.Scope.Id))
            return await DecisionAsync(false, ResourceAccessDecisionReasons.ConsumerOutsideOwnerTree,
                current.ScopeRef, cancellationToken);

        if (current.AccessPolicySource is { } source)
        {
            if (source.Uid == current.Uid || source.OwnerScopeRef != current.ScopeRef)
                return await DecisionAsync(false, ResourceAccessDecisionReasons.RequestInvalid,
                    current.ScopeRef, cancellationToken);
            return await EvaluateAsync(source, consumerScopeRef, action, cancellationToken);
        }

        var stored = await policies.GetAsync(resource.Uid, current.ScopeRef, cancellationToken);
        if (stored is null)
            return await DecisionAsync(false, ResourceAccessDecisionReasons.PolicyMissing,
                current.ScopeRef, cancellationToken);
        if (!Matches(stored.Policy.Resource, current))
            return await DecisionAsync(false, ResourceAccessDecisionReasons.PolicyIdentityStale,
                current.ScopeRef, cancellationToken);

        var result = ResourceAccessRules.Evaluate(stored.Policy, consumerScopeRef,
            consumer.Ancestors.Select(value => value.Ref).ToArray(), action);
        return await DecisionAsync(result.Allowed, result.ReasonCode, current.ScopeRef, cancellationToken);
    }

    private async Task<ResourceIdentitySnapshot> RequireCurrentIdentityAsync(ResourceAccessIdentity identity,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var current = await resources.GetIdentityByUidAsync(identity.Uid, cancellationToken)
            ?? throw Invalid("resource_not_found", $"Resource '{identity.Uid:D}' was not found.");
        if (!Matches(identity, current))
            throw Invalid("resource_identity_stale", "The supplied resource identity does not match the current exact resource.");
        return current;
    }

    private async Task ValidateGrantsAsync(ResourceScopeRef ownerScopeRef,
        IReadOnlyList<ResourceAccessGrant> grants, CancellationToken cancellationToken)
    {
        if (grants is null || grants.Count > MaximumGrants)
            throw Invalid("access_grants_invalid", $"An access policy must contain at most {MaximumGrants} grants.");
        var owner = await scopes.ResolveAsync(ownerScopeRef, cancellationToken)
            ?? throw Invalid("owner_scope_not_found", $"Resource scope '{ownerScopeRef}' was not found.");
        var seen = new HashSet<ResourceScopeRef>();
        foreach (var grant in grants)
        {
            if (grant is null || grant.TargetScopeRef == default)
                throw Invalid("grant_target_required", "An access grant requires a target scope.");
            if (!seen.Add(grant.TargetScopeRef))
                throw Invalid("grant_target_duplicate", $"Duplicate access grant for scope '{grant.TargetScopeRef}'.");
            ValidateActions(grant.Actions, allowEmpty: true);
            ValidateActions(grant.DeniedActions, allowEmpty: true);
            if (grant.Actions.Count + grant.DeniedActions.Count == 0
                || grant.Actions.Intersect(grant.DeniedActions).Any())
                throw Invalid("grant_actions_invalid", "A grant requires disjoint allow/deny actions.");
            var target = await scopes.ResolveAsync(grant.TargetScopeRef, cancellationToken)
                ?? throw Invalid("grant_target_not_found", $"Resource scope '{grant.TargetScopeRef}' was not found.");
            if (target.Scope.Ref == owner.Scope.Ref
                || !target.Ancestors.Any(value => value.Id == owner.Scope.Id))
                throw Invalid("grant_target_outside_owner_tree",
                    $"Scope '{grant.TargetScopeRef}' is not a descendant of '{ownerScopeRef}'.");
        }
    }

    private static void ValidateActions(IReadOnlyList<ResourceAccessAction> actions, bool allowEmpty)
    {
        if (actions is null || !allowEmpty && actions.Count == 0
            || actions.Count > Enum.GetValues<ResourceAccessAction>().Length
            || actions.Distinct().Count() != actions.Count || actions.Any(value => !Enum.IsDefined(value)))
            throw Invalid("grant_actions_invalid", "Actions must be unique supported values.");
    }

    private static void ValidateIdentity(ResourceAccessIdentity identity)
    {
        if (identity.Uid == Guid.Empty) throw Invalid("resource_uid_required", "A resource UID is required.");
        if (identity.OwnerScopeRef == default) throw Invalid("resource_scope_required", "A resource owner scope is required.");
        if (string.IsNullOrWhiteSpace(identity.Kind) || identity.Kind.Length > 256)
            throw Invalid("resource_kind_invalid", "A valid resource kind is required.");
        if (identity.Generation is < 0)
            throw Invalid("resource_generation_invalid", "A resource generation cannot be negative.");
        if (identity.Version?.Length > 128)
            throw Invalid("resource_version_invalid", "A resource version cannot exceed 128 characters.");
    }

    private static bool IsValidIdentity(ResourceAccessIdentity identity) =>
        identity.Uid != Guid.Empty
        && identity.OwnerScopeRef != default
        && !string.IsNullOrWhiteSpace(identity.Kind)
        && identity.Kind.Length <= 256
        && identity.Generation is not < 0
        && identity.Version?.Length is not > 128;

    private static ResourceAccessIdentity CanonicalIdentity(ResourceAccessIdentity requested,
        ResourceIdentitySnapshot current) => new(current.Uid, current.ScopeRef, current.Namespace, current.Kind,
            requested.Generation, requested.Version);

    private static bool Matches(ResourceAccessIdentity requested, ResourceIdentitySnapshot current) =>
        requested.Uid == current.Uid
        && requested.OwnerScopeRef == current.ScopeRef
        && requested.Namespace == current.Namespace
        && string.Equals(requested.Kind, current.Kind, StringComparison.Ordinal)
        && (requested.Generation is null || requested.Generation == current.Generation)
        && (requested.Version is null || string.Equals(requested.Version, current.Version, StringComparison.Ordinal));

    private async Task<ResourceAccessDecision> DecisionAsync(bool allowed, string reason,
        ResourceScopeRef ownerScopeRef, CancellationToken cancellationToken)
    {
        await AuditAsync(SecurityAuditActions.ResourceAccessEvaluated, ownerScopeRef, reason, cancellationToken,
            allowed ? SecurityAuditOutcome.Succeeded : SecurityAuditOutcome.Failed);
        return new(allowed, reason);
    }

    private Task AuditAsync(string action, ResourceScopeRef scopeRef, string? reason,
        CancellationToken cancellationToken, SecurityAuditOutcome outcome = SecurityAuditOutcome.Succeeded) =>
        audit.WriteAsync(new(action, outcome,
            TenantId: scopeRef.Kind == ResourceScopeKind.Tenant ? scopeRef.TargetId : null,
            WorkspaceId: scopeRef.Kind == ResourceScopeKind.Workspace ? scopeRef.TargetId : null,
            ReasonCode: reason), cancellationToken);

    private static ResourceAccessPolicyResponse ToResponse(StoredResourceAccessPolicy stored) =>
        new(stored.Policy, stored.ETag, stored.UpdatedAt);

    private static ResourceAccessPolicyException Invalid(string code, string message) => new(code, message);
}
