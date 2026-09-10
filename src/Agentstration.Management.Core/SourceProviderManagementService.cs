using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class SourceProviderValidationException(string message) : Exception(message);
public sealed class SourceProviderNotFoundException(ResourceAddress address) : Exception($"Source provider '{address}' was not found.");
public sealed record SourceProviderUsage(ResourceScopeRef SourceScopeRef, string Publisher, string SourceName, string BindingName);
public sealed class SourceProviderInUseException(string providerName, IReadOnlyList<SourceProviderUsage> usages)
    : Exception($"Source provider '{providerName}' is referenced by {usages.Count} Source binding(s).")
{
    public IReadOnlyList<SourceProviderUsage> Usages { get; } = usages;
}
public sealed record SourceProviderStatus(string Provider, string Status, DateTimeOffset CheckedAt, string? Details);

public sealed class SourceProviderManagementService(
    IControlPlaneStore store,
    IResourceReferenceResolver references,
    ResourceScopeOperationService scopeOperations,
    IEnumerable<IExtensionInspector> inspectors,
    TimeProvider timeProvider)
{
    private const string Available = "available";
    private const string SourceProviderContribution = "source-provider";

    public Task<StoredResource<SourceProviderResource>?> GetAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        store.GetExactAsync<SourceProviderResource>(
            ScopedResourceAddress.Create(ResourceScopeRef.Instance, @namespace, ResourceKinds.SourceProvider, name),
            cancellationToken);

    public Task<IReadOnlyList<StoredResource<SourceProviderResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListExactAsync<SourceProviderResource>(ResourceScopeRef.Instance, ResourceKinds.SourceProvider, 0, int.MaxValue, cancellationToken);

    public async Task<StoredResource<SourceProviderResource>> CreateAsync(
        SourceProviderResource resource,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        var scopeRef = resource.ScopeRef ?? ResourceScopeRef.Instance;
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        return await scopeOperations.WriteAsync(resource, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            if (await GetAsync(resource.Namespace, resource.Name, token) is not null)
                throw new ControlPlaneConcurrencyException($"Source provider '{resource.Address}' already exists.");
            var definition = await ValidateDefinitionAsync(resource.Namespace, resource.Definition, scopeRef, token);
            return await store.PutExactAsync(scopeRef, resource with
            {
                ScopeRef = scopeRef,
                Generation = 1,
                Definition = definition,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            }, null, true, token);
        }, cancellationToken);
    }

    public async Task<StoredResource<SourceProviderResource>> PutAsync(
        ResourceNamespace @namespace,
        string name,
        SourceProviderProperties definition,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new SourceProviderNotFoundException(new(@namespace, ResourceKinds.SourceProvider, name));
        var scopeRef = existing.Value.ScopeRef
            ?? throw new SourceProviderValidationException("The Source Provider has no ownership scope.");
        return await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var validated = await ValidateDefinitionAsync(@namespace, definition, scopeRef, token);
            return await store.PutExactAsync(scopeRef, existing.Value with
            {
                Generation = checked(existing.Value.Generation + 1),
                Definition = validated,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            }, ifMatch, false, token);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SourceProviderUsage>> GetUsagesAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken)
    {
        var usages = new List<SourceProviderUsage>();
        foreach (var configuration in await store.ListAllAsync<SourceConfigurationResource>(ResourceKinds.SourceConfiguration, cancellationToken))
        {
            foreach (var binding in configuration.Value.Definition.Bindings.Where(value =>
                         string.Equals(value.TargetKind, SourceKinds.SourceProvider, StringComparison.Ordinal)
                         && value.Target is not null))
            {
                var address = binding.Target!.Resolve(configuration.Value.Namespace, ResourceKinds.SourceProvider);
                if (address.Namespace != @namespace || !string.Equals(address.Name, name, StringComparison.Ordinal)) continue;
                usages.Add(new(
                    configuration.Value.ScopeRef
                        ?? throw new SourceProviderValidationException("A referencing Source configuration has no ownership scope."),
                    configuration.Value.Namespace.Value,
                    configuration.Value.Name,
                    binding.Name));
            }
        }
        return usages;
    }

    public async Task<SourceProviderStatus> GetStatusAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken)
    {
        var provider = (await GetAsync(@namespace, name, cancellationToken))?.Value
            ?? throw new SourceProviderNotFoundException(new(@namespace, ResourceKinds.SourceProvider, name));
        var checkedAt = timeProvider.GetUtcNow();
        var scopeRef = provider.ScopeRef
            ?? throw new SourceProviderValidationException("The Source Provider has no ownership scope.");
        var extension = await references.ResolveAsync<ExtensionRegistrationResource>(
            provider.Definition.Extension,
            provider.Namespace,
            ResourceKinds.ExtensionRegistration,
            scopeRef,
            cancellationToken);
        if (extension is null) return new(name, "unavailable", checkedAt, "The referenced extension registration was not found.");
        if (!extension.Value.Definition.Enabled) return new(name, "disabled", checkedAt, "The referenced extension registration is disabled.");

        var inspector = inspectors.SingleOrDefault(value => value.CanInspectEndpoint(extension.Value.Definition.Endpoint));
        if (inspector is null) return new(name, "unknown", checkedAt, "No inspector can validate the extension endpoint.");

        ExtensionInspection inspection;
        try
        {
            inspection = await inspector.InspectAsync(extension.Value.Name, extension.Value.Definition.Endpoint, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(name, "unavailable", checkedAt, exception.Message);
        }

        if (!string.Equals(inspection.Status, Available, StringComparison.OrdinalIgnoreCase))
            return new(name, inspection.Status, checkedAt, inspection.Details);
        if (extension.Value.Definition.ExpectedExtensionId is { Length: > 0 } expectedId
            && !string.Equals(inspection.Extension?.Id, expectedId, StringComparison.Ordinal))
            return new(name, "incompatible", checkedAt, $"Expected extension '{expectedId}', but the endpoint reports '{inspection.Extension?.Id ?? "no identity"}'.");
        if (!inspection.Contributions.Any(value =>
                string.Equals(value.Kind, SourceProviderContribution, StringComparison.Ordinal)
                && string.Equals(value.Id, provider.Definition.ContributionId, StringComparison.OrdinalIgnoreCase)))
            return new(name, "incompatible", checkedAt, $"The extension does not advertise Source Provider contribution '{provider.Definition.ContributionId}'.");
        return new(name, Available, checkedAt, inspection.Details);
    }

    public async Task DeleteAsync(
        ResourceNamespace @namespace,
        string name,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new SourceProviderNotFoundException(new(@namespace, ResourceKinds.SourceProvider, name));
        var usages = await GetUsagesAsync(@namespace, name, cancellationToken);
        if (usages.Count > 0) throw new SourceProviderInUseException(name, usages);
        var scopeRef = existing.Value.ScopeRef
            ?? throw new SourceProviderValidationException("The Source Provider has no ownership scope.");
        await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            await store.DeleteExactAsync(
                ScopedResourceAddress.Create(scopeRef, @namespace, ResourceKinds.SourceProvider, name),
                ifMatch,
                token);
            return true;
        }, cancellationToken);
    }

    private async Task<SourceProviderProperties> ValidateDefinitionAsync(
        ResourceNamespace ownerNamespace,
        SourceProviderProperties definition,
        ResourceScopeRef ownerScopeRef,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.DisplayName))
            throw new SourceProviderValidationException("A display name is required.");
        if (string.IsNullOrWhiteSpace(definition.ContributionId))
            throw new SourceProviderValidationException("An AEP source-provider contribution id is required.");
        var extensionAddress = definition.Extension.Resolve(ownerNamespace, ResourceKinds.ExtensionRegistration);
        if (await references.ResolveAsync<ExtensionRegistrationResource>(
                definition.Extension,
                ownerNamespace,
                ResourceKinds.ExtensionRegistration,
                ownerScopeRef,
                cancellationToken) is null)
            throw new SourceProviderValidationException($"Referenced extension registration '{extensionAddress}' does not exist.");
        return definition with
        {
            DisplayName = definition.DisplayName.Trim(),
            ContributionId = definition.ContributionId.Trim()
        };
    }

    private static void ValidateIdentity(SourceProviderResource resource)
    {
        if (resource.Kind != ResourceKinds.SourceProvider)
            throw new SourceProviderValidationException($"Kind must be '{ResourceKinds.SourceProvider}'.");
        if (resource.ApiVersion != ManagementApiVersions.CoreV1)
            throw new SourceProviderValidationException($"ApiVersion must be '{ManagementApiVersions.CoreV1}'.");
        if (string.IsNullOrWhiteSpace(resource.Name))
            throw new SourceProviderValidationException("A resource name is required.");
    }
}
