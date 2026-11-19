using System.Net;
using Agentstration.Sources.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Components.Pages;

public partial class SourceProviders
{
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? pending;
    private IReadOnlyList<SourceProviderSummaryResponse>? providers;
    private CollectionQuery query = new();
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? contextKey;
    private long revision;
    private bool initialized;
    private bool disposed;
    private bool interactive;
    private bool hasCatalogAuthorization;
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}";

    // The existing endpoint returns the complete authorized catalog in one array. No additional reads are introduced.
    private IReadOnlyList<SourceProviderSummaryResponse> Filtered => (providers ?? []).Where(provider =>
        (string.IsNullOrWhiteSpace(query.Search) || provider.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase) || provider.DisplayName.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
        && (query.Filter("status").Value.Length == 0 || StatusValue(provider.Status) == query.Filter("status").Value)
        && (query.Filter("extension").Value.Length == 0 || Extension(provider) == query.Filter("extension").Value)
        && (query.Filter("contribution").Value.Length == 0 || provider.ContributionId == query.Filter("contribution").Value)
        && (query.Filter("namespace").Value.Length == 0 || provider.Namespace == query.Filter("namespace").Value)).ToArray();
    private IReadOnlyList<SourceProviderSummaryResponse> PageItems => Filtered.Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToArray();
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
        if (scope != contextKey) { providers = null; hasCatalogAuthorization = false; query = new(); contextKey = scope; }
        if (Context.Error is not null || Context.Current is null)
        {
            providers = null;
            hasCatalogAuthorization = false;
            loadState = new(Context.Error is null ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed);
            return;
        }
        loadState = new(providers is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = providers is not null };
        try
        {
            var result = await Client.GetSourceProvidersAsync(token);
            if (!IsCurrent(requestRevision, scope)) return;
            providers = result;
            hasCatalogAuthorization = true;
            CorrectPage();
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
            var forbidden = status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            if (forbidden) { providers = null; hasCatalogAuthorization = false; }
            loadState = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed)
            {
                HasContent = providers is not null,
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
            // PlatformAdmin is checked by the API, including after a role/context snapshot changes.
            providers = null;
            hasCatalogAuthorization = false;
            var loading = LoadAsync();
            StateHasChanged();
            await loading;
            StateHasChanged();
        });
    }
    public void Dispose() { disposed = true; revision++; Context.Changed -= ContextChanged; pending?.Cancel(); pending?.Dispose(); cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this); }
}
