using System.Globalization;
using System.Net;
using Agentstration.Artifacts.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Components.Pages;

public partial class Artifacts
{
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? pending;
    private IReadOnlyList<StagedArtifactView>? staged;
    private IReadOnlyList<FlowRunArtifactResource>? durable;
    private CollectionQuery stagedQuery = new();
    private CollectionQuery durableQuery = new();
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? contextKey;
    private long revision;
    private bool initialized;
    private bool disposed;
    private bool interactive;
    private bool showDurable = true;
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}|{Context.Current?.WorkspaceId:D}";
    private bool CanInspect => Context.HasPermission(AuthorizationPermissions.ResourcesRead) && Context.HasPermission(AuthorizationPermissions.ArtifactsInspect);

    // Both existing endpoints return complete authorized Workspace arrays. Only metadata is read.
    private IReadOnlyList<FlowRunArtifactResource> FilteredDurable => (durable ?? []).Where(item =>
        Matches(DurableName(item), durableQuery.Search) || Matches(item.ArtifactId.ToString(), durableQuery.Search)).Where(item =>
        Matches(item.ProducerFlowRunId, durableQuery.Filter("run").Value)
        && (durableQuery.Filter("producer").Value.Length == 0 || Matches(item.ProducerResource?.Name, durableQuery.Filter("producer").Value) || Matches(item.ProducerFlowStepId, durableQuery.Filter("producer").Value))
        && Matches(item.Receipt.Materialization?.Target.Storage.Storage.Name, durableQuery.Filter("storage").Value)
        && (durableQuery.Filter("media").Value.Length == 0 || item.Receipt.MediaType == durableQuery.Filter("media").Value)
        && MatchesSize(item.Receipt.Length, durableQuery.Filter("size").Value)).ToArray();
    private IReadOnlyList<StagedArtifactView> FilteredStaged => (staged ?? []).Where(item =>
        (Matches(item.FileName, stagedQuery.Search) || Matches(item.ArtifactId.ToString(), stagedQuery.Search))
        && (stagedQuery.Filter("status").Value.Length == 0 || StatusKey(item.Status) == stagedQuery.Filter("status").Value)
        && (stagedQuery.Filter("kind").Value.Length == 0 || item.Producer.Kind.ToString() == stagedQuery.Filter("kind").Value)
        && Matches(ProducerIdentity(item.Producer), stagedQuery.Filter("producer").Value)
        && MatchesSize(item.Length, stagedQuery.Filter("size").Value)).ToArray();
    private IReadOnlyList<FlowRunArtifactResource> DurableItems => FilteredDurable.Skip(checked((durableQuery.Page - 1) * durableQuery.PageSize)).Take(durableQuery.PageSize).ToArray();
    private IReadOnlyList<StagedArtifactView> StagedItems => FilteredStaged.Skip(checked((stagedQuery.Page - 1) * stagedQuery.PageSize)).Take(stagedQuery.PageSize).ToArray();
    private CollectionPagination DurablePagination => new(durableQuery.Page, durableQuery.PageSize, durableQuery.Page * durableQuery.PageSize < FilteredDurable.Count) { Total = FilteredDurable.Count, SupportsDirectNavigation = true };
    private CollectionPagination StagedPagination => new(stagedQuery.Page, stagedQuery.PageSize, stagedQuery.Page * stagedQuery.PageSize < FilteredStaged.Count) { Total = FilteredStaged.Count, SupportsDirectNavigation = true };
    private static bool Matches(string? value, string criterion) => string.IsNullOrWhiteSpace(criterion) || value?.Contains(criterion, StringComparison.OrdinalIgnoreCase) == true;
    private static bool MatchesSize(long size, string criterion) => criterion.Length == 0 || decimal.TryParse(criterion, NumberStyles.Float, CultureInfo.InvariantCulture, out var count) && size == count;
    private static bool ValidQuery(CollectionQuery query) => query.Page >= 1 && query.PageSize is 20 or 50 or 100;
    private Task ChangeDurableQueryAsync(CollectionQuery next) { if (ValidQuery(next)) { durableQuery = next; CorrectPages(); } return Task.CompletedTask; }
    private Task ChangeStagedQueryAsync(CollectionQuery next) { if (ValidQuery(next)) { stagedQuery = next; CorrectPages(); } return Task.CompletedTask; }
    private static CollectionQuery Clamp(CollectionQuery query, int count) => query with { Page = Math.Clamp(query.Page, 1, Math.Max(1, (int)Math.Ceiling((double)count / query.PageSize))) };
    private void CorrectPages() { durableQuery = Clamp(durableQuery, FilteredDurable.Count); stagedQuery = Clamp(stagedQuery, FilteredStaged.Count); }
    private void ClearCollections() { preview?.Close(); durable = null; staged = null; }

    protected override async Task OnInitializedAsync()
    {
        await Context.EnsureLoadedAsync(cancellation.Token);
        if (disposed) return;
        Context.Changed += ContextChanged;
        contextKey = CurrentContextKey;
        initialized = true;
        await LoadAsync();
    }
    protected override void OnAfterRender(bool firstRender) { if (firstRender) { interactive = true; StateHasChanged(); } }
    private async Task LoadAsync()
    {
        if (disposed) return;
        var requestRevision = ++revision;
        pending?.Cancel();
        pending?.Dispose();
        pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var token = pending.Token;
        var scope = CurrentContextKey;
        if (scope != contextKey) { ClearCollections(); durableQuery = new(); stagedQuery = new(); contextKey = scope; }
        if (Context.Error is not null || !CanInspect)
        {
            ClearCollections();
            loadState = new(Context.Error is null ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed);
            return;
        }
        var hasContent = staged is not null && durable is not null;
        loadState = new(hasContent ? ResourceLoadStatus.Refreshing : ResourceLoadStatus.Loading) { HasContent = hasContent };
        try
        {
            var stagedTask = Client.GetStagedAsync(token);
            var durableTask = Client.GetDurableAsync(token);
            await Task.WhenAll(stagedTask, durableTask);
            if (!IsCurrent(requestRevision, scope)) return;
            staged = (await stagedTask).OrderByDescending(item => item.CreatedAt).ToArray();
            durable = (await durableTask).OrderByDescending(item => item.CreatedAt).ToArray();
            CorrectPages();
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
            var forbidden = status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            if (forbidden) ClearCollections();
            loadState = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed) { HasContent = staged is not null && durable is not null, ErrorId = (error as AgentstrationApiException)?.ErrorId };
        }
    }
    private bool IsCurrent(long requestRevision, string scope) => !disposed && revision == requestRevision && CurrentContextKey == scope;
    private async Task RetryAsync() { if (Context.Error is not null) await Context.LoadAsync(cancellation.Token); await LoadAsync(); }
    private void ContextChanged()
    {
        if (!initialized || disposed) return;
        _ = InvokeAsync(async () =>
        {
            if (disposed) return;
            if (CurrentContextKey != contextKey || Context.Error is not null || !CanInspect || loadState.Status == ResourceLoadStatus.Forbidden)
            {
                var loading = LoadAsync();
                StateHasChanged();
                await loading;
            }
            StateHasChanged();
        });
    }
    public void Dispose() { disposed = true; revision++; Context.Changed -= ContextChanged; pending?.Cancel(); pending?.Dispose(); cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this); }
}
