using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ModelProviders;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Models;

public sealed record ModelDiscoveryDiff(
    int Created,
    int Updated,
    int Unchanged,
    int Missing,
    int Reappeared,
    int Total);

public sealed class ModelDiscoveryFailedException(string providerName, string message, Exception? innerException = null)
    : Exception($"Model discovery for provider '{providerName}' failed: {message}", innerException);

public sealed class ModelDiscoveryService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    ModelProviderManagementService providers,
    IEnumerable<IModelProviderDiscovery> discoveries,
    TimeProvider timeProvider)
{
    public const int MaximumModels = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<StoredResource<ModelResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<ModelResource>(ModelResourceKinds.Model, cancellationToken);

    public Task<StoredResource<ModelResource>?> GetAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        store.GetAsync<ModelResource>(ResourceKey.Create(ModelResourceKinds.Model, name, @namespace), cancellationToken);

    public async Task<IReadOnlyList<StoredResource<ModelResource>>> ListProviderModelsAsync(
        ResourceNamespace providerNamespace,
        string providerName,
        CancellationToken cancellationToken)
    {
        var provider = await providers.GetAsync(providerNamespace, providerName, cancellationToken)
            ?? throw new ModelProviderResourceNotFoundException(providerName);
        return await ListProviderModelsAsync(provider.Value, cancellationToken);
    }

    public async Task<StoredResource<ModelResource>?> FindAsync(
        Guid providerUid,
        string externalId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        return (await ListAsync(cancellationToken)).SingleOrDefault(value =>
            value.Value.Definition.ProviderUid == providerUid
            && string.Equals(value.Value.Definition.ExternalId, externalId, StringComparison.Ordinal));
    }

    public static DiscoveredModel ToDiscoveredModel(
        ModelResource resource,
        ModelSpecificationOverride? specificationOverride = null) => new(
        resource.Definition.ExternalId,
        resource.Definition.DisplayName,
        resource.Definition.Observation.State switch
        {
            ModelObservationState.Available => resource.Definition.ProviderStatus,
            ModelObservationState.Missing => "unavailable",
            _ => "failed"
        },
        EffectiveModelSpecificationResolver.Resolve(resource.Definition.Specification, specificationOverride),
        resource.Definition.Identity,
        resource.Definition.Specification,
        specificationOverride,
        resource.Name);

    public async Task<ModelDiscoveryDiff> RefreshAsync(
        ResourceNamespace providerNamespace,
        string providerName,
        CancellationToken cancellationToken)
    {
        var storedProvider = await providers.GetAsync(providerNamespace, providerName, cancellationToken)
            ?? throw new ModelProviderResourceNotFoundException(providerName);
        var scopeRef = storedProvider.Value.ScopeRef
            ?? throw new ModelProviderValidationException("The model provider has no ownership scope.");

        return await scopeOperations.WriteAsync(
            storedProvider.Value,
            scopeRef,
            AuthorizationPermissions.ResourcesWrite,
            token => RefreshCoreAsync(storedProvider.Value, scopeRef, token),
            cancellationToken);
    }

    private async Task<ModelDiscoveryDiff> RefreshCoreAsync(
        ModelProviderResource providerResource,
        ResourceScopeRef scopeRef,
        CancellationToken cancellationToken)
    {
        var provider = await providers.GetConfigurationRequiredAsync(
            providerResource.Namespace,
            providerResource.Name,
            cancellationToken);
        var existing = await ListProviderModelsAsync(providerResource, cancellationToken);
        var now = timeProvider.GetUtcNow();
        IReadOnlyList<DiscoveredModel> observed;

        try
        {
            var discovery = discoveries.SingleOrDefault(candidate => candidate.CanHandle(provider.AdapterType))
                ?? throw new InvalidOperationException("No discovery adapter is registered in this host.");
            var health = await discovery.GetHealthAsync(provider, cancellationToken);
            if (!string.Equals(health.Status, "available", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(health.Details ?? $"Provider health is '{health.Status}'.");
            observed = await discovery.ListModelsAsync(provider, cancellationToken);
            ValidateObservation(observed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await MarkFailedAsync(scopeRef, existing, now, exception.Message, cancellationToken);
            throw new ModelDiscoveryFailedException(provider.Name, exception.Message, exception);
        }

        var byExternalId = existing.ToDictionary(
            value => value.Value.Definition.ExternalId,
            StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var created = 0;
        var updated = 0;
        var unchanged = 0;
        var reappeared = 0;

        foreach (var descriptor in observed.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            seen.Add(descriptor.Name);
            if (byExternalId.TryGetValue(descriptor.Name, out var current))
            {
                var changed = !ObservationEquals(current.Value.Definition, descriptor);
                var wasAvailable = current.Value.Definition.Observation.State == ModelObservationState.Available;
                var definition = current.Value.Definition with
                {
                    DisplayName = descriptor.DisplayName,
                    ProviderStatus = descriptor.Status,
                    Identity = descriptor.Identity,
                    Specification = descriptor.Specification,
                    Observation = current.Value.Definition.Observation with
                    {
                        State = ModelObservationState.Available,
                        LastObservedAt = now,
                        LastAttemptedAt = now,
                        ErrorCode = null,
                        ErrorMessage = null
                    }
                };
                await store.PutExactAsync(scopeRef, current.Value with
                {
                    Generation = checked(current.Value.Generation + 1),
                    Definition = definition,
                    Status = Status(ModelObservationState.Available, now)
                }, current.ETag, false, cancellationToken);
                if (!wasAvailable) reappeared++;
                else if (changed) updated++;
                else unchanged++;
                continue;
            }

            await store.PutExactAsync(scopeRef, new ModelResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ModelResourceKinds.Model,
                Metadata = new ResourceMetadata
                {
                    Namespace = providerResource.Namespace,
                    Name = ResourceName(providerResource, descriptor.Name)
                },
                ScopeRef = scopeRef,
                Generation = 1,
                Status = Status(ModelObservationState.Available, now),
                Definition = new ModelProperties
                {
                    DisplayName = descriptor.DisplayName,
                    Provider = new ResourceReference(providerResource.Name, scopeRef, providerResource.Namespace),
                    ProviderUid = providerResource.Uid,
                    ExternalId = descriptor.Name,
                    ProviderStatus = descriptor.Status,
                    Identity = descriptor.Identity,
                    Specification = descriptor.Specification,
                    Observation = new ModelObservation
                    {
                        State = ModelObservationState.Available,
                        FirstObservedAt = now,
                        LastObservedAt = now,
                        LastAttemptedAt = now
                    }
                }
            }, null, true, cancellationToken);
            created++;
        }

        var missing = 0;
        foreach (var current in existing.Where(value => !seen.Contains(value.Value.Definition.ExternalId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Value.Definition.Observation.State == ModelObservationState.Missing) continue;
            await store.PutExactAsync(scopeRef, current.Value with
            {
                Generation = checked(current.Value.Generation + 1),
                Definition = current.Value.Definition with
                {
                    Observation = current.Value.Definition.Observation with
                    {
                        State = ModelObservationState.Missing,
                        LastAttemptedAt = now,
                        ErrorCode = null,
                        ErrorMessage = null
                    }
                },
                Status = Status(ModelObservationState.Missing, now)
            }, current.ETag, false, cancellationToken);
            missing++;
        }

        return new ModelDiscoveryDiff(created, updated, unchanged, missing, reappeared, observed.Count);
    }

    private async Task<IReadOnlyList<StoredResource<ModelResource>>> ListProviderModelsAsync(
        ModelProviderResource provider,
        CancellationToken cancellationToken)
    {
        var scopeRef = provider.ScopeRef
            ?? throw new ModelProviderValidationException("The model provider has no ownership scope.");
        return (await store.ListExactAsync<ModelResource>(scopeRef, ModelResourceKinds.Model, 0, MaximumModels, cancellationToken))
            .Where(value => value.Value.Namespace == provider.Namespace
                && value.Value.Definition.ProviderUid == provider.Uid)
            .OrderBy(value => value.Value.Definition.ExternalId, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task MarkFailedAsync(
        ResourceScopeRef scopeRef,
        IReadOnlyList<StoredResource<ModelResource>> existing,
        DateTimeOffset now,
        string message,
        CancellationToken cancellationToken)
    {
        var safeMessage = message.Length <= 1024 ? message : message[..1024];
        foreach (var current in existing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.PutExactAsync(scopeRef, current.Value with
            {
                Generation = checked(current.Value.Generation + 1),
                Definition = current.Value.Definition with
                {
                    Observation = current.Value.Definition.Observation with
                    {
                        State = ModelObservationState.Failed,
                        LastAttemptedAt = now,
                        ErrorCode = "discovery_failed",
                        ErrorMessage = safeMessage
                    }
                },
                Status = Status(ModelObservationState.Failed, now, safeMessage)
            }, current.ETag, false, cancellationToken);
        }
    }

    private static void ValidateObservation(IReadOnlyList<DiscoveredModel> observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.Count > MaximumModels)
            throw new InvalidOperationException($"A provider returned more than {MaximumModels} models.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in observed)
        {
            if (string.IsNullOrWhiteSpace(model.Name) || model.Name.Length > 256)
                throw new InvalidOperationException("A discovered model identifier must contain between 1 and 256 characters.");
            if (string.IsNullOrWhiteSpace(model.DisplayName) || model.DisplayName.Length > 256)
                throw new InvalidOperationException($"Discovered model '{model.Name}' has an invalid display name.");
            if (!names.Add(model.Name))
                throw new InvalidOperationException($"Provider returned duplicate model '{model.Name}'.");
        }
    }

    private static bool ObservationEquals(ModelProperties current, DiscoveredModel observed) =>
        current.DisplayName == observed.DisplayName
        && current.ProviderStatus == observed.Status
        && JsonSerializer.Serialize(current.Identity, JsonOptions) == JsonSerializer.Serialize(observed.Identity, JsonOptions)
        && JsonSerializer.Serialize(current.Specification, JsonOptions) == JsonSerializer.Serialize(observed.Specification, JsonOptions);

    private static ResourceStatus Status(
        ModelObservationState state,
        DateTimeOffset transitionTime,
        string? message = null) => new()
    {
        ProvisioningState = ProvisioningState.Succeeded,
        Conditions =
        [
            new ResourceCondition
            {
                Type = "Observed",
                Status = state switch
                {
                    ModelObservationState.Available => "True",
                    ModelObservationState.Missing => "False",
                    _ => "Unknown"
                },
                Reason = state.ToString(),
                Message = message,
                LastTransitionTime = transitionTime
            }
        ]
    };

    private static string ResourceName(ModelProviderResource provider, string externalId)
    {
        var providerPart = Normalize(provider.Name, 64, "provider");
        var modelPart = Normalize(externalId, 96, "model");
        var identity = $"{provider.Uid:N}\n{externalId}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"{providerPart}.{modelPart}-{digest}";
    }

    private static string Normalize(string value, int maximumLength, string fallback)
    {
        var normalized = new string(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '-').ToArray()).Trim('-');
        if (normalized.Length == 0) normalized = fallback;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}
