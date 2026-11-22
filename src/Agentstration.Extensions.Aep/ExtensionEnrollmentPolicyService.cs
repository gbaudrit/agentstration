using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Extensions.Aep;

public sealed record AutomaticEnrollmentTarget(ResourceScopeRef ScopeRef, Guid? TenantId, string PolicyName);

public sealed class ExtensionEnrollmentPolicyService(
    IResourceStore store,
    IIdentityStore identities,
    ICurrentRequestContext context,
    AepEnrollmentPolicyOptions options)
{
    public Task<StoredResource<ExtensionEnrollmentPolicyResource>?> GetAsync(string name, CancellationToken cancellationToken) =>
        store.GetExactAsync<ExtensionEnrollmentPolicyResource>(
            ScopedResourceAddress.Create(ResourceScopeRef.Instance, ResourceNamespace.Default,
                ExtensionKinds.ExtensionEnrollmentPolicy, name), cancellationToken);

    public Task<IReadOnlyList<StoredResource<ExtensionEnrollmentPolicyResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListExactAsync<ExtensionEnrollmentPolicyResource>(ResourceScopeRef.Instance,
            ExtensionKinds.ExtensionEnrollmentPolicy, 0, 1000, cancellationToken);

    public static void Validate(ExtensionEnrollmentPolicyProperties definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ExtensionId) || definition.ExtensionId.Length > 256
            || definition.InstanceId == Guid.Empty)
            throw new AepEnrollmentException("enrollment_policy_invalid", "The enrollment policy requires a bounded extension ID and a non-empty installation ID when supplied.");
        ValidateLevel(definition.AutoEnrollmentLevel);
    }

    public static void ValidateLevel(string? level)
    {
        if (level is null) return;
        var parts = level.Split('/');
        if (level == "/instance") return;
        if (level.Length <= 1024 && parts.Length is 3 or 5 && parts[0].Length == 0
            && parts[1] == "tenants" && ValidSegment(parts[2])
            && (parts.Length == 3 || (parts[3] == "workspaces" && ValidSegment(parts[4])))) return;
        throw new AepEnrollmentException("enrollment_level_invalid", "An enrollment level must be /instance, /tenants/<name-or-id>, or /tenants/<name-or-id>/workspaces/<name-or-id>.");
    }

    public async Task CreateAsync(ExtensionEnrollmentPolicyResource resource, CancellationToken cancellationToken)
    {
        RequireSystem();
        Validate(resource.Definition);
        var policies = await ListAsync(cancellationToken);
        if (policies.Count >= 1000)
            throw new AepEnrollmentException("enrollment_policy_limit", "The instance enrollment policy limit has been reached.");
        if (policies.Any(value => value.Value.Name != resource.Name
            && SameSelector(value.Value.Definition, resource.Definition)))
            throw new AepEnrollmentException("enrollment_policy_conflict", "Only one enrollment policy may select an extension installation at the same specificity.", 409);
        _ = await store.PutExactAsync(ResourceScopeRef.Instance, resource with
        {
            ScopeRef = ResourceScopeRef.Instance,
            Generation = 1,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, null, true, cancellationToken);
    }

    public async Task ValidateTargetsAsync(CancellationToken cancellationToken)
    {
        RequireSystem();
        if (options.AutoEnrollmentLevel is { } level)
            _ = await ResolveLevelAsync(level, "configuration", cancellationToken);
        var policies = await ListAsync(cancellationToken);
        var selectors = new HashSet<(string, Guid?)>();
        foreach (var policy in policies)
        {
            Validate(policy.Value.Definition);
            if (!selectors.Add((policy.Value.Definition.ExtensionId, policy.Value.Definition.InstanceId)))
                throw new AepEnrollmentException("enrollment_policy_conflict", "Several enrollment policies select the same extension installation.", 409);
            if (policy.Value.Definition.AutoEnrollmentLevel is { } target)
                _ = await ResolveLevelAsync(target, policy.Value.Name, cancellationToken);
        }
    }

    public async Task<AutomaticEnrollmentTarget?> SelectAsync(
        string extensionId, Guid instanceId, CancellationToken cancellationToken)
    {
        RequireSystem();
        var matching = (await ListAsync(cancellationToken)).Where(value =>
            value.Value.Definition.ExtensionId == extensionId
            && (value.Value.Definition.InstanceId is null || value.Value.Definition.InstanceId == instanceId))
            .OrderByDescending(value => value.Value.Definition.InstanceId.HasValue).ToArray();
        var selected = matching.FirstOrDefault();
        var level = selected is null ? options.AutoEnrollmentLevel : selected.Value.Definition.AutoEnrollmentLevel;
        return level is null ? null : await ResolveLevelAsync(level, selected?.Value.Name ?? "configuration", cancellationToken);
    }

    public async Task<AutomaticEnrollmentTarget> ResolveLevelAsync(
        string level, string policyName, CancellationToken cancellationToken)
    {
        RequireSystem();
        ValidateLevel(level);
        if (level == "/instance") return new(ResourceScopeRef.Instance, null, policyName);
        var parts = level.Split('/');
        var tenant = Guid.TryParseExact(parts[2], "D", out var tenantId)
            ? await identities.GetTenantAsync(tenantId, cancellationToken)
            : await identities.FindTenantByNameAsync(parts[2], cancellationToken);
        if (tenant is null || tenant.Status != TenantStatus.Active)
            throw new AepEnrollmentException("enrollment_target_missing", $"The enrollment policy '{policyName}' requires an active Tenant at '{level}'.");
        if (parts.Length == 3) return new(ResourceScopeRef.Tenant(tenant.Id), tenant.Id, policyName);
        var workspace = Guid.TryParseExact(parts[4], "D", out var workspaceId)
            ? await identities.GetWorkspaceAsync(tenant.Id, workspaceId, cancellationToken)
            : await identities.FindWorkspaceByNameAsync(tenant.Id, parts[4], cancellationToken);
        if (workspace is null || workspace.Status != WorkspaceStatus.Active)
            throw new AepEnrollmentException("enrollment_target_missing", $"The enrollment policy '{policyName}' requires an active Workspace in its Tenant at '{level}'.");
        return new(ResourceScopeRef.Workspace(workspace.Id), tenant.Id, policyName);
    }

    public static bool SameSelector(ExtensionEnrollmentPolicyProperties left, ExtensionEnrollmentPolicyProperties right) =>
        left.ExtensionId == right.ExtensionId && left.InstanceId == right.InstanceId;

    private static bool ValidSegment(string value) => value.Length is > 0 and <= 256
        && value is not "." and not ".." && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private void RequireSystem()
    {
        if (context.AccessMode != ControlPlaneAccessMode.System)
            throw new ResourceScopeAccessDeniedException(ResourceScopeRef.Instance);
    }
}
