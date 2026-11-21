using Microsoft.AspNetCore.WebUtilities;

namespace Agentstration.Web.Components.State;

public sealed record ConsoleResourceLocation(Uri Location, string ContextScopeRef, Guid Uid,
    string OwnerScopeRef, string Namespace, string Kind, string Name, string? CollectionLocation = null);

public sealed record ConsoleResourceSwitchFailure(Uri Target, string SourceLocation, string FallbackLocation);

/// <summary>Shares the current saved identity and observes its normal load after an accepted context switch.</summary>
public sealed class ConsoleResourceNavigationState
{
    private object? registration;
    private PendingSwitch? pending;
    private ConsoleResourceSwitchFailure? failure;
    public ConsoleResourceLocation? Current { get; private set; }
    public bool IsLoading { get; private set; }
    public bool HasFrame => registration is not null;
    public event Action? Changed;
    public event Action<ConsoleResourceSwitchFailure>? ResourceUnavailable;

    public ConsoleResourceLocation? GetCurrent(Uri location, string contextScopeRef) =>
        Current is { } current && current.ContextScopeRef == contextScopeRef
        && SameLocation(current.Location, location, contextScopeRef, current.OwnerScopeRef) ? current : null;

    public void BeginSwitch(Uri source, Uri target, string fallbackLocation, ConsoleResourceLocation? resource)
    {
        failure = null;
        pending = resource is null ? null : new(source, target, fallbackLocation, resource);
    }

    public void LocationChanged(Uri location)
    {
        if (pending is { } loading && !SameLocation(loading.Target, location)) pending = null;
        if (failure is { } rejected && !SameLocation(rejected.Target, location)) failure = null;
    }

    // Returns true when the frame must suppress content until the fallback navigation completes.
    public bool Observe(object owner, Uri location, string contextScopeRef,
        ConsoleResourceLocation? resource, ResourceLoadState? loadState)
    {
        var requestedContext = ConsoleNavigationContext.GetScopeRef(location.AbsoluteUri);
        if (requestedContext is not null && requestedContext != contextScopeRef) return false;
        if (failure is { } rejected && SameLocation(rejected.Target, location)) return true;

        var ready = resource is not null && loadState?.BlocksContent != true;
        if (pending is { } loading && SameLocation(loading.Target, location))
        {
            var unavailable = loadState?.Status is ResourceLoadStatus.Forbidden or ResourceLoadStatus.NotFound;
            var replaced = ready && (resource!.OwnerScopeRef != loading.Resource.OwnerScopeRef
                || resource.Namespace != loading.Resource.Namespace || resource.Kind != loading.Resource.Kind
                || resource.Name != loading.Resource.Name
                || loading.Resource.Uid != Guid.Empty && resource.Uid != loading.Resource.Uid);
            if (unavailable || replaced)
            {
                pending = null;
                failure = new(location, loading.Source.AbsoluteUri, loading.FallbackLocation);
                ResourceUnavailable?.Invoke(failure);
                return true;
            }
            if (ready) pending = null;
        }

        var next = ready ? resource : null;
        var isLoading = !ready && loadState?.Status is ResourceLoadStatus.Loading or ResourceLoadStatus.Refreshing;
        var changed = !ReferenceEquals(registration, owner) || Current != next || IsLoading != isLoading;
        registration = owner;
        Current = next;
        IsLoading = isLoading;
        if (changed) Changed?.Invoke();
        return false;
    }

    public void Release(object owner)
    {
        if (!ReferenceEquals(registration, owner)) return;
        registration = null;
        Current = null;
        IsLoading = false;
        Changed?.Invoke();
    }

    public static bool SameLocation(Uri first, Uri second, string? context = null, string? owner = null) =>
        CanonicalLocation(first, context, owner) == CanonicalLocation(second, context, owner);

    private static string CanonicalLocation(Uri location, string? context, string? owner)
    {
        var query = QueryHelpers.ParseQuery(location.Query);
        query.Remove("view"); query.Remove("tab"); query.Remove("returnUrl");
        if (context is not null && (!query.TryGetValue("context", out var requestedContext) || string.IsNullOrWhiteSpace(requestedContext.ToString()))) query["context"] = context;
        if (owner is not null && (!query.TryGetValue("scopeRef", out var requestedOwner) || string.IsNullOrWhiteSpace(requestedOwner.ToString()))) query["scopeRef"] = owner;
        return QueryHelpers.AddQueryString(location.GetLeftPart(UriPartial.Path), query.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    private sealed record PendingSwitch(Uri Source, Uri Target, string FallbackLocation, ConsoleResourceLocation Resource);
}
