using Agentstration.Resources;

namespace Agentstration.ResourceManagement;

/// <summary>A grant permits use at one descendant scope, optionally including its descendants.</summary>
public sealed record DescendantUseGrant(ResourceScopeRef ScopeRef, bool IncludeDescendants = false);

public sealed record DescendantUsePolicy
{
    public IReadOnlyList<DescendantUseGrant> Grants { get; init; } = [];
}

public sealed class InvalidDescendantUseGrantException(string message) : Exception(message);

public sealed class DescendantResourceUseAuthorizer(IResourceScopeResolver scopes)
{
    public async Task<bool> IsSameOrDescendantAsync(ResourceScopeRef ownerScopeRef,
        ResourceScopeRef consumerScopeRef, CancellationToken cancellationToken)
    {
        if (ownerScopeRef == consumerScopeRef) return true;
        var owner = await scopes.ResolveAsync(ownerScopeRef, cancellationToken);
        var consumer = await scopes.ResolveAsync(consumerScopeRef, cancellationToken);
        return owner is not null && consumer?.Ancestors.Any(value => value.Id == owner.Scope.Id) == true;
    }

    public async Task ValidateAsync(ResourceScopeRef ownerScopeRef, DescendantUsePolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Grants is null || policy.Grants.Count > 256)
            throw new InvalidDescendantUseGrantException("A use policy must contain at most 256 grants.");
        var owner = await RequireScopeAsync(ownerScopeRef, cancellationToken);
        var seen = new HashSet<ResourceScopeRef>();
        foreach (var grant in policy.Grants)
        {
            if (grant is null || grant.ScopeRef == default)
                throw new InvalidDescendantUseGrantException("A use grant requires a target scope.");
            if (!seen.Add(grant.ScopeRef))
                throw new InvalidDescendantUseGrantException($"Duplicate use grant for scope '{grant.ScopeRef}'.");
            var target = await RequireScopeAsync(grant.ScopeRef, cancellationToken);
            if (target.Scope.Ref == owner.Scope.Ref || !target.Ancestors.Any(value => value.Id == owner.Scope.Id))
                throw new InvalidDescendantUseGrantException($"Scope '{grant.ScopeRef}' is not a descendant of '{ownerScopeRef}'.");
        }
    }

    public async Task<bool> CanUseAsync(ResourceScopeRef ownerScopeRef, ResourceScopeRef consumerScopeRef,
        DescendantUsePolicy policy, CancellationToken cancellationToken)
    {
        if (ownerScopeRef == consumerScopeRef) return true;
        if (policy?.Grants is null) return false;
        var owner = await scopes.ResolveAsync(ownerScopeRef, cancellationToken);
        var consumer = await scopes.ResolveAsync(consumerScopeRef, cancellationToken);
        if (owner is null || consumer is null) return false;
        var ownerIndex = Array.FindIndex(consumer.Ancestors.ToArray(), value => value.Id == owner.Scope.Id);
        if (ownerIndex < 0) return false;
        var validTargets = new HashSet<ResourceScopeRef>(
            [consumerScopeRef, .. consumer.Ancestors.Take(ownerIndex).Select(value => value.Ref)]);
        return policy.Grants.Any(grant => grant is not null && validTargets.Contains(grant.ScopeRef)
            && (grant.ScopeRef == consumerScopeRef || grant.IncludeDescendants));
    }

    private async Task<ResolvedResourceScope> RequireScopeAsync(ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
        await scopes.ResolveAsync(scopeRef, cancellationToken)
        ?? throw new InvalidDescendantUseGrantException($"Resource scope '{scopeRef}' was not found.");
}
