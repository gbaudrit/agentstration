using System.Globalization;
using System.Net;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Console;

namespace Agentstration.Web.Components.Pages;

public partial class Agents
{
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? pending;
    private IReadOnlyList<AgentSummary>? items;
    private AgentstrationApiException? error;
    private AgentSummary? pendingDelete;
    private CollectionQuery query = new();
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? contextKey;
    private long revision;
    private bool initialized, disposed, interactive, deleting;
    private int loadedPage = 1, loadedSize = 20;
    private bool hasMore, deploymentWindowComplete = true;
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}|{Context.Current?.TenantId:D}|{Context.Current?.WorkspaceId:D}";
    private bool CanCreate => items is not null && Context.HasPermission(AuthorizationPermissions.ResourcesWrite);
    private string CreateUrl => ResourceOwnershipContext.PreserveNavigation("/agents/new", Services);
    private CollectionPagination Pagination => new(loadedPage, loadedSize, hasMore);
    private string DeleteMessage => pendingDelete is null ? string.Empty : F("DeleteMessage", pendingDelete.Name);
    protected override async Task OnInitializedAsync()
    {
        Context.Changed += ContextChanged;
        await Context.EnsureLoadedAsync(cancellation.Token);
        if (disposed) return;
        contextKey = CurrentContextKey; initialized = true;
        await LoadAsync();
    }
    protected override void OnAfterRender(bool firstRender) { if (firstRender) { interactive = true; StateHasChanged(); } }
    private async Task ChangeQueryAsync(CollectionQuery next)
    {
        if (next.Page < 1 || next.PageSize is not (20 or 50 or 100) || next.HasFilters) return;
        query = next;
        await LoadAsync();
    }
    private async Task LoadAsync()
    {
        if (disposed) return;
        var requestRevision = ++revision;
        pending?.Cancel(); pending?.Dispose();
        pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var token = pending.Token;
        var scope = CurrentContextKey;
        if (scope != contextKey) { ClearCatalog(); query = new(); contextKey = scope; }
        if (Context.Error is not null || !Context.HasPermission(AuthorizationPermissions.ResourcesRead))
        {
            ClearCatalog(); loadState = new(Context.Error is null ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed); return;
        }
        error = null;
        loadState = new(items is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = items is not null };
        try
        {
            var requested = query;
            var result = await Client.GetAgentsPageAsync(checked((requested.Page - 1) * requested.PageSize), requested.PageSize, token);
            if (!IsCurrent(requestRevision, scope)) return;
            items = result.Items; loadedPage = requested.Page; loadedSize = requested.PageSize; hasMore = result.HasMore; deploymentWindowComplete = result.DeploymentWindowComplete;
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            var status = exception is AgentstrationApiException api ? api.StatusCode : (exception as HttpRequestException)?.StatusCode;
            var forbidden = status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            if (forbidden) ClearCatalog();
            loadState = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed) { HasContent = items is not null, ErrorId = (exception as AgentstrationApiException)?.ErrorId };
        }
    }
    private bool IsCurrent(long requestRevision, string scope) => !disposed && revision == requestRevision && CurrentContextKey == scope;
    private async Task RetryAsync() { if (Context.Error is not null) await Context.LoadAsync(cancellation.Token); await LoadAsync(); }
    private void ClearCatalog() { items = null; pendingDelete = null; preview?.Close(); error = null; hasMore = false; loadedPage = 1; loadedSize = 20; deploymentWindowComplete = true; }
    private void ContextChanged()
    {
        if (!initialized || disposed) return;
        _ = InvokeAsync(async () => { if (disposed) return; ClearCatalog(); var loadingTask = LoadAsync(); StateHasChanged(); await loadingTask; StateHasChanged(); });
    }
    private bool CanDelete(AgentSummary item) => Context.HasPermission(AuthorizationPermissions.ResourcesDelete) && item.Namespace.IsDefault && !item.IsManaged && ResourceOwnershipContext.CanManage(item.ScopeRef, Services);
    private void RequestDelete(AgentSummary item) { if (!deleting && CanDelete(item)) pendingDelete = item; }
    private void CancelDelete() => pendingDelete = null;
    private async Task DeleteAsync()
    {
        if (pendingDelete is not { } deleted || deleting || !CanDelete(deleted)) return;
        var requestRevision = revision;
        var scope = CurrentContextKey;
        var token = pending?.Token ?? cancellation.Token;
        pendingDelete = null; deleting = true;
        try
        {
            var snapshot = deleted.ScopeRef is { } owner
                ? await Client.GetAgentAsync(deleted.Namespace, deleted.Id, owner, token)
                : await Client.GetAgentAsync(deleted.Namespace, deleted.Id, token);
            if (!IsCurrent(requestRevision, scope) || !CanDelete(deleted)) return;
            if (snapshot.Value.Governance.IsManaged) { await LoadAsync(); return; }
            if (deleted.ScopeRef is { } exact)
                await Client.DeleteAgentAsync(deleted.Namespace, deleted.Id, exact, snapshot.ETag, token);
            else await Client.DeleteAgentAsync(deleted.Id, snapshot.ETag, token);
            if (!IsCurrent(requestRevision, scope)) return;
            Notifications.Add(new NotificationItem(Guid.NewGuid(), T("AgentDeleted"), deleted.Name, DateTimeOffset.Now, UiStatus.Success));
            await LoadAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (AgentstrationApiException exception)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            if (exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) { ClearCatalog(); loadState = new(ResourceLoadStatus.Forbidden); }
            else error = exception;
            Notifications.Add(new NotificationItem(Guid.NewGuid(), T("DeleteFailed"), exception.Message, DateTimeOffset.Now, UiStatus.Danger));
        }
        finally { if (!disposed) deleting = false; }
    }
    private string EditUrl(AgentSummary item) => ResourceOwnershipContext.PreserveNavigation(item.DetailsUrl, Services);
    private string OverviewUrl(AgentSummary item) => EditUrl(item).Split('#')[0] + "#overview";
    private string DefinitionUrl(AgentSummary item) => EditUrl(item).Split('#')[0] + "#definition/configuration";
    private string RunUrl(AgentSummary item)
    {
        var url = EditUrl(item); var query = url.IndexOf('?');
        return query < 0 ? $"{url}/run" : $"{url[..query]}/run{url[query..]}";
    }
    private string ModelProfileUrl(AgentSummary item) => ResourceOwnershipContext.PreserveNavigation(ConsoleResourceUrls.ModelProfile(item.ModelProfileAddress, item.ModelProfileScopeRef), Services);
    private static string ResourceName(string resourceId) => resourceId;
    private string LastActivity(DateTimeOffset value) => value == DateTimeOffset.MinValue ? T("List.NoDeploymentObserved") : value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    private string StatusLabel(string value) => value switch { "Accepted" => T("StatusValid"), "Succeeded" => T("StatusPublished"), "Failed" => T("StatusFailed"), "Unknown" or "" => T("StatusUnknown"), _ => value };
    private string DeploymentStatusLabel(string value) => value == "Unknown" ? T("List.DeploymentUnknown") : string.Equals(value, "Not deployed", StringComparison.OrdinalIgnoreCase)
        ? DeploymentLocalizer["Status.NotDeployed"]
        : DeploymentLocalizer[$"Status.{value}"];
    private string T(string key) => Localizer[key];
    private string F(string key, params object[] arguments) => Localizer[key, arguments];
    private string DeploymentUrl(AgentSummary item) => ResourceOwnershipContext.PreserveNavigation(ConsoleResourceUrls.WithOwnerScope(item.DeploymentUrl!, item.ScopeRef), Services);
    public void Dispose() { disposed = true; revision++; Context.Changed -= ContextChanged; pending?.Cancel(); pending?.Dispose(); cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this); }
}
