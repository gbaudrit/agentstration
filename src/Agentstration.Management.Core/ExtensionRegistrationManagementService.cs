using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class ExtensionRegistrationValidationException(string message) : Exception(message);
public sealed class ExtensionRegistrationNotFoundException(ResourceAddress address) : Exception($"Extension registration '{address}' was not found.");
public sealed record ExtensionRegistrationUsage(string Kind, string Name, string DisplayName, string ContributionId);
public sealed class ExtensionRegistrationInUseException(string name, IReadOnlyList<ExtensionRegistrationUsage> usages)
    : Exception($"The extension registration '{name}' is used by {usages.Count} provider resource(s).")
{
    public IReadOnlyList<ExtensionRegistrationUsage> Usages { get; } = usages;
}

public sealed class ExtensionRegistrationManagementService(
    IControlPlaneStore store,
    IResourceReferenceResolver references,
    ResourceScopeOperationService scopeOperations,
    Agentstration.Aep.Client.AepTransportSecurityOptions? transportOptions = null)
{
    public Task<StoredResource<ExtensionRegistrationResource>?> GetAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        store.GetAsync<ExtensionRegistrationResource>(
            new(ResourceKinds.ExtensionRegistration, name, @namespace),
            cancellationToken);

    public Task<StoredResource<ExtensionRegistrationResource>?> GetExactAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        store.GetExactAsync<ExtensionRegistrationResource>(
            ScopedResourceAddress.Create(scopeRef, @namespace, ResourceKinds.ExtensionRegistration, name),
            cancellationToken);

    public Task<IReadOnlyList<StoredResource<ExtensionRegistrationResource>>> ListAsync(
        CancellationToken cancellationToken) =>
        store.ListAllAsync<ExtensionRegistrationResource>(ResourceKinds.ExtensionRegistration, cancellationToken);

    public async Task<StoredResource<ExtensionRegistrationResource>> CreateAsync(
        ExtensionRegistrationResource resource,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(resource);
        var scopeRef = resource.ScopeRef ?? DefaultScopeRef(resource.Definition.Source);
        return await scopeOperations.WriteAsync(resource, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var definition = await ValidateDefinitionAsync(resource.Namespace, resource.Metadata.Name, resource.Definition, scopeRef, token);
            if (await GetExactAsync(scopeRef, resource.Namespace, resource.Name, token) is not null)
                throw new ControlPlaneConcurrencyException($"Extension registration '{resource.Address}' already exists in scope '{scopeRef}'.");
            return await store.PutExactAsync(scopeRef, resource with
            {
                ScopeRef = scopeRef,
                Generation = 1,
                Definition = definition,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            },
            null,
            true,
            token);
        }, cancellationToken);
    }

    public async Task<StoredResource<ExtensionRegistrationResource>> PutAsync(
        ResourceNamespace @namespace,
        string name,
        ExtensionRegistrationProperties definition,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new ExtensionRegistrationNotFoundException(new(@namespace, ResourceKinds.ExtensionRegistration, name));
        if (existing.Value.Definition.Source != ExtensionRegistrationSource.Manual)
            throw new ExtensionRegistrationValidationException("Configuration and Aspire extension registrations are read-only.");
        var scopeRef = existing.Value.ScopeRef ?? throw new ExtensionRegistrationValidationException("The extension registration has no ownership scope.");
        return await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var validated = await ValidateDefinitionAsync(@namespace, name, definition, scopeRef, token);
            return await store.PutExactAsync(scopeRef, existing.Value with
            {
                Generation = checked(existing.Value.Generation + 1),
                Definition = validated,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            },
            ifMatch,
            false,
            token);
        }, cancellationToken);
    }

    public async Task DeleteAsync(
        ResourceNamespace @namespace,
        string name,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new ExtensionRegistrationNotFoundException(new(@namespace, ResourceKinds.ExtensionRegistration, name));
        if (existing.Value.Definition.Source != ExtensionRegistrationSource.Manual)
            throw new ExtensionRegistrationValidationException("Configuration and Aspire extension registrations are read-only.");
        var scopeRef = existing.Value.ScopeRef ?? throw new ExtensionRegistrationValidationException("The extension registration has no ownership scope.");
        var usages = await GetUsagesAsync(scopeRef, @namespace, name, cancellationToken);
        if (usages.Count > 0) throw new ExtensionRegistrationInUseException(name, usages);
        await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            await store.DeleteExactAsync(
                ScopedResourceAddress.Create(scopeRef, @namespace, ResourceKinds.ExtensionRegistration, name),
                ifMatch,
                token);
            return true;
        }, cancellationToken);
    }

    public async Task<StoredResource<ExtensionRegistrationResource>> SynchronizeAsync(
        string name,
        ExtensionRegistrationProperties definition,
        CancellationToken cancellationToken)
    {
        if (definition.Source == ExtensionRegistrationSource.Manual)
            throw new ExtensionRegistrationValidationException("Discovered registrations must identify their configuration source.");
        var @namespace = ResourceNamespace.Default;
        var scopeRef = ResourceScopeRef.Instance;
        var validated = await ValidateDefinitionAsync(@namespace, name, definition, scopeRef, cancellationToken);
        var existing = await GetExactAsync(scopeRef, @namespace, name, cancellationToken);
        if (existing is null)
        {
            return await CreateAsync(new ExtensionRegistrationResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.ExtensionRegistration,
                Metadata = new ResourceMetadata { Name = name },
                ScopeRef = scopeRef,
                Definition = validated
            }, cancellationToken);
        }
        if (existing.Value.Definition == validated) return existing;
        return await store.PutExactAsync(scopeRef, existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            Definition = validated,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, existing.ETag, false, cancellationToken);
    }

    public async Task<IReadOnlyList<ExtensionRegistrationUsage>> GetUsagesAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken)
    {
        var registration = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new ExtensionRegistrationNotFoundException(new(@namespace, ResourceKinds.ExtensionRegistration, name));
        return await GetUsagesAsync(
            registration.Value.ScopeRef ?? throw new ExtensionRegistrationValidationException("The extension registration has no ownership scope."),
            @namespace,
            name,
            cancellationToken);
    }

    private async Task<IReadOnlyList<ExtensionRegistrationUsage>> GetUsagesAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        [
            .. (await store.ListAllAsync<ModelProviderResource>(ResourceKinds.ModelProvider, cancellationToken))
            .Where(value =>
            {
                var address = value.Value.Definition.Extension.Resolve(value.Value.Namespace, ResourceKinds.ExtensionRegistration);
                return address.Namespace == @namespace && string.Equals(address.Name, name, StringComparison.Ordinal);
            })
            .Select(value => new ExtensionRegistrationUsage(
                value.Value.Kind,
                value.Value.Name,
                value.Value.Definition.DisplayName,
                value.Value.Definition.ContributionId)),
            .. (await store.ListAllAsync<SourceProviderResource>(ResourceKinds.SourceProvider, cancellationToken))
            .Where(value =>
            {
                var address = value.Value.Definition.Extension.Resolve(value.Value.Namespace, ResourceKinds.ExtensionRegistration);
                return address.Namespace == @namespace
                    && string.Equals(address.Name, name, StringComparison.Ordinal)
                    && (value.Value.Definition.Extension.ScopeRef is null
                        || value.Value.Definition.Extension.ScopeRef == scopeRef);
            })
            .Select(value => new ExtensionRegistrationUsage(
                value.Value.Kind,
                value.Value.Name,
                value.Value.Definition.DisplayName,
                value.Value.Definition.ContributionId))
        ];

    private async Task<ExtensionRegistrationProperties> ValidateDefinitionAsync(
        ResourceNamespace @namespace,
        string name,
        ExtensionRegistrationProperties definition,
        ResourceScopeRef ownerScopeRef,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.DisplayName))
            throw new ExtensionRegistrationValidationException("A display name is required.");
        if (definition.Endpoint is null || !definition.Endpoint.IsAbsoluteUri || definition.Endpoint.Scheme is not ("http" or "https"))
            throw new ExtensionRegistrationValidationException("Extension endpoint must be an absolute HTTP(S) URL.");
        if (!string.IsNullOrEmpty(definition.Endpoint.UserInfo)
            || !string.IsNullOrEmpty(definition.Endpoint.Query)
            || !string.IsNullOrEmpty(definition.Endpoint.Fragment))
            throw new ExtensionRegistrationValidationException("Extension endpoint cannot contain credentials, a query string, or a fragment.");
        // Hosts embedding Management.Core without the HTTP client stack may omit a
        // transport policy. The web host always registers one and therefore enforces
        // the production trust boundary at registration time.
        try
        {
            if (transportOptions is not null)
                Agentstration.Aep.Client.AepTransportSecurity.ValidateEndpoint(definition.Endpoint, transportOptions);
        }
        catch (Agentstration.Aep.Client.AepTransportSecurityException exception)
        {
            throw new ExtensionRegistrationValidationException(exception.Message);
        }
        var endpoint = Normalize(definition.Endpoint);
        if (definition.AuthenticationMode == AepTransportAuthenticationMode.None && definition.Credential is not null)
            throw new ExtensionRegistrationValidationException("An AEP credential requires the staticBearer authentication mode.");
        if (definition.AuthenticationMode == AepTransportAuthenticationMode.StaticBearer && definition.Credential is null)
            throw new ExtensionRegistrationValidationException("The staticBearer authentication mode requires a Secret credential.");
        await ValidateCredentialAsync(@namespace, definition.Credential, ownerScopeRef, cancellationToken);
        var duplicate = (await store.ListVisibleAsync<ExtensionRegistrationResource>(
                ownerScopeRef, ResourceKinds.ExtensionRegistration, 0, 200, cancellationToken)).FirstOrDefault(value =>
            value.Value.Namespace == @namespace
            && !string.Equals(value.Value.Name, name, StringComparison.Ordinal)
            && Uri.Compare(
                value.Value.Definition.Endpoint,
                endpoint,
                UriComponents.HttpRequestUrl,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) == 0);
        if (duplicate is not null)
            throw new ExtensionRegistrationValidationException(
                $"Endpoint '{endpoint}' is already registered as '{duplicate.Value.Address}'.");
        return definition with
        {
            DisplayName = definition.DisplayName.Trim(),
            Endpoint = endpoint,
            ExpectedExtensionId = string.IsNullOrWhiteSpace(definition.ExpectedExtensionId) ? null : definition.ExpectedExtensionId.Trim()
        };
    }

    private async Task ValidateCredentialAsync(
        ResourceNamespace ownerNamespace,
        ResourceReference? credential,
        ResourceScopeRef ownerScopeRef,
        CancellationToken cancellationToken)
    {
        if (credential is null) return;
        var address = credential.Resolve(ownerNamespace, ResourceKinds.Secret);
        if (await references.ResolveAsync<SecretResource>(
                credential, ownerNamespace, ResourceKinds.Secret, ownerScopeRef, cancellationToken) is null)
            throw new ExtensionRegistrationValidationException($"Referenced secret '{address}' does not exist or is not visible from '{ownerScopeRef}'.");
    }

    private ResourceScopeRef DefaultScopeRef(ExtensionRegistrationSource source) =>
        source == ExtensionRegistrationSource.Manual
            ? scopeOperations.DefaultScopeRef(ResourceKinds.ExtensionRegistration)
            : ResourceScopeRef.Instance;

    private static void ValidateIdentity(ExtensionRegistrationResource resource)
    {
        if (resource.Kind != ResourceKinds.ExtensionRegistration)
            throw new ExtensionRegistrationValidationException($"Kind must be '{ResourceKinds.ExtensionRegistration}'.");
        if (resource.ApiVersion != ManagementApiVersions.CoreV1)
            throw new ExtensionRegistrationValidationException($"ApiVersion must be '{ManagementApiVersions.CoreV1}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name);
    }

    private static Uri Normalize(Uri endpoint) =>
        new(endpoint.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
}
