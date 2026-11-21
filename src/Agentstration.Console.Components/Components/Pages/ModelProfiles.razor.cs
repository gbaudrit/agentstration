using System.Globalization;
using System.Net;
using Agentstration.Identity.Contracts;
using Agentstration.Models.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Console;

namespace Agentstration.Web.Components.Pages;

public partial class ModelProfiles
{
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? pending;
    private IReadOnlyList<ModelProfileSummaryResponse>? items;
    private CollectionQuery query = new();
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? contextKey;
    private long revision;
    private bool initialized;
    private bool disposed;
    private bool interactive;
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}";

    // ListAsync uses ListAllAsync; the API returns the complete authorized ValueResponse.
    // Criteria apply to that one snapshot, not a bounded server page or per-row resolutions.
    private IReadOnlyList<ModelProfileSummaryResponse> Filtered => ModelManagementUi.FilterProfiles(items ?? [], query.Search).Where(profile =>
        (query.Filter("status").Value.Length == 0 || StatusKey(profile.Properties.Status) == query.Filter("status").Value)
        && (query.Filter("provider").Value.Length == 0 || profile.Properties.Provider.Name == query.Filter("provider").Value)
        && (query.Filter("model").Value.Length == 0 || profile.Properties.Model.Name.Contains(query.Filter("model").Value, StringComparison.OrdinalIgnoreCase))
        && (query.Filter("usage").Value.Length == 0 || int.TryParse(query.Filter("usage").Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var usage) && profile.Properties.UsageCount == usage)
        && (query.Filter("namespace").Value.Length == 0 || profile.Namespace == query.Filter("namespace").Value)
        && (query.Filter("scope").Value.Length == 0 || profile.ScopeRef?.Value == query.Filter("scope").Value)).ToArray();
    private IReadOnlyList<ModelProfileSummaryResponse> PageItems => Filtered.Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToArray();
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
        if (scope != contextKey) { ClearOperations(); items = null; query = new(); contextKey = scope; }
        if (Context.Error is not null || !Context.HasPermission(AuthorizationPermissions.ResourcesRead))
        {
            ClearOperations();
            items = null;
            loadState = new(Context.Error is null ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed);
            return;
        }
        loadState = new(items is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = items is not null };
        try
        {
            var result = await Client.GetModelProfilesAsync(null, null, null, token);
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
            if (forbidden) { ClearOperations(); items = null; }
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
            if (!Context.HasPermission(AuthorizationPermissions.ResourcesDelete)) ClearOperations();
            if (CurrentContextKey != contextKey || Context.Error is not null || !Context.HasPermission(AuthorizationPermissions.ResourcesRead) || loadState.Status == ResourceLoadStatus.Forbidden)
            {
                var loading = LoadAsync();
                StateHasChanged();
                await loading;
            }
            StateHasChanged();
        });
    }
    public void Dispose() { disposed = true; ClearOperations(); revision++; Context.Changed -= ContextChanged; pending?.Cancel(); pending?.Dispose(); cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this); }
    private CancellationTokenSource? operation;
    private long operationRevision;
    private bool operationBusy;
    private bool operationFailed;
    private string? operationErrorId;
    private ModelProfileSummaryResponse? pendingDelete;
    private IReadOnlyList<ModelProfileUsageResponse> deleteConflictUsages = [];
    private bool CanDelete(ModelProfileSummaryResponse profile) => !profile.IsManaged
        && Context.HasPermission(AuthorizationPermissions.ResourcesRead)
        && Context.HasPermission(AuthorizationPermissions.ResourcesDelete)
        && profile.ScopeRef is { } scope && scope.Value == Context.Current?.SelectedScopeRef;
    private bool IsCurrentOperation(long version, string scope, ModelProfileSummaryResponse profile) =>
        !disposed && operationRevision == version && CurrentContextKey == scope && CanDelete(profile);
    private void ClearOperations()
    {
        operationRevision++;
        operation?.Cancel();
        operation?.Dispose();
        operation = null;
        operationBusy = false;
        operationFailed = false;
        operationErrorId = null;
        pendingDelete = null;
        deleteConflictUsages = [];
    }
    private async Task RunOperationAsync(ModelProfileSummaryResponse profile, Func<CancellationToken, long, string, Task> action)
    {
        if (disposed || operationBusy || !CanDelete(profile)) return;
        operation?.Dispose();
        operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var token = operation.Token;
        var version = ++operationRevision;
        var scope = CurrentContextKey;
        operationBusy = true;
        operationFailed = false;
        operationErrorId = null;
        try { await action(token, version, scope); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrentOperation(version, scope, profile)) return;
            operationFailed = true;
            operationErrorId = (error as AgentstrationApiException)?.ErrorId;
        }
        finally { if (operationRevision == version) operationBusy = false; }
    }
    private Task RequestDeleteAsync(ModelProfileSummaryResponse profile) => RunOperationAsync(profile, async (token, version, context) =>
    {
        pendingDelete = null;
        deleteConflictUsages = [];
        var usages = await Client.GetModelProfileUsagesAsync(ResourceNamespace.Parse(profile.Namespace), profile.Name, profile.ScopeRef!.Value, token);
        if (!IsCurrentOperation(version, context, profile)) return;
        if (usages.Count > 0) { deleteConflictUsages = usages.Value; return; }
        pendingDelete = profile;
    });
    private void CancelDelete() => pendingDelete = null;
    private async Task DeleteAsync()
    {
        if (pendingDelete is not { } selected) return;
        pendingDelete = null;
        await RunOperationAsync(selected, async (token, version, context) =>
        {
            var ns = ResourceNamespace.Parse(selected.Namespace);
            var owner = selected.ScopeRef!.Value;
            var snapshot = await Client.GetModelProfileAsync(ns, selected.Name, owner, token);
            if (!IsCurrentOperation(version, context, selected)) return;
            try { await Client.DeleteModelProfileAsync(ns, selected.Name, owner, snapshot.ETag, token); }
            catch (AgentstrationApiException error) when (error.StatusCode == HttpStatusCode.Conflict)
            {
                try
                {
                    var usages = await Client.GetModelProfileUsagesAsync(ns, selected.Name, owner, token);
                    if (IsCurrentOperation(version, context, selected)) deleteConflictUsages = usages.Value;
                }
                catch (Exception failure) when (failure is AgentstrationApiException or HttpRequestException or OperationCanceledException) { }
                throw;
            }
            if (!IsCurrentOperation(version, context, selected)) return;
            items = items?.Where(profile => profile.Name != selected.Name || profile.Namespace != selected.Namespace || profile.ScopeRef != selected.ScopeRef).ToArray();
            CorrectPage();
            Notifications.Add(new NotificationItem(Guid.NewGuid(), T("DeletedNotification"), selected.Name, DateTimeOffset.Now, UiStatus.Success));
            await LoadAsync();
        });
    }

}
