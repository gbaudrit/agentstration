using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Models;

public sealed class ModelProviderDiscoveryState(IResourceStore store, TimeProvider timeProvider)
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);
    private static ScopedResourceAddress Address(ModelProviderResource provider) => ScopedResourceAddress.Create(
        ResourceScopeRef.Instance, provider.Namespace, ModelProviderDiscoveryResource.ResourceKind, provider.Uid.ToString("N"));

    internal async Task DeleteAsync(ModelProviderResource provider, CancellationToken cancellationToken)
    {
        var current = await store.GetExactAsync<ModelProviderDiscoveryResource>(Address(provider), cancellationToken);
        if (current is null) return;
        try { await store.DeleteExactAsync(Address(provider), current.ETag, cancellationToken); }
        catch (ResourceConcurrencyException) { /* A finishing observation must not fail provider deletion. */ }
    }

    public async Task<ModelProviderDiscoveryObservation> ReadAsync(ModelProviderResource provider, CancellationToken cancellationToken)
    {
        var state = await store.GetExactAsync<ModelProviderDiscoveryResource>(Address(provider), cancellationToken);
        return state?.Value.ProviderGeneration == provider.Generation ? state.Value.Observation : new();
    }

    public async Task<ModelProviderDiscoveryObservation> RequestAsync(ModelProviderResource provider, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var current = await store.GetExactAsync<ModelProviderDiscoveryResource>(Address(provider), cancellationToken);
            var observation = current?.Value.ProviderGeneration == provider.Generation ? current.Value.Observation : new();
            if (observation.State == "queued" || (observation.State == "running" && observation.LeaseUntil > timeProvider.GetUtcNow()))
                return observation;
            var queued = observation with { State = "queued", NextAttemptAt = timeProvider.GetUtcNow(), LeaseUntil = null };
            try
            {
                await SaveAsync(provider, current, queued, cancellationToken);
                return queued;
            }
            catch (ResourceConcurrencyException) when (attempt < 3) { }
        }
        throw new InvalidOperationException("Unable to queue model discovery.");
    }

    internal async Task<StoredResource<ModelProviderDiscoveryResource>?> ClaimAsync(ModelProviderResource provider, CancellationToken cancellationToken)
    {
        var current = await store.GetExactAsync<ModelProviderDiscoveryResource>(Address(provider), cancellationToken);
        var observation = current?.Value.ProviderGeneration == provider.Generation ? current.Value.Observation : new();
        var now = timeProvider.GetUtcNow();
        if (observation.State == "running" && observation.LeaseUntil > now) return null;
        if (observation.State != "running" && observation.NextAttemptAt > now) return null;
        try
        {
            return await SaveAsync(provider, current, observation with
            {
                State = "running",
                LastAttemptAt = now,
                LeaseUntil = now.AddMinutes(2),
                NextAttemptAt = null
            }, cancellationToken);
        }
        catch (ResourceConcurrencyException) { return null; }
    }

    internal async Task FinishAsync(StoredResource<ModelProviderDiscoveryResource> claim,
        ModelDiscoveryDiff? result, string health, string? errorCode, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var observation = claim.Value.Observation;
        var failures = result is null ? Math.Min(observation.Failures + 1, 10) : 0;
        var next = result is null ? TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, failures - 1))) : RefreshInterval;
        try
        {
            await store.PutExactAsync(ResourceScopeRef.Instance, claim.Value with
            {
                Observation = observation with
                {
                    State = result is null ? "failed" : "succeeded",
                    Health = health,
                    LastSuccessAt = result is null ? observation.LastSuccessAt : now,
                    NextAttemptAt = now + next,
                    LeaseUntil = null,
                    Failures = failures,
                    ErrorCode = errorCode,
                    Result = result is null ? observation.Result : new(result.Created, result.Updated, result.Unchanged,
                        result.Missing, result.Reappeared, result.Total)
                }
            }, claim.ETag, false, cancellationToken);
        }
        catch (ResourceConcurrencyException) { /* A newer request or provider generation owns the observation. */ }
    }

    private Task<StoredResource<ModelProviderDiscoveryResource>> SaveAsync(ModelProviderResource provider,
        StoredResource<ModelProviderDiscoveryResource>? current, ModelProviderDiscoveryObservation observation,
        CancellationToken cancellationToken) => store.PutExactAsync(ResourceScopeRef.Instance, new ModelProviderDiscoveryResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ModelProviderDiscoveryResource.ResourceKind,
            Metadata = new() { Namespace = provider.Namespace, Name = provider.Uid.ToString("N") },
            ScopeRef = ResourceScopeRef.Instance,
            Uid = current?.Value.Uid ?? Guid.Empty,
            ProviderUid = provider.Uid,
            ProviderGeneration = provider.Generation,
            Observation = observation
        }, current?.ETag, current is null, cancellationToken);
}
