using System.Net;
using Agentstration.Security.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Components.Pages;

public partial class OrganizationSecurityAudit
{
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? pending;
    private IReadOnlyList<SecurityAuditEvent>? items;
    private CollectionQuery query = new();
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? contextKey;
    private long revision;
    private bool initialized;
    private bool disposed;
    private bool interactive;
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}";

    // This privileged endpoint returns only the latest 200 events. There is no continuation,
    // global query or total contract; preserve the bounded snapshot without local filtering.

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
        var requestRevision = ++revision;
        pending?.Cancel();
        pending?.Dispose();
        pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var token = pending.Token;
        var scope = CurrentContextKey;
        if (scope != contextKey) { items = null; contextKey = scope; }
        if (Context.Error is not null || Context.Current is null)
        {
            items = null;
            loadState = new(Context.Error is null ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed);
            return;
        }
        loadState = new(items is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = items is not null };
        try
        {
            var result = await Client.GetSecurityAuditEventsAsync(200, token);
            if (!IsCurrent(requestRevision, scope)) return;
            items = result;
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
            var forbidden = status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            if (forbidden) { items = null; }
            loadState = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed)
            {
                HasContent = items is not null,
                Message = forbidden ? T("PlatformAdminRequiredMessage") : null,
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
            items = null;
            var loading = LoadAsync();
            StateHasChanged();
            await loading;
            StateHasChanged();
        });
    }
    public void Dispose() { disposed = true; revision++; Context.Changed -= ContextChanged; pending?.Cancel(); pending?.Dispose(); cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this); }
}
