using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Models;

public sealed class ModelProviderDiscoveryLifecycle(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    IRequestContextScopeFactory scopes,
    ModelProviderDiscoveryState state,
    ModelDiscoveryService discovery,
    TimeProvider timeProvider)
{
    public async Task<ModelProviderDiscoveryObservation> RequestAsync(ResourceNamespace providerNamespace,
        string providerName, CancellationToken cancellationToken)
    {
        var provider = await store.GetExactAsync<ModelProviderResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Instance, providerNamespace, ModelResourceKinds.ModelProvider, providerName), cancellationToken)
            ?? throw new ModelProviderResourceNotFoundException(providerName);
        return await scopeOperations.WriteAsync(provider.Value, ResourceScopeRef.Instance, AuthorizationPermissions.ResourcesWrite,
            token => state.RequestAsync(provider.Value, token), cancellationToken);
    }

    // Recovery is derived from stored provider UID/generation, so providers created by Bootstrap,
    // packs and previous processes follow exactly the same lifecycle as Console-created providers.
    public async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        const int pageSize = 100;
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await store.ListExactAsync<ModelProviderResource>(ResourceScopeRef.Instance,
                ModelResourceKinds.ModelProvider, offset, pageSize, cancellationToken);
            await Parallel.ForEachAsync(page, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (provider, token) => await ProcessAsync(provider.Value, token));
            if (page.Count < pageSize) return;
        }
    }

    public async Task ProcessAsync(ModelProviderResource provider, CancellationToken cancellationToken)
    {
        var claim = await state.ClaimAsync(provider, cancellationToken);
        if (claim is null) return;
        using var timeout = new CancellationTokenSource(ModelProviderDiscoveryState.AttemptTimeout, timeProvider);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var result = await discovery.RefreshAsync(provider.Namespace, provider.Name, attempt.Token);
            await state.FinishAsync(claim, result, "available", null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            await state.FinishAsync(claim, null, "unavailable", "discovery_timeout", cancellationToken);
        }
        catch (ModelDiscoveryFailedException exception)
        {
            await state.FinishAsync(claim, null, exception.Health, "discovery_failed", cancellationToken);
        }
        catch (ResourceConcurrencyException)
        {
            await state.FinishAsync(claim, null, "unknown", "provider_changed", cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Persist only a bounded invariant code; upstream exception text can contain secrets.
            await state.FinishAsync(claim, null, "unknown", "discovery_failed", cancellationToken);
        }
    }
}
