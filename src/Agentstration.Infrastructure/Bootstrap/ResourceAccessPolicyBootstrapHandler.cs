using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Extensions.Aep;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Hosting;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed class ResourceAccessPolicyBootstrapHandler(
    IResourceIdentityCatalog identities,
    IResourceAccessPolicyService policies,
    IResourceScopeResolver scopeResolver,
    ExtensionEnrollmentPolicyService targets,
    IRequestContextScopeFactory scopes,
    IResourceGovernanceContext governance) : IBootstrapResourceHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string Kind => "ResourceAccessPolicy";
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope profileScope) => true;

    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken cancellationToken)
    {
        RequireAdministrativeInput();
        var definition = Read(resource);
        var owner = BootstrapExtensionReferencePreparation.ConsumerScope(operation);
        var identity = await FindAsync(definition.Resource, owner, cancellationToken);
        if (identity is not null && await policies.GetAsync(identity.Uid, cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        var ns = definition.Resource.Namespace ?? ResourceNamespace.Default;
        if (identity is null && !WorkspaceBootstrapResource.IsAvailable(planning,
                definition.Resource.Kind, definition.Resource.Name, ns))
            throw new DeclarativeBootstrapException($"Resource '{owner}/{ns}/{definition.Resource.Kind}/{definition.Resource.Name}' must exist or be planned before its access policy.");
        _ = await ResolveGrantsAsync(definition, owner, cancellationToken);
        var selector = $"{owner}/{ns}/{definition.Resource.Kind}/{definition.Resource.Name}";
        if (planning.Contains(Kind, selector))
            throw new DeclarativeBootstrapException($"Several access policies select '{selector}'.");
        planning.Register(Kind, selector);
        return new(BootstrapResourceDisposition.Create);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation, CancellationToken cancellationToken)
    {
        RequireAdministrativeInput();
        var definition = Read(resource);
        var owner = BootstrapExtensionReferencePreparation.ConsumerScope(operation);
        var identity = await FindAsync(definition.Resource, owner, cancellationToken)
            ?? throw new DeclarativeBootstrapException($"Resource '{definition.Resource.Kind}/{definition.Resource.Name}' does not exist in '{owner}'.");
        if (await policies.GetAsync(identity.Uid, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;
        var grants = await ResolveGrantsAsync(definition, owner, cancellationToken);
        try
        {
            _ = await policies.PutAsync(new(new(identity.Uid, owner, identity.Namespace, identity.Kind,
                identity.Generation, identity.Version), grants), null, cancellationToken);
        }
        catch (ResourceConcurrencyException)
        {
            if (await policies.GetAsync(identity.Uid, cancellationToken) is null) throw;
            return BootstrapResourceApplyResult.Skipped;
        }
        return BootstrapResourceApplyResult.Created;
    }

    private async Task<ResourceIdentitySnapshot?> FindAsync(PolicyResourceSelector selector,
        ResourceScopeRef owner, CancellationToken cancellationToken)
    {
        var inventory = await identities.ListExactInventoryAsync(owner, cancellationToken);
        var entry = inventory.SingleOrDefault(value => value.Kind == selector.Kind && value.Name == selector.Name
            && value.Namespace == (selector.Namespace ?? ResourceNamespace.Default));
        return entry is null ? null : await identities.GetIdentityByUidAsync(entry.Uid, cancellationToken);
    }

    private async Task<IReadOnlyList<ResourceAccessGrant>> ResolveGrantsAsync(PolicyDefinition definition,
        ResourceScopeRef owner, CancellationToken cancellationToken)
    {
        var result = new List<ResourceAccessGrant>();
        foreach (var grant in definition.Grants)
        {
            ResourceScopeRef target;
            // Name/ID resolution is administrative and read-only; policy writes
            // below still use the original owner context and existing authorization.
            using (scopes.PushSystem())
                target = (await targets.ResolveLevelAsync(grant.TargetLevel, "bootstrap-access-policy", cancellationToken)).ScopeRef;
            var resolved = await scopeResolver.ResolveAsync(target, cancellationToken)
                ?? throw new DeclarativeBootstrapException($"Access policy target '{grant.TargetLevel}' does not exist.");
            if (!resolved.Ancestors.Any(value => value.Ref == owner))
                throw new DeclarativeBootstrapException($"Access policy target '{grant.TargetLevel}' must be a descendant of '{owner}'.");
            if (result.Any(value => value.TargetScopeRef == target))
                throw new DeclarativeBootstrapException($"Duplicate access policy target '{grant.TargetLevel}'.");
            result.Add(new(target, grant.Actions, grant.IncludeDescendants));
        }
        return result;
    }

    private void RequireAdministrativeInput()
    {
        if (governance.Current is { } authority
            && (authority.IsManaged || authority.Origin.Kind != ResourceOriginKind.User))
            throw new DeclarativeBootstrapException("Access policies require local administrative Bootstrap input; managed bundles cannot declare them.");
    }

    private static PolicyDefinition Read(BootstrapResourceDocument resource)
    {
        var definition = resource.Definition.Deserialize<PolicyDefinition>(JsonOptions)
            ?? throw new DeclarativeBootstrapException("An access policy definition is required.");
        if (definition.Resource is null || string.IsNullOrWhiteSpace(definition.Resource.Kind) || string.IsNullOrWhiteSpace(definition.Resource.Name))
            throw new DeclarativeBootstrapException("An access policy requires a resource kind and name.");
        if (definition.Grants is null || definition.Grants.Count > 256 || definition.Grants.Any(grant =>
                grant is null || string.IsNullOrWhiteSpace(grant.TargetLevel) || grant.Actions is null || grant.Actions.Count == 0
                || grant.Actions.Any(action => !Enum.IsDefined(action))
                || grant.Actions.Distinct().Count() != grant.Actions.Count))
            throw new DeclarativeBootstrapException("Access policies require bounded grants with unique discover/use actions and explicit targets.");
        return definition;
    }

    private sealed record PolicyDefinition(PolicyResourceSelector Resource, IReadOnlyList<PolicyGrant> Grants);
    private sealed record PolicyResourceSelector(string Kind, string Name, ResourceNamespace? Namespace = null);
    private sealed record PolicyGrant(string TargetLevel, IReadOnlyList<ResourceAccessAction> Actions, bool IncludeDescendants = false);
}
