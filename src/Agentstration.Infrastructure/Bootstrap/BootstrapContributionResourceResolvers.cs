using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed class BootstrapToolContributionResolver(IResourceStore store, ToolAccessService? access = null) : IBootstrapContributionResourceResolver
{
    public string Kind => ToolResourceKinds.Tool;
    public async Task<BootstrapResolvedReference> ResolveAsync(
        ExtensionRegistrationResource registration, BootstrapContributionSelector selector, CancellationToken cancellationToken)
    {
        var tools = await store.ListExactAsync<ToolResource>(registration.ScopeRef!.Value, Kind, 0, 1001, cancellationToken);
        var enabledUids = new HashSet<Guid>();
        foreach (var tool in tools)
            if (access is null ? tool.Value.Definition.Enabled : await access.IsEnabledAsync(tool.Value, cancellationToken)) enabledUids.Add(tool.Value.Uid);
        if (tools.Any(value => value.Value.Definition.ExternalId == selector.ContributionId
            && BootstrapExtensionReferenceResolver.IsOwnedBy(value.Value, registration) && !enabledUids.Contains(value.Value.Uid)))
            throw new BootstrapDependencyException("contribution_disabled", $"Required Tool '{selector.ContributionId}' is disabled. Bootstrap preserves its enablement decision.");
        var matches = tools.Where(value => value.Value.Definition.ExternalId == selector.ContributionId
            && enabledUids.Contains(value.Value.Uid) && value.Value.Definition.Discovery?.Available == true
            && value.Value.Definition.Provider?.Name == registration.Name
            && value.Value.Definition.Provider.ScopeRef == registration.ScopeRef
            && BootstrapExtensionReferenceResolver.IsOwnedBy(value.Value, registration)
            && (selector.Version is null || value.Value.Definition.Version == selector.Version)).ToArray();
        if (tools.Count > 1000 || matches.Length > 1)
            throw new BootstrapDependencyException("contribution_ambiguous", $"Tool contribution '{selector.ContributionId}' cannot be resolved uniquely.");
        if (matches.Length == 0) throw BootstrapExtensionReferenceResolver.Missing(selector);
        return BootstrapExtensionReferenceResolver.Resolved(matches[0].Value, matches[0].ETag);
    }
}

public sealed class BootstrapConnectedServiceTypeContributionResolver(IResourceStore store) : IBootstrapContributionResourceResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string Kind => ConnectedServiceResourceKinds.ConnectedServiceType;
    public async Task<BootstrapResolvedReference> ResolveAsync(
        ExtensionRegistrationResource registration, BootstrapContributionSelector selector, CancellationToken cancellationToken)
    {
        var resources = await store.ListExactAsync<ConnectedServiceTypeResource>(registration.ScopeRef!.Value, Kind, 0, 1001, cancellationToken);
        if (resources.Any(value => value.Value.Name == selector.ContributionId
            && BootstrapExtensionReferenceResolver.IsOwnedBy(value.Value, registration) && !value.Value.Definition.Enabled))
            throw new BootstrapDependencyException("contribution_disabled", $"Required Connected Service Type '{selector.ContributionId}' is disabled.");
        var matches = resources.Where(value => value.Value.Name == selector.ContributionId
            && value.Value.Definition.Enabled && BootstrapExtensionReferenceResolver.IsOwnedBy(value.Value, registration)
            && value.Value.Definition.Versions.Any(version => !version.Deprecated
                && version.Version == (selector.Version ?? value.Value.Definition.PreferredVersion))).ToArray();
        if (resources.Count > 1000 || matches.Length > 1)
            throw new BootstrapDependencyException("contribution_ambiguous", $"Connected Service Type contribution '{selector.ContributionId}' cannot be resolved uniquely.");
        if (matches.Length == 0) throw BootstrapExtensionReferenceResolver.Missing(selector);
        var selected = matches[0];
        var version = selected.Value.Definition.Versions.Single(value =>
            value.Version == (selector.Version ?? selected.Value.Definition.PreferredVersion));
        var resolved = BootstrapExtensionReferenceResolver.Resolved(selected.Value, selected.ETag);
        return resolved with
        {
            TargetValue = JsonSerializer.SerializeToElement(new ConnectedServiceTypeReference
            {
                Type = resolved.Reference,
                TypeUid = selected.Value.Uid,
                Version = version.Version,
                SchemaDigest = version.SchemaDigest
            }, JsonOptions)
        };
    }
}

public sealed class BootstrapStorageTypeContributionResolver(IResourceStore store) : IBootstrapContributionResourceResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string Kind => StorageResourceKinds.StorageType;
    public async Task<BootstrapResolvedReference> ResolveAsync(
        ExtensionRegistrationResource registration, BootstrapContributionSelector selector, CancellationToken cancellationToken)
    {
        var resources = await store.ListExactAsync<StorageTypeResource>(registration.ScopeRef!.Value, Kind, 0, 1001, cancellationToken);
        if (resources.Any(value => value.Value.Name == selector.ContributionId
            && BootstrapExtensionReferenceResolver.IsOwnedBy(value.Value, registration) && !value.Value.Definition.Enabled))
            throw new BootstrapDependencyException("contribution_disabled", $"Required Storage Type '{selector.ContributionId}' is disabled.");
        var matches = resources.Where(value => value.Value.Definition.Enabled
            && BootstrapExtensionReferenceResolver.IsOwnedBy(value.Value, registration)
            && value.Value.Definition.Versions.Any(version => !version.Deprecated
                && version.Version == (selector.Version ?? value.Value.Definition.PreferredVersion)
                && version.Contribution?.ContributionId == selector.ContributionId
                && version.Contribution.Provider == ScopedResourceAddress.Create(registration.ScopeRef.Value, registration.Namespace, registration.Kind, registration.Name))).ToArray();
        if (resources.Count > 1000 || matches.Length > 1)
            throw new BootstrapDependencyException("contribution_ambiguous", $"Storage Type contribution '{selector.ContributionId}' cannot be resolved uniquely.");
        if (matches.Length == 0) throw BootstrapExtensionReferenceResolver.Missing(selector);
        var selected = matches[0];
        var version = selected.Value.Definition.Versions.Single(value =>
            value.Version == (selector.Version ?? selected.Value.Definition.PreferredVersion));
        var resolved = BootstrapExtensionReferenceResolver.Resolved(selected.Value, selected.ETag);
        return resolved with
        {
            TargetValue = JsonSerializer.SerializeToElement(new StorageTypeReference
            {
                Type = resolved.Reference,
                TypeUid = selected.Value.Uid,
                Version = version.Version,
                SchemaDigest = version.SchemaDigest,
                ContractDigest = version.ContractDigest
            }, JsonOptions)
        };
    }
}

public sealed class BootstrapDataSourceProfileContributionResolver(IResourceStore store) : IBootstrapContributionResourceResolver
{
    public string Kind => DataSourceResourceKinds.DataSourceProfile;
    public async Task<BootstrapResolvedReference> ResolveAsync(
        ExtensionRegistrationResource registration, BootstrapContributionSelector selector, CancellationToken cancellationToken)
    {
        var installations = await store.ListExactAsync<ExtensionBundleInstallationResource>(registration.ScopeRef!.Value,
            ExtensionKinds.ExtensionBundleInstallation, 0, 1001, cancellationToken);
        var matches = installations.Where(value => value.Value.Definition.State == ExtensionBundleInstallationState.Installed
            && value.Value.Definition.ExtensionId == selector.ExtensionId
            && value.Value.Definition.Registration.Name == registration.Name
            && value.Value.Definition.Registration.ScopeRef == registration.ScopeRef)
            .SelectMany(value => value.Value.Definition.ManagedResources
                .Where(resource => resource.Kind == Kind && resource.Name == selector.ContributionId)
                .Select(resource => (Installation: value.Value, Resource: resource))).ToArray();
        if (installations.Count > 1000 || matches.Length > 1)
            throw new BootstrapDependencyException("contribution_ambiguous", $"Data Source Profile contribution '{selector.ContributionId}' cannot be resolved uniquely.");
        if (matches.Length == 0) throw BootstrapExtensionReferenceResolver.Missing(selector);
        var match = matches[0];
        var resource = await store.GetExactAsync<DataSourceProfileResource>(ScopedResourceAddress.Create(
            registration.ScopeRef.Value, match.Resource.Namespace, Kind, match.Resource.Name), cancellationToken);
        if (resource is null || !resource.Value.Definition.Enabled || resource.Value.ActiveVersion is null
            || resource.Value.Governance.ManagedBy?.Reference != ScopedResourceAddress.Create(registration.ScopeRef.Value, match.Installation.Namespace, match.Installation.Kind, match.Installation.Name).ToString()
            || (selector.Version is not null && resource.Value.ActiveVersion != selector.Version))
            throw BootstrapExtensionReferenceResolver.Missing(selector);
        return BootstrapExtensionReferenceResolver.Resolved(resource.Value, resource.ETag);
    }
}
