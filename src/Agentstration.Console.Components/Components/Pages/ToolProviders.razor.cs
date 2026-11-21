using System.Globalization;
using System.Net;
using Agentstration.Identity.Contracts;
using Agentstration.Tools;
using Agentstration.Web.Console;

namespace Agentstration.Web.Components.Pages;

public partial class ToolProviders
{
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? pending;
    private IReadOnlyList<ToolProviderResource>? providers;
    private CollectionQuery query = new();
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? contextKey;
    private long revision;
    private bool initialized;
    private bool disposed;
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}";

    // The existing endpoint returns the complete authorized catalog in one array. No additional reads are introduced.
    private IReadOnlyList<ToolProviderResource> Filtered => (providers ?? []).Where(provider =>
        (string.IsNullOrWhiteSpace(query.Search) || provider.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || provider.Definition.DisplayName.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
        && (query.Filter("status").Value.Length == 0 || StatusValue(provider.Definition.Discovery.Status) == query.Filter("status").Value)
        && (query.Filter("type").Value.Length == 0 || provider.Definition.ProviderType.ToString() == query.Filter("type").Value)
        && (query.Filter("enabled").Value.Length == 0 || (provider.Definition.Enabled ? "true" : "false") == query.Filter("enabled").Value)
        && (query.Filter("tools").Value.Length == 0 || decimal.TryParse(query.Filter("tools").Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var count) && provider.Definition.Discovery.ToolCount == count)
        && (query.Filter("namespace").Value.Length == 0 || provider.Namespace.Value == query.Filter("namespace").Value)).ToArray();
    private IReadOnlyList<ToolProviderResource> PageItems => Filtered.Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToArray();
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

    private Task ChangeQueryAsync(CollectionQuery next)
    {
        if (next.PageSize is not (20 or 50 or 100) || next.Page < 1) return Task.CompletedTask;
        query = next;
        CorrectPage();
        return Task.CompletedTask;
    }

    private void CorrectPage() => query = query with { Page = Math.Clamp(query.Page, 1, Math.Max(1, (int)Math.Ceiling((double)Filtered.Count / query.PageSize))) };

    private async Task LoadAsync()
    {
        if (disposed) return;
        var requestRevision = ++revision;
        pending?.Cancel();
        pending?.Dispose();
        pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var token = pending.Token;
        var scope = CurrentContextKey;
        if (scope != contextKey) { preview?.Close(); providers = null; query = new(); contextKey = scope; }
        if (Context.Error is not null || !Context.HasPermission(AuthorizationPermissions.ResourcesRead))
        {
            providers = null;
            loadState = new(Context.Error is null ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed);
            return;
        }
        loadState = new(providers is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = providers is not null };
        try
        {
            var result = await Client.GetProvidersAsync(token);
            if (!IsCurrent(requestRevision, scope)) return;
            providers = result;
            CorrectPage();
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
            var forbidden = status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            if (forbidden) providers = null;
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
