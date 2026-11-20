using System.Net;
using Agentstration.Functions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;

namespace Agentstration.Web.Components.Pages;

public partial class TransformationFunctions
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Catalog<TransformationFunctionResource> functions = new();
    private readonly Catalog<FunctionKindResource> kinds = new();
    private readonly HashSet<string> expandedKinds = [];
    private string? contextKey;
    private bool initialized, disposed, interactive, savingKind;
    private AgentstrationApiException? operationError;
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}|{Context.Current?.TenantId:D}|{Context.Current?.WorkspaceId:D}";
    private ResourceScopeRef? Owner => ResourceOwnershipContext.TryGetCurrent(Services);
    private bool CanCreate => functions.Items is not null && Context.HasPermission(AuthorizationPermissions.ResourcesWrite);
    private static string KindKey(FunctionKindResource kind) => $"{kind.ScopeRef}|{kind.Namespace}|{kind.Name}";
    private void ToggleKindDetails(FunctionKindResource kind) { if (!expandedKinds.Add(KindKey(kind))) expandedKinds.Remove(KindKey(kind)); }
    protected override async Task OnInitializedAsync()
    {
        Context.Changed += ContextChanged;
        await Context.EnsureLoadedAsync(cancellation.Token);
        if (disposed) return;
        contextKey = CurrentContextKey; initialized = true;
        await LoadBothAsync();
    }
    protected override void OnAfterRender(bool firstRender) { if (firstRender) { interactive = true; StateHasChanged(); } }
    private Task LoadBothAsync() => Task.WhenAll(LoadFunctionsAsync(), LoadKindsAsync());
    private Task LoadFunctionsAsync() => LoadAsync(functions, (skip, take, ct) => Client.ListPageAsync(Owner, skip, take, ct));
    private Task LoadKindsAsync() { operationError = null; return LoadAsync(kinds, (skip, take, ct) => Client.ListKindsPageAsync(Owner, skip, take, ct)); }
    private async Task RetryFunctionsAsync() { if (Context.Error is not null) await Context.LoadAsync(cancellation.Token); await LoadFunctionsAsync(); }
    private async Task RetryKindsAsync() { if (Context.Error is not null) await Context.LoadAsync(cancellation.Token); await LoadKindsAsync(); }
    private Task ChangeFunctionsAsync(CollectionQuery next) => ChangeAsync(functions, next, LoadFunctionsAsync);
    private Task ChangeKindsAsync(CollectionQuery next) => ChangeAsync(kinds, next, LoadKindsAsync);
    private static Task ChangeAsync<T>(Catalog<T> catalog, CollectionQuery next, Func<Task> load)
    {
        if (next.Page < 1 || next.PageSize is not (20 or 50) || next.HasFilters || (long)(next.Page - 1) * next.PageSize > 1_000_000) return Task.CompletedTask;
        catalog.Query = next;
        return load();
    }
    private async Task LoadAsync<T>(Catalog<T> catalog, Func<int, int, CancellationToken, Task<FunctionCatalogPage<T>>> read)
    {
        if (disposed) return;
        var requestRevision = ++catalog.Revision;
        catalog.Pending?.Cancel(); catalog.Pending?.Dispose();
        catalog.Pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var token = catalog.Pending.Token;
        var scope = CurrentContextKey;
        if (Context.Error is not null || !Context.HasPermission(AuthorizationPermissions.ResourcesRead))
        {
            catalog.Clear(); if (ReferenceEquals(catalog, kinds)) expandedKinds.Clear();
            catalog.LoadState = new(Context.Error is null ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed); return;
        }
        catalog.LoadState = new(catalog.Items is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = catalog.Items is not null };
        try
        {
            var requested = catalog.Query;
            var result = await read(checked((requested.Page - 1) * requested.PageSize), requested.PageSize, token);
            if (!IsCurrent(catalog, requestRevision, scope)) return;
            catalog.Items = result.Items; catalog.LoadedPage = requested.Page; catalog.LoadedSize = requested.PageSize; catalog.HasMore = result.HasMore;
            catalog.LoadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(catalog, requestRevision, scope)) return;
            var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
            var forbidden = status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            if (forbidden) { catalog.Clear(); if (ReferenceEquals(catalog, kinds)) expandedKinds.Clear(); }
            catalog.LoadState = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed) { HasContent = catalog.Items is not null, ErrorId = (error as AgentstrationApiException)?.ErrorId };
        }
    }
    private bool IsCurrent<T>(Catalog<T> catalog, long requestRevision, string scope) => !disposed && catalog.Revision == requestRevision && CurrentContextKey == scope;
    private void ContextChanged()
    {
        if (!initialized || disposed) return;
        _ = InvokeAsync(async () =>
        {
            if (disposed) return;
            functions.Clear(); kinds.Clear(); expandedKinds.Clear(); operationError = null;
            if (contextKey != CurrentContextKey) { functions.Query = new(); kinds.Query = new(); contextKey = CurrentContextKey; }
            var loading = LoadBothAsync(); StateHasChanged(); await loading; StateHasChanged();
        });
    }
    private bool CanToggle(FunctionKindResource kind) => !kind.Governance.IsManaged && ResourceOwnershipContext.CanManage(kind.ScopeRef, Services) && Context.HasPermission(AuthorizationPermissions.ResourcesWrite);
    private async Task ToggleKindAsync(FunctionKindResource kind)
    {
        if (savingKind || !CanToggle(kind)) return;
        var requestRevision = kinds.Revision;
        var scope = CurrentContextKey;
        var token = kinds.Pending?.Token ?? cancellation.Token;
        savingKind = true; operationError = null;
        try
        {
            var snapshot = await Client.GetKindAsync(new(kind.Name, "", kind.ScopeRef, kind.Namespace), token);
            if (!IsCurrent(kinds, requestRevision, scope) || !CanToggle(snapshot.Value) || KindKey(snapshot.Value) != KindKey(kind)) return;
            await Client.SaveKindAsync(snapshot.Value with { Definition = snapshot.Value.Definition with { Enabled = !snapshot.Value.Definition.Enabled } }, snapshot.ETag, token);
            if (IsCurrent(kinds, requestRevision, scope)) await LoadBothAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (AgentstrationApiException error)
        {
            if (!IsCurrent(kinds, requestRevision, scope)) return;
            if (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) { kinds.Clear(); expandedKinds.Clear(); kinds.LoadState = new(ResourceLoadStatus.Forbidden); }
            else operationError = error;
        }
        finally { if (!disposed) savingKind = false; }
    }
    private string ScopedUri(string uri) => ResourceOwnershipContext.PreserveNavigation(uri, Services);
    private string Url(TransformationFunctionResource resource) => ScopedUri("/transformation-functions/" + Uri.EscapeDataString(resource.Name)
        + "?scopeRef=" + Uri.EscapeDataString(resource.ScopeRef!.Value.Value) + "&namespace=" + Uri.EscapeDataString(resource.Namespace.Value));
    public void Dispose() { disposed = true; Context.Changed -= ContextChanged; functions.DisposePending(); kinds.DisposePending(); cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this); }
    private sealed class Catalog<T>
    {
        public IReadOnlyList<T>? Items { get; set; }
        public CollectionQuery Query { get; set; } = new();
        public ResourceLoadState LoadState { get; set; } = new(ResourceLoadStatus.Loading);
        public int LoadedPage { get; set; } = 1;
        public int LoadedSize { get; set; } = 20;
        public bool HasMore { get; set; }
        public long Revision { get; set; }
        public CancellationTokenSource? Pending { get; set; }
        public CollectionPagination Pagination => new(LoadedPage, LoadedSize, HasMore) { PageSizes = [20, 50] };
        public void Clear() { Items = null; LoadedPage = 1; LoadedSize = 20; HasMore = false; }
        public void DisposePending() { Revision++; Pending?.Cancel(); Pending?.Dispose(); }
    }
}
