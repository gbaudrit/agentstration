using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.ConnectedServices;

public sealed class ConnectedServiceManagementService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    DescendantResourceUseAuthorizer useAuthorizer,
    IEnumerable<IConnectedServiceUsageProvider> usageProviders,
    IParameterResolver parameters,
    ISecretResolver secrets,
    TimeProvider timeProvider) : IConnectedServiceResolver
{
    public const int MaximumDisplayNameLength = 200;
    public const int MaximumDescriptionLength = 2_000;
    public const int MaximumOptionsBytes = 256 * 1024;

    public Task<IReadOnlyList<StoredResource<ConnectedServiceResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<ConnectedServiceResource>(ConnectedServiceResourceKinds.ConnectedService, cancellationToken);

    public Task<StoredResource<ConnectedServiceResource>?> GetAsync(string name, CancellationToken cancellationToken) =>
        store.GetAsync<ConnectedServiceResource>(new(ConnectedServiceResourceKinds.ConnectedService, name), cancellationToken);

    public Task<StoredResource<ConnectedServiceResource>?> GetExactAsync(
        ResourceScopeRef scopeRef,
        string name,
        CancellationToken cancellationToken) =>
        store.GetExactAsync<ConnectedServiceResource>(Scoped(scopeRef, name), cancellationToken);

    public async Task<IReadOnlyList<ConnectedServiceUsage>> GetUsagesAsync(
        ResourceScopeRef scopeRef,
        string name,
        CancellationToken cancellationToken)
    {
        _ = await GetExactAsync(scopeRef, name, cancellationToken)
            ?? throw new ConnectedServiceNotFoundException(name);
        var usages = new List<ConnectedServiceUsage>();
        foreach (var provider in usageProviders)
            usages.AddRange(await provider.GetUsagesAsync(Scoped(scopeRef, name), cancellationToken));
        return usages;
    }
    public async Task<StoredResource<ConnectedServiceResource>> CreateAsync(
        ConnectedServiceResource resource,
        CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(ConnectedServiceResourceKinds.ConnectedService);
        var scoped = resource with { ScopeRef = scopeRef };
        return await scopeOperations.WriteAsync(scoped, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var observed = await ValidateAsync(scoped, token);
            return await store.PutExactAsync(scopeRef, scoped with
            {
                Generation = 1,
                ObservedState = observed,
                Status = Succeeded()
            }, null, true, token);
        }, cancellationToken);
    }

    public async Task<StoredResource<ConnectedServiceResource>> PutAsync(
        string name,
        ConnectedServiceProperties definition,
        string? etag,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(name, cancellationToken)
            ?? throw new ConnectedServiceNotFoundException(name);
        return await PutAsync(existing, definition, etag, cancellationToken);
    }

    public async Task<StoredResource<ConnectedServiceResource>> PutExactAsync(
        ResourceScopeRef scopeRef,
        string name,
        ConnectedServiceProperties definition,
        string? etag,
        CancellationToken cancellationToken)
    {
        var existing = await GetExactAsync(scopeRef, name, cancellationToken)
            ?? throw new ConnectedServiceNotFoundException(name);
        return await PutAsync(existing, definition, etag, cancellationToken);
    }

    public async Task<StoredResource<ConnectedServiceResource>> ReconcileAsync(
        ResourceScopeRef scopeRef,
        string name,
        string? etag,
        CancellationToken cancellationToken)
    {
        var existing = await GetExactAsync(scopeRef, name, cancellationToken)
            ?? throw new ConnectedServiceNotFoundException(name);
        return await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var observed = await ObserveAsync(existing.Value, token);
            return await store.PutExactAsync(scopeRef, existing.Value with
            {
                Generation = checked(existing.Value.Generation + 1),
                ObservedState = observed,
                Status = Succeeded()
            }, etag, false, token);
        }, cancellationToken);
    }

    public async Task<StoredResource<ConnectedServiceResource>> DeactivateAsync(
        ResourceScopeRef scopeRef,
        string name,
        string? etag,
        CancellationToken cancellationToken)
    {
        var existing = await GetExactAsync(scopeRef, name, cancellationToken)
            ?? throw new ConnectedServiceNotFoundException(name);
        if (!existing.Value.Definition.Enabled) return existing;
        return await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
            await store.PutExactAsync(scopeRef, existing.Value with
            {
                Generation = checked(existing.Value.Generation + 1),
                Definition = existing.Value.Definition with { Enabled = false },
                ObservedState = Observed(ConnectedServiceAvailability.Disabled, "disabled",
                    "The Connected Service is disabled."),
                Status = Succeeded()
            }, etag, false, token), cancellationToken);
    }

    public async Task DeleteAsync(
        ResourceScopeRef scopeRef,
        string name,
        string? etag,
        CancellationToken cancellationToken)
    {
        var existing = await GetExactAsync(scopeRef, name, cancellationToken)
            ?? throw new ConnectedServiceNotFoundException(name);
        await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            await store.DeleteExactAsync(Scoped(scopeRef, name), etag, token);
            return true;
        }, cancellationToken);
    }

    public async Task<ResolvedConnectedService?> ResolveAsync(
        ConnectedServiceReference service,
        ConnectedServiceResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(context);
        if (service.Address.Kind != ConnectedServiceResourceKinds.ConnectedService)
            throw new ConnectedServiceValidationException("The referenced resource must be a ConnectedService.");
        if (!await useAuthorizer.IsSameOrDescendantAsync(service.ScopeRef, context.ConsumerScopeRef, cancellationToken))
            throw new ConnectedServiceAccessDeniedException(service.Address);
        var stored = await store.GetExactAsync<ConnectedServiceResource>(new ScopedResourceAddress(
            service.ScopeRef, service.Address.Namespace, service.Address.Kind, service.Address.Name), cancellationToken);
        if (stored is null) return null;
        if (!stored.Value.Definition.Enabled
            || stored.Value.ObservedState?.Availability != ConnectedServiceAvailability.Ready)
            throw new ConnectedServiceUnavailableException(service.Address);
        if (!await useAuthorizer.CanUseAsync(service.ScopeRef, context.ConsumerScopeRef,
                stored.Value.Definition.UsePolicy, cancellationToken))
            throw new ConnectedServiceAccessDeniedException(service.Address);

        var (_, version) = await ResolveTypeAsync(stored.Value, cancellationToken);

        var inputs = new List<ResolvedConnectedServiceInput>();
        try
        {
            foreach (var binding in stored.Value.Definition.Bindings)
                inputs.Add(await ResolveBindingAsync(binding, stored.Value, cancellationToken));
            return new ResolvedConnectedService
            {
                Reference = service,
                Type = stored.Value.Definition.Type,
                Options = stored.Value.Definition.Options.Clone(),
                Exports = version.Exports,
                Inputs = inputs
            };
        }
        catch
        {
            foreach (var input in inputs) input.Dispose();
            throw;
        }
    }

    private async Task<StoredResource<ConnectedServiceResource>> PutAsync(
        StoredResource<ConnectedServiceResource> existing,
        ConnectedServiceProperties definition,
        string? etag,
        CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(existing.Value);
        var changed = existing.Value with { Definition = definition };
        return await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var observed = await ValidateAsync(changed, token);
            return await store.PutExactAsync(scopeRef, changed with
            {
                Generation = checked(existing.Value.Generation + 1),
                ObservedState = observed,
                Status = Succeeded()
            }, etag, false, token);
        }, cancellationToken);
    }

    private async Task<ConnectedServiceObservedState> ValidateAsync(
        ConnectedServiceResource resource,
        CancellationToken cancellationToken)
    {
        if (resource.Kind != ConnectedServiceResourceKinds.ConnectedService
            || resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw new ConnectedServiceValidationException("Invalid ConnectedService resource envelope.");
        ValidateName(resource.Name);
        if (string.IsNullOrWhiteSpace(resource.Definition?.DisplayName)
            || resource.Definition.DisplayName.Length > MaximumDisplayNameLength)
            throw new ConnectedServiceValidationException(
                $"Connected Service display names must contain 1 to {MaximumDisplayNameLength} characters.");
        if (resource.Definition.Description?.Length > MaximumDescriptionLength)
            throw new ConnectedServiceValidationException(
                $"Connected Service descriptions cannot exceed {MaximumDescriptionLength} characters.");
        if (resource.Definition.Type is null
            || resource.Definition.Type.Type is null
            || string.IsNullOrWhiteSpace(resource.Definition.Type.Type.Name)
            || resource.Definition.Type.TypeUid == Guid.Empty
            || string.IsNullOrWhiteSpace(resource.Definition.Type.Version)
            || string.IsNullOrWhiteSpace(resource.Definition.Type.SchemaDigest))
            throw new ConnectedServiceValidationException(
                "Connected Service type references require an exact type, typeUid, version and schemaDigest.");
        if (resource.Definition.Bindings is null)
            throw new ConnectedServiceValidationException("Connected Service bindings cannot be null.");
        if (resource.Definition.Options.ValueKind != JsonValueKind.Object)
            throw new ConnectedServiceValidationException("Connected Service options must be a JSON object.");
        if (JsonSerializer.SerializeToUtf8Bytes(resource.Definition.Options).Length > MaximumOptionsBytes)
            throw new ConnectedServiceValidationException(
                $"Connected Service options cannot exceed {MaximumOptionsBytes} bytes.");
        var scopeRef = RequireScope(resource);
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        await useAuthorizer.ValidateAsync(scopeRef, resource.Definition.UsePolicy, cancellationToken);
        var (_, version) = await ResolveTypeAsync(resource, cancellationToken);
        ValidateOptions(resource.Definition.Options, version.Schema);
        await ValidateBindingsAsync(resource, version, cancellationToken);
        return resource.Definition.Enabled
            ? Observed(ConnectedServiceAvailability.Ready)
            : Observed(ConnectedServiceAvailability.Disabled, "disabled", "The Connected Service is disabled.");
    }

    private async Task<ConnectedServiceObservedState> ObserveAsync(
        ConnectedServiceResource resource,
        CancellationToken cancellationToken)
    {
        if (!resource.Definition.Enabled)
            return Observed(ConnectedServiceAvailability.Disabled, "disabled", "The Connected Service is disabled.");
        try
        {
            _ = await ResolveTypeAsync(resource, cancellationToken);
            return Observed(ConnectedServiceAvailability.Ready);
        }
        catch (ConnectedServiceTypeRevisionException exception)
        {
            return Observed(ConnectedServiceAvailability.Stale, "type_revision_mismatch", exception.Message);
        }
        catch (ConnectedServiceValidationException exception)
        {
            return Observed(ConnectedServiceAvailability.Unavailable, "type_unavailable", exception.Message);
        }
    }

    private async Task<(ConnectedServiceTypeResource Type, ConnectedServiceTypeVersionDescriptor Version)>
        ResolveTypeAsync(ConnectedServiceResource resource, CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(resource);
        var reference = resource.Definition.Type.Type;
        var typeScope = reference.ScopeRef
            ?? throw new ConnectedServiceValidationException("The Connected Service type reference must include an exact scopeRef.");
        if (!await useAuthorizer.IsSameOrDescendantAsync(typeScope, scopeRef, cancellationToken))
            throw new ConnectedServiceValidationException(
                "The Connected Service type must belong to the same scope or an ancestor scope.");
        var address = reference.Resolve(resource.Namespace, ConnectedServiceResourceKinds.ConnectedServiceType);
        var stored = await store.GetExactAsync<ConnectedServiceTypeResource>(new ScopedResourceAddress(
            typeScope, address.Namespace, address.Kind, address.Name), cancellationToken)
            ?? throw new ConnectedServiceValidationException($"Connected Service type '{address}' was not found.");
        if (stored.Value.Uid != resource.Definition.Type.TypeUid)
            throw new ConnectedServiceTypeRevisionException(
                "The pinned Connected Service type UID no longer matches the stored resource.");
        if (!stored.Value.Definition.Enabled)
            throw new ConnectedServiceValidationException($"Connected Service type '{address}' is disabled.");
        if (!await useAuthorizer.CanUseAsync(typeScope, scopeRef,
                stored.Value.Definition.UsePolicy, cancellationToken))
            throw new ConnectedServiceValidationException(
                $"Connected Service type '{address}' cannot be used from this scope.");
        var version = stored.Value.Definition.Versions.SingleOrDefault(value =>
            string.Equals(value.Version, resource.Definition.Type.Version, StringComparison.Ordinal));
        if (version is null)
            throw new ConnectedServiceTypeRevisionException(
                $"Connected Service type version '{resource.Definition.Type.Version}' is not available.");
        if (!string.Equals(version.SchemaDigest, resource.Definition.Type.SchemaDigest, StringComparison.Ordinal))
            throw new ConnectedServiceTypeRevisionException(
                "The Connected Service type schema digest no longer matches the pinned revision.");
        return (stored.Value, version);
    }

    private async Task ValidateBindingsAsync(
        ConnectedServiceResource resource,
        ConnectedServiceTypeVersionDescriptor version,
        CancellationToken cancellationToken)
    {
        var duplicate = resource.Definition.Bindings.GroupBy(value => value.InputId, StringComparer.Ordinal)
            .FirstOrDefault(value => value.Count() > 1);
        if (duplicate is not null)
            throw new ConnectedServiceValidationException($"Input '{duplicate.Key}' is bound more than once.");
        var declared = version.Inputs.ToDictionary(value => value.Id, StringComparer.Ordinal);
        foreach (var binding in resource.Definition.Bindings)
        {
            if (!declared.TryGetValue(binding.InputId, out var input))
                throw new ConnectedServiceValidationException($"Input '{binding.InputId}' is not declared by the pinned type revision.");
            ValidateBindingShape(binding, input);
            if (binding.Kind == ConnectedServiceBindingKind.Parameter)
            {
                using var resolved = await ResolveBindingAsync(binding, resource, cancellationToken);
                if (resolved.Value is { } value && !Matches(value, input.Type))
                    throw new ConnectedServiceValidationException($"Parameter bound to input '{binding.InputId}' does not match type '{input.Type}'.");
            }
            else
            {
                await ValidateSecretReferenceAsync(binding, resource, cancellationToken);
            }
        }
        var supplied = resource.Definition.Bindings.Select(value => value.InputId).ToHashSet(StringComparer.Ordinal);
        var missing = version.Inputs.FirstOrDefault(value => value.Required && !supplied.Contains(value.Id));
        if (missing is not null)
            throw new ConnectedServiceValidationException($"Required input '{missing.Id}' is not bound.");
    }

    private async Task<ResolvedConnectedServiceInput> ResolveBindingAsync(
        ConnectedServiceValueBinding binding,
        ConnectedServiceResource service,
        CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(service);
        var consumer = service.Address;
        if (binding.Kind == ConnectedServiceBindingKind.Parameter)
        {
            var resolved = await parameters.ResolveAsync(binding.Parameter!, new(scopeRef, consumer), cancellationToken)
                ?? throw new ConnectedServiceValidationException($"Parameter bound to input '{binding.InputId}' was not found.");
            return new ResolvedConnectedServiceInput
            {
                Id = binding.InputId,
                Type = TypeOf(resolved.Value),
                Value = resolved.Value.Clone()
            };
        }
        var secret = await secrets.ResolveAsync(binding.Secret!, new(scopeRef, consumer), cancellationToken)
            ?? throw new ConnectedServiceValidationException($"Secret bound to input '{binding.InputId}' was not found.");
        return new ResolvedConnectedServiceInput
        {
            Id = binding.InputId,
            Type = ConnectedServiceValueType.Text,
            Secret = secret.Value
        };
    }

    private async Task ValidateSecretReferenceAsync(
        ConnectedServiceValueBinding binding,
        ConnectedServiceResource service,
        CancellationToken cancellationToken)
    {
        var reference = binding.Secret!;
        var secretScope = reference.ScopeRef!.Value;
        var serviceScope = RequireScope(service);
        if (!await useAuthorizer.IsSameOrDescendantAsync(secretScope, serviceScope, cancellationToken))
            throw new ConnectedServiceValidationException(
                $"Secret bound to input '{binding.InputId}' is outside the Connected Service scope.");
        var stored = await store.GetExactAsync<SecretResource>(new ScopedResourceAddress(
            secretScope,
            reference.Address.Namespace,
            SecretResourceKinds.Secret,
            reference.Address.Name), cancellationToken)
            ?? throw new ConnectedServiceValidationException(
                $"Secret bound to input '{binding.InputId}' was not found.");
        if (!await useAuthorizer.CanUseAsync(secretScope, serviceScope,
                stored.Value.Definition.UsePolicy, cancellationToken))
            throw new ConnectedServiceValidationException(
                $"Secret bound to input '{binding.InputId}' cannot be used by this Connected Service.");
    }

    private static void ValidateBindingShape(
        ConnectedServiceValueBinding binding,
        ConnectedServiceInputDescriptor input)
    {
        var valid = binding.Kind switch
        {
            ConnectedServiceBindingKind.Parameter => binding.Parameter is not null && binding.Secret is null
                && input.Protection == ConnectedServiceValueProtection.Standard,
            ConnectedServiceBindingKind.Secret => binding.Parameter is null && binding.Secret is { ScopeRef: not null }
                && input.Type == ConnectedServiceValueType.Text,
            _ => false
        };
        if (!valid)
            throw new ConnectedServiceValidationException(
                $"Binding for input '{binding.InputId}' does not match its declared protection and type.");
    }

    private static void ValidateOptions(JsonElement values, JsonElement schema)
    {
        var issues = ConnectedServiceOptionSchemaValidator.Validate(values, schema);
        if (issues.Count > 0)
            throw new ConnectedServiceValidationException(string.Join(" ", issues));
    }

    private ConnectedServiceObservedState Observed(
        ConnectedServiceAvailability availability,
        string? code = null,
        string? message = null) => new()
    {
        Availability = availability,
        ObservedAt = timeProvider.GetUtcNow(),
        Code = code,
        Message = message
    };

    private static bool Matches(JsonElement value, ConnectedServiceValueType type) => type switch
    {
        ConnectedServiceValueType.Text => value.ValueKind == JsonValueKind.String,
        ConnectedServiceValueType.WholeNumber => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        ConnectedServiceValueType.DecimalNumber => value.ValueKind == JsonValueKind.Number,
        ConnectedServiceValueType.Logical => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => false
    };

    private static ConnectedServiceValueType TypeOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => ConnectedServiceValueType.Text,
        JsonValueKind.Number when value.TryGetInt64(out _) => ConnectedServiceValueType.WholeNumber,
        JsonValueKind.Number => ConnectedServiceValueType.DecimalNumber,
        JsonValueKind.True or JsonValueKind.False => ConnectedServiceValueType.Logical,
        _ => throw new ConnectedServiceValidationException("Connected Service inputs only support scalar values.")
    };

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !char.IsLetterOrDigit(name[0])
            || name.Any(character => !char.IsLower(character) && !char.IsDigit(character)
                && character is not '-' and not '.'))
            throw new ConnectedServiceValidationException(
                "Connected Service names must contain 1 to 128 lowercase letters, digits, '-' or '.', and start with a letter or digit.");
    }

    private static ResourceScopeRef RequireScope(Resource resource) => resource.ScopeRef
        ?? throw new ConnectedServiceValidationException($"Resource '{resource.Address}' has no ownership scope.");
    private static ScopedResourceAddress Scoped(ResourceScopeRef scopeRef, string name) =>
        ScopedResourceAddress.Create(scopeRef, ResourceNamespace.Default,
            ConnectedServiceResourceKinds.ConnectedService, name);
    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
}

public class ConnectedServiceValidationException(string message) : Exception(message);
public sealed class ConnectedServiceTypeRevisionException(string message) : ConnectedServiceValidationException(message);
public sealed class ConnectedServiceNotFoundException(string name) : Exception($"Connected Service '{name}' was not found.");
public sealed class ConnectedServiceAccessDeniedException(ResourceAddress address)
    : Exception($"Connected Service '{address}' is outside the execution context.");
public sealed class ConnectedServiceUnavailableException(ResourceAddress address)
    : Exception($"Connected Service '{address}' is not ready for use.");
