using System.Net;
using Agentstration.Sources.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Components.Pages;

public partial class SourceRegistryCollection
{
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? pending;
    private IReadOnlyList<SourceRegistryRegistrationView>? registries;
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? contextKey;
    private long revision;
    private bool initialized;
    private bool disposed;
    private bool interactive;
    private bool busy;
    private bool hasCatalogAuthorization;
    private AgentstrationApiException? operationError;
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}|{Context.Current?.TenantId:D}|{Context.Current?.WorkspaceId:D}";

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

    private async Task LoadAsync()
    {
        if (disposed) return;
        operationError = null;
        var requestRevision = ++revision;
        pending?.Cancel();
        pending?.Dispose();
        pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var token = pending.Token;
        var scope = CurrentContextKey;
        if (scope != contextKey) { registries = null; hasCatalogAuthorization = false; contextKey = scope; }
        if (Context.Error is not null)
        {
            registries = null; hasCatalogAuthorization = false;
            loadState = new(ResourceLoadStatus.Failed);
            return;
        }
        loadState = new(registries is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = registries is not null };
        try
        {
            var result = await Client.GetRegistriesAsync(token);
            if (!IsCurrent(requestRevision, scope)) return;
            registries = result;
            hasCatalogAuthorization = true;
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
            var forbidden = status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            if (forbidden) { registries = null; hasCatalogAuthorization = false; }
            loadState = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed)
            {
                HasContent = registries is not null,
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
            // The API rechecks PlatformAdmin after every context/role notification.
            operationError = null;
            registries = null; hasCatalogAuthorization = false;
            var loading = LoadAsync();
            StateHasChanged();
            await loading;
            StateHasChanged();
        });
    }
    private Task ToggleAsync(SourceRegistryRegistrationView item) => RunActionAsync(async token =>
    {
        var current = await Client.GetRegistryAsync(item.Registration.Name, token);
        token.ThrowIfCancellationRequested();
        await Client.UpdateRegistryAsync(item.Registration.Name, new(current.Value.Registration.Definition with { Enabled = !current.Value.Registration.Definition.Enabled }), current.ETag, token);
    });
    private Task RefreshAsync(SourceRegistryRegistrationView item) => RunActionAsync(async token => { await Client.RefreshRegistryAsync(item.Registration.Name, token); });
    private async Task RunActionAsync(Func<CancellationToken, Task> action)
    {
        if (busy || !hasCatalogAuthorization || disposed) return;
        var requestRevision = revision;
        var scope = CurrentContextKey;
        var token = pending?.Token ?? cancellation.Token;
        busy = true;
        operationError = null;
        try { await action(token); }
        catch (AgentstrationApiException error)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            operationError = error;
            if (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) { registries = null; hasCatalogAuthorization = false; loadState = new(ResourceLoadStatus.Forbidden); return; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        finally { if (!disposed) busy = false; }
        if (IsCurrent(requestRevision, scope))
        {
            var reportedError = operationError;
            await LoadAsync();
            if (!disposed && CurrentContextKey == scope) operationError = reportedError;
        }
    }
    public void Dispose() { disposed = true; revision++; Context.Changed -= ContextChanged; pending?.Cancel(); pending?.Dispose(); cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this); }
}
