using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.ResourceManagement;

/// <summary>One action-aware rule order shared by storage discovery and application use.</summary>
public static class ResourceAccessRules
{
    public static ResourceAccessDecision Evaluate(ResourceAccessPolicy? policy,
        ResourceScopeRef consumer, IReadOnlyList<ResourceScopeRef> ancestors, ResourceAccessAction action)
    {
        if (policy is null) return new(false, ResourceAccessDecisionReasons.PolicyMissing);
        // Ancestors must be ordered nearest first. A narrower rule overrides propagation.
        foreach (var target in new[] { consumer }.Concat(ancestors))
        {
            var grant = policy.Grants.SingleOrDefault(value => value.TargetScopeRef == target
                && (target == consumer || value.IncludeDescendants));
            if (grant is null) continue;
            if (grant.DeniedActions.Contains(action)) return new(false, ResourceAccessDecisionReasons.ExplicitDeny);
            if (grant.Actions.Contains(action)) return new(true, target == consumer
                ? ResourceAccessDecisionReasons.ExactGrant : ResourceAccessDecisionReasons.PropagatedGrant);
        }
        return policy.AllDescendantActions.Contains(action)
            ? new(true, ResourceAccessDecisionReasons.AllDescendants)
            : new(false, ResourceAccessDecisionReasons.GrantMissing);
    }
}
