using System.Net;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Tools;
using Agentstration.Web.Console;

namespace Agentstration.Web.Components.Pages;

public partial class Tools
{
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? pending;
    private IReadOnlyList<ToolResource>? items;
    private IReadOnlyList<ExtensionInventoryItemResponse> extensions = [];
    private IReadOnlyList<ToolProviderResource> providers = [];
    private bool interactive;
    private CollectionQuery query = new();
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? contextKey;
    private long revision;
    private bool initialized;
    private bool disposed;
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}|{Context.Current?.WorkspaceId:D}";

    // The existing endpoint returns the complete authorized catalog in one array. No additional reads are introduced.
    private IReadOnlyList<ToolResource> Filtered => (items ?? []).Where(tool =>
        (string.IsNullOrWhiteSpace(query.Search) || new[] { tool.Name, tool.Namespace.Value, tool.Definition.DisplayName, tool.Definition.ExternalId, ProviderDisplayName(tool) }.Any(value => value?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) == true))
        && (query.Filter("availability").Value.Length == 0 || (tool.Definition.Discovery?.Available == true) == (query.Filter("availability").Value == "available"))
        && (query.Filter("enabled").Value.Length == 0 || tool.Definition.Enabled.ToString().Equals(query.Filter("enabled").Value, StringComparison.OrdinalIgnoreCase))
        && (query.Filter("namespace").Value.Length == 0 || tool.Namespace.Value == query.Filter("namespace").Value)
        && (query.Filter("scope").Value.Length == 0 || tool.ScopeRef?.Value == query.Filter("scope").Value)).ToArray();
    private IReadOnlyList<ToolResource> PageItems => Filtered.Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToArray();
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
        if (scope != contextKey) { items = null; providers = []; extensions = []; query = new(); contextKey = scope; }
        if (Context.Error is not null || !Context.HasPermission(AuthorizationPermissions.ResourcesRead))
        {
            items = null;
            providers = [];
            extensions = [];
            loadState = new(Context.Error is null ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed);
            return;
        }
        loadState = new(items is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = items is not null };
        try
        {
            var toolsTask = Client.GetToolsAsync(cancellationToken: token);
            var providersTask = Client.GetProvidersAsync(token);
            await Task.WhenAll(toolsTask, providersTask);
            var result = await toolsTask;
            var providerCatalog = await providersTask;
            if (!IsCurrent(requestRevision, scope)) return;
            var inventory = extensions;
            if (Services.GetService(typeof(IExtensionsClient)) is IExtensionsClient client)
            {
                try { inventory = await client.GetExtensionInventoryAsync(token); }
                catch (Exception error) when (error is HttpRequestException or AgentstrationApiException)
                {
                    // Provenance is optional. Never keep inventory after an explicit authorization denial.
                    if (IsForbidden(error)) inventory = [];
                }
            }
            if (!IsCurrent(requestRevision, scope)) return;
            items = result;
            providers = providerCatalog;
            extensions = inventory;
            CorrectPage();
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            var forbidden = IsForbidden(error);
            if (forbidden) { items = null; providers = []; extensions = []; }
            loadState = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed)
            {
                HasContent = items is not null,
                ErrorId = (error as AgentstrationApiException)?.ErrorId
            };
        }
    }

    private static bool IsForbidden(Exception error)
    {
        var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
        return status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
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
