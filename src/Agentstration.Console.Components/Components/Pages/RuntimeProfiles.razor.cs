using System.Net;
using Agentstration.Identity.Contracts;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Components.Pages;

public partial class RuntimeProfiles
{
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? pending;
    private IReadOnlyList<RuntimeProfileSummaryResponse>? profiles;
    private CollectionQuery query = new();
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? contextKey;
    private long revision;
    private bool initialized;
    private bool disposed;
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}";

    // The existing endpoint returns the complete authorized catalog in one array. No additional reads are introduced.
    private IReadOnlyList<RuntimeProfileSummaryResponse> Filtered => (profiles ?? []).Where(profile =>
        (string.IsNullOrWhiteSpace(query.Search) || profile.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase) || profile.Properties.DisplayName.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
        && (query.Filter("runtime").Value.Length == 0 || profile.Properties.RuntimeType == query.Filter("runtime").Value)
        && (query.Filter("session").Value.Length == 0 || profile.Properties.Execution.SessionMode.ToString() == query.Filter("session").Value)
        && (query.Filter("streaming").Value.Length == 0 || profile.Properties.Execution.Streaming.ToString() == query.Filter("streaming").Value)
        && (query.Filter("namespace").Value.Length == 0 || profile.Namespace == query.Filter("namespace").Value)).ToArray();
    private IReadOnlyList<RuntimeProfileSummaryResponse> PageItems => Filtered.Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToArray();
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
        if (scope != contextKey) { profiles = null; query = new(); contextKey = scope; }
        if (Context.Error is not null || !Context.HasPermission(AuthorizationPermissions.ResourcesRead))
        {
            profiles = null;
            loadState = new(Context.Error is null ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed);
            return;
        }
        loadState = new(profiles is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = profiles is not null };
        try
        {
            var result = await Client.GetRuntimeProfilesAsync(token);
            if (!IsCurrent(requestRevision, scope)) return;
            profiles = result;
            CorrectPage();
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
            var forbidden = status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            if (forbidden) profiles = null;
            loadState = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed)
            {
                HasContent = profiles is not null,
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
