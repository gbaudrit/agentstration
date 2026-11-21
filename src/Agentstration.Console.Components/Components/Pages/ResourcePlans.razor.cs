using System.Collections.Concurrent;
using System.Net;
using Agentstration.Identity.Contracts;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Components.Pages;

public partial class ResourcePlans
{
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? pending;
    private IReadOnlyList<ResourcePlanSnapshot>? items;
    private CollectionQuery query = new();
    private CollectionQuery? loadedQuery;
    private CollectionPagination? pagination;
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? contextKey;
    private long revision;
    private bool initialized;
    private bool interactive;
    private bool disposed;
    private readonly Dictionary<Guid, PlanSummary> summaries = [];
    private sealed record PlanSummary(int? Changes, ResourceChangeSetReadiness? Readiness, bool Stale);
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}|{Context.Current?.WorkspaceId:D}";

    protected override async Task OnInitializedAsync()
    {
        Context.Changed += ContextChanged;
        await Context.EnsureLoadedAsync(cancellation.Token);
        if (disposed) return;
        contextKey = CurrentContextKey;
        initialized = true;
        await ReloadAsync();
    }
    protected override void OnAfterRender(bool firstRender) { if (firstRender) { interactive = true; StateHasChanged(); } }
    private Task ReloadAsync() => LoadAsync(query);
    private Task ChangeQueryAsync(CollectionQuery next)
    {
        var status = next.Filter("status").Value;
        return next.Page < 1 || next.PageSize is not (20 or 50 or 100) || !string.IsNullOrWhiteSpace(next.Search)
            || next.Filters.Keys.Any(key => key != "status")
            || status.Length > 0 && (!Enum.TryParse<ResourcePlanStatus>(status, out var value) || !Enum.IsDefined(value))
            ? Task.CompletedTask : LoadAsync(next);
    }

    private async Task LoadAsync(CollectionQuery requested)
    {
        if (disposed) return;
        var requestRevision = ++revision;
        pending?.Cancel();
        pending?.Dispose();
        pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var token = pending.Token;
        var scope = CurrentContextKey;
        if (scope != contextKey) { items = null; summaries.Clear(); loadedQuery = null; pagination = null; requested = new(); contextKey = scope; }
        query = requested;
        if (Context.Error is not null || !Context.HasPermission(AuthorizationPermissions.ResourcesRead))
        {
            items = null;
            summaries.Clear();
            pagination = null;
            loadState = new(Context.Error is null ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed);
            return;
        }
        if (loadedQuery is null || !loadedQuery.HasSameCriteria(requested) || loadedQuery.PageSize != requested.PageSize) { items = null; summaries.Clear(); pagination = null; }
        loadState = new(items is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = items is not null };
        try
        {
            var status = Enum.TryParse<ResourcePlanStatus>(requested.Filter("status").Value, out var parsed) ? parsed : (ResourcePlanStatus?)null;
            var result = await Client.ListPlansAsync(status, checked((requested.Page - 1) * requested.PageSize), requested.PageSize, token);
            if (!IsCurrent(requestRevision, scope)) return;
            items = result.Items;
            summaries.Clear();
            loadedQuery = requested;
            pagination = new(requested.Page, requested.PageSize, result.HasMore);
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
            _ = LoadSummariesAsync(result.Items, requestRevision, scope, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
            var forbidden = status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            if (forbidden) { items = null; summaries.Clear(); pagination = null; }
            loadState = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed)
            {
                HasContent = items is not null,
                ErrorId = (error as AgentstrationApiException)?.ErrorId
            };
        }
    }
    private async Task LoadSummariesAsync(IReadOnlyList<ResourcePlanSnapshot> pageItems, long requestRevision, string scope, CancellationToken token)
    {
        try
        {
            var loaded = new ConcurrentDictionary<Guid, PlanSummary>();
            // Preserve the existing bounded per-plan review reads and concurrency. They are not
            // a global status/filter catalog, and never change primary pagination capabilities.
            await Parallel.ForEachAsync(pageItems, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (item, cancellationToken) =>
            {
                if (!IsCurrent(requestRevision, scope)) return;
                var sets = await Client.ListChangeSetsAsync(item.Value.Id.Value, 0, 50, cancellationToken);
                var latest = ResourcePlanReviewProjection.LatestChangeSet(item.Value, sets.Items);
                if (latest is null) { loaded[item.Value.Id.Value] = new(null, null, false); return; }
                var validations = await Client.ListValidationsAsync(latest.Value.Id.Value, cancellationToken);
                var validation = ResourcePlanReviewProjection.LatestValidation(latest.Value, validations);
                loaded[item.Value.Id.Value] = new(latest.Value.Changes.Count, validation?.Readiness,
                    ResourcePlanReviewProjection.IsStale(item.Value, latest.Value)
                    || validation is not null && ResourcePlanReviewProjection.IsStale(item.Value, latest.Value, validation));
            });
            if (!IsCurrent(requestRevision, scope)) return;
            await InvokeAsync(() =>
            {
                if (!IsCurrent(requestRevision, scope) || !ReferenceEquals(items, pageItems)) return;
                summaries.Clear();
                foreach (var entry in loaded) summaries.Add(entry.Key, entry.Value);
                StateHasChanged();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            await InvokeAsync(() =>
            {
                if (!IsCurrent(requestRevision, scope) || !ReferenceEquals(items, pageItems)) return;
                var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
                var forbidden = status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
                if (forbidden) { items = null; summaries.Clear(); pagination = null; }
                loadState = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed) { HasContent = items is not null, ErrorId = (error as AgentstrationApiException)?.ErrorId };
                StateHasChanged();
            });
        }
    }

    private bool IsCurrent(long requestRevision, string? scope) => !disposed && requestRevision == revision && CurrentContextKey == scope;
    private async Task RetryAsync() { if (Context.Error is not null) await Context.LoadAsync(cancellation.Token); await ReloadAsync(); }
    private void ContextChanged()
    {
        if (!initialized || disposed) return;
        _ = InvokeAsync(async () =>
        {
            if (disposed) return;
            if (CurrentContextKey != contextKey || Context.Error is not null || !Context.HasPermission(AuthorizationPermissions.ResourcesRead) || loadState.Status == ResourceLoadStatus.Forbidden)
            {
                var loading = ReloadAsync();
                StateHasChanged();
                await loading;
            }
            StateHasChanged();
        });
    }
    public void Dispose() { disposed = true; revision++; Context.Changed -= ContextChanged; pending?.Cancel(); pending?.Dispose(); cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this); }
}
