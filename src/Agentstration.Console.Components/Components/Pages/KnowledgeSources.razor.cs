using System.Net;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Components.Pages;

public partial class KnowledgeSources
{
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? pending;
    private IReadOnlyList<KnowledgeSourceResource>? items;
    private CollectionQuery query = new();
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? contextKey;
    private long revision;
    private bool initialized;
    private bool disposed;
    private bool interactive;
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}";

    // The existing endpoint returns the complete authorized catalog in one array. No additional reads are introduced.
    private IReadOnlyList<KnowledgeSourceResource> Filtered => (items ?? []).Where(source =>
        (string.IsNullOrWhiteSpace(query.Search) || source.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase) || source.Definition.DisplayName.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
        && (query.Filter("enabled").Value.Length == 0 || source.Definition.Enabled.ToString().Equals(query.Filter("enabled").Value, StringComparison.OrdinalIgnoreCase))
        && (query.Filter("inputs").Value.Length == 0 || source.Definition.DataSources.Any(binding => binding.DataSource.Name.Contains(query.Filter("inputs").Value, StringComparison.OrdinalIgnoreCase)))
        && (query.Filter("projection").Value.Length == 0 || source.Definition.ProjectionFlow is { } projection && Flow(source, projection).Contains(query.Filter("projection").Value, StringComparison.OrdinalIgnoreCase))
        && (query.Filter("retrieval").Value.Length == 0 || source.Definition.RetrievalFlow is { } retrieval && Flow(source, retrieval).Contains(query.Filter("retrieval").Value, StringComparison.OrdinalIgnoreCase))
        && (query.Filter("namespace").Value.Length == 0 || source.Namespace.Value == query.Filter("namespace").Value)
        && (query.Filter("scope").Value.Length == 0 || source.ScopeRef?.Value == query.Filter("scope").Value)).ToArray();
    private IReadOnlyList<KnowledgeSourceResource> PageItems => Filtered.Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToArray();
    private CollectionPagination Pagination => new(query.Page, query.PageSize, query.Page * query.PageSize < Filtered.Count)
    {
        Total = Filtered.Count,
        SupportsDirectNavigation = true
    };

    protected override async Task OnInitializedAsync()
    {
        Context.Changed += ContextChanged;
        await Context.EnsureLoadedAsync(cancellation.Token);
        if (disposed) return;
        contextKey = CurrentContextKey;
        initialized = true;
        await LoadAsync();
    }

    protected override void OnAfterRender(bool firstRender) { if (firstRender) { interactive = true; StateHasChanged(); } }

    private Task ChangeQueryAsync(CollectionQuery next)
    {
        if (next.PageSize is not (20 or 50 or 100) || next.Page < 1) return Task.CompletedTask;
        query = next;
        CorrectPage();
        return Task.CompletedTask;
    }

    private void CorrectPage()
    {
        var page = Math.Clamp(query.Page, 1, Math.Max(1, (int)Math.Ceiling((double)Filtered.Count / query.PageSize)));
        if (page != query.Page) query = query with { Page = page };
    }

    private async Task LoadAsync()
    {
        if (disposed) return;
        var requestRevision = ++revision;
        pending?.Cancel();
        pending?.Dispose();
        pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var token = pending.Token;
        var scope = CurrentContextKey;
        if (scope != contextKey) { preview?.Close(); items = null; query = new(); contextKey = scope; }
        if (Context.Error is not null || !Context.HasPermission(AuthorizationPermissions.ResourcesRead))
        {
            preview?.Close();
            items = null;
            loadState = new(Context.Error is null ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed);
            return;
        }
        loadState = new(items is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = items is not null };
        try
        {
            var result = await Client.GetAsync(token);
            if (!IsCurrent(requestRevision, scope)) return;
            items = result;
            CorrectPage();
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
            var forbidden = status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            if (forbidden) { preview?.Close(); items = null; }
            loadState = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed)
            {
                HasContent = items is not null,
                ErrorId = (error as AgentstrationApiException)?.ErrorId
            };
        }
    }

    private bool IsCurrent(long requestRevision, string? scope) => !disposed && revision == requestRevision && CurrentContextKey == scope;
    private async Task RetryAsync() { if (Context.Error is not null) await Context.LoadAsync(cancellation.Token); await LoadAsync(); }
    private void ContextChanged()
    {
        if (!initialized || disposed) return;
        _ = InvokeAsync(async () =>
        {
            if (disposed) return;
            if (CurrentContextKey != contextKey || Context.Error is not null || !Context.HasPermission(AuthorizationPermissions.ResourcesRead) || loadState.Status == ResourceLoadStatus.Forbidden)
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
