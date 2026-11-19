using System.Net;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Components.Pages;

public partial class Storages
{
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? pending;
    private IReadOnlyList<StorageResource>? items;
    private IReadOnlyList<StorageTypeResource>? types;
    private CollectionQuery query = new();
    private CollectionQuery? loadedQuery;
    private CollectionPagination? pagination;
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? contextKey;
    private long revision;
    private bool initialized;
    private bool interactive;
    private bool disposed;
    private CollectionPagination? Pagination => pagination;
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}";

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
    private Task ReloadAsync() => LoadAsync(query, refreshTypes: true);
    private Task ChangeQueryAsync(CollectionQuery next) => next.Page < 1 || next.PageSize is not (20 or 50 or 100) || next.HasFilters
        ? Task.CompletedTask : LoadAsync(next);

    private async Task LoadAsync(CollectionQuery requested, bool refreshTypes = false)
    {
        if (disposed) return;
        var requestRevision = ++revision;
        pending?.Cancel();
        pending?.Dispose();
        pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var token = pending.Token;
        var scope = CurrentContextKey;
        if (scope != contextKey) { items = null; types = null; loadedQuery = null; pagination = null; requested = new(); contextKey = scope; }
        query = requested;
        if (Context.Error is not null || !Context.HasPermission(AuthorizationPermissions.ResourcesRead))
        {
            items = null;
            types = null;
            pagination = null;
            loadState = new(Context.Error is null ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed);
            return;
        }
        if (loadedQuery is null || !loadedQuery.HasSameCriteria(requested) || loadedQuery.PageSize != requested.PageSize) items = null;
        loadState = new(items is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = items is not null };
        try
        {
            var owner = ResourceScopeRef.Parse(Context.Current!.SelectedScopeRef);
            var pageTask = Client.GetStoragesPageAsync(owner, checked((requested.Page - 1) * requested.PageSize), requested.PageSize, token);
            // Keep the existing supplementary type-contract evidence catalog. Page navigation reuses it;
            // primary Storage pagination never calls the legacy GetStoragesAsync fetch-all method.
            var typesTask = refreshTypes || types is null ? Client.GetTypesAsync(owner, token) : Task.FromResult(types);
            await Task.WhenAll(pageTask, typesTask);
            var result = await pageTask;
            if (!IsCurrent(requestRevision, scope)) return;
            types = await typesTask;
            // Correct a removed page with one bounded request for the first page.
            if (result.Value.Count == 0 && string.IsNullOrEmpty(result.NextLink) && requested.Page > 1)
            {
                await LoadAsync(requested with { Page = 1 });
                return;
            }
            items = result.Value;
            loadedQuery = requested;
            pagination = new(requested.Page, requested.PageSize, !string.IsNullOrEmpty(result.NextLink));
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
            var forbidden = status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            if (forbidden) { items = null; types = null; pagination = null; }
            loadState = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed)
            {
                HasContent = items is not null,
                ErrorId = (error as AgentstrationApiException)?.ErrorId
            };
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
