using System.Net;
using Agentstration.Sources.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Components.Pages;

public partial class Sources
{
    private CancellationTokenSource? pending;
    private CollectionQuery sourceQuery = new();
    private ResourceLoadState collectionLoad = new(ResourceLoadStatus.Loading);
    private string? collectionContextKey;
    private long revision;
    private bool collectionInitialized;
    private bool collectionDisposed;
    private string CurrentContextKey => $"{ContextState.Current?.UserId:D}|{ContextState.Current?.SelectedScopeRef}|{ContextState.Current?.TenantId:D}|{ContextState.Current?.WorkspaceId:D}";

    private bool IsCollection => Publisher is null || Name is null || string.IsNullOrWhiteSpace(ScopeRefValue);
    private bool hasCollectionAuthorization;
    private IReadOnlyList<SourceConsoleListItem> Filtered => (sources ?? []).Where(item =>
        (string.IsNullOrWhiteSpace(sourceQuery.Search) || item.Source.Configuration.Definition.DisplayName.Contains(sourceQuery.Search, StringComparison.OrdinalIgnoreCase) || item.Source.Source.Name.Contains(sourceQuery.Search, StringComparison.OrdinalIgnoreCase) || item.Source.Source.Definition.Publisher.Contains(sourceQuery.Search, StringComparison.OrdinalIgnoreCase))
        && (sourceQuery.Filter("health").Value.Length == 0 || item.Source.Observed.Definition.LastOutcome.ToString() == sourceQuery.Filter("health").Value)
        && (sourceQuery.Filter("origin").Value.Length == 0 || (item.Source.Configuration.Definition.Origin is null ? "pasted" : "url") == sourceQuery.Filter("origin").Value)
        && (sourceQuery.Filter("publisher").Value.Length == 0 || item.Source.Source.Definition.Publisher == sourceQuery.Filter("publisher").Value)
        && (sourceQuery.Filter("scope").Value.Length == 0 || item.Source.Source.ScopeRef!.Value.Value == sourceQuery.Filter("scope").Value)).ToArray();
    private IReadOnlyList<SourceConsoleListItem> SourcePageItems => Filtered.Skip(checked((sourceQuery.Page - 1) * sourceQuery.PageSize)).Take(sourceQuery.PageSize).ToArray();
    private CollectionPagination SourcePagination => new(sourceQuery.Page, sourceQuery.PageSize, sourceQuery.Page * sourceQuery.PageSize < Filtered.Count)
    {
        Total = Filtered.Count,
        SupportsDirectNavigation = true
    };

    protected override async Task OnInitializedAsync()
    {
        ContextState.Changed += CollectionContextChanged;
        await ContextState.EnsureLoadedAsync(lifetime.Token);
        if (collectionDisposed) return;
        collectionContextKey = CurrentContextKey;
        collectionInitialized = true;
    }

    private Task ChangeSourceQueryAsync(CollectionQuery next)
    {
        if (next.PageSize is not (20 or 50 or 100) || next.Page < 1) return Task.CompletedTask;
        sourceQuery = next;
        CorrectPage();
        return Task.CompletedTask;
    }

    private void CorrectPage()
    {
        var page = Math.Clamp(sourceQuery.Page, 1, Math.Max(1, (int)Math.Ceiling((double)Filtered.Count / sourceQuery.PageSize)));
        if (page != sourceQuery.Page) sourceQuery = sourceQuery with { Page = page };
    }

    private async Task LoadCollectionAsync()
    {
        if (collectionDisposed || !IsCollection) return;
        var requestRevision = ++revision;
        pending?.Cancel();
        pending?.Dispose();
        pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = pending.Token;
        var scope = CurrentContextKey;
        if (scope != collectionContextKey) { sources = null; hasCollectionAuthorization = false; sourceQuery = new(); collectionContextKey = scope; }
        if (ContextState.Error is not null)
        {
            sources = null; hasCollectionAuthorization = false;
            collectionLoad = new(ResourceLoadStatus.Failed);
            return;
        }
        collectionLoad = new(sources is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = sources is not null };
        try
        {
            var result = await Service.ListAsync(token);
            if (!IsCurrent(requestRevision, scope)) return;
            sources = result;
            hasCollectionAuthorization = true;
            CorrectPage();
            collectionLoad = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
            var forbidden = status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            if (forbidden) { sources = null; hasCollectionAuthorization = false; }
            collectionLoad = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed)
            {
                HasContent = sources is not null,
                ErrorId = (error as AgentstrationApiException)?.ErrorId
            };
        }
    }

    private bool IsCurrent(long requestRevision, string? scope) => !collectionDisposed && IsCollection && revision == requestRevision && CurrentContextKey == scope;
    private async Task RetryCollectionAsync() { if (ContextState.Error is not null) await ContextState.LoadAsync(lifetime.Token); await LoadCollectionAsync(); }
    private void CollectionContextChanged()
    {
        if (!collectionInitialized || collectionDisposed || !IsCollection) return;
        _ = InvokeAsync(async () =>
        {
            if (collectionDisposed || !IsCollection) return;
            // PlatformAdmin is rechecked by the API after every context notification.
            importing = false; importYaml = importUrl = ""; importScope = null; error = null;
            sources = null; hasCollectionAuthorization = false;
            var loading = LoadCollectionAsync();
            StateHasChanged();
            await loading;
            StateHasChanged();
        });
    }
    private void DisposeCollection() { collectionDisposed = true; revision++; ContextState.Changed -= CollectionContextChanged; pending?.Cancel(); pending?.Dispose(); }
}
