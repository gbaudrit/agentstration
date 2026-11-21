using System.Net;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.Components.Forms;

namespace Agentstration.Web.Components.Pages;

public partial class OrganizationMembers
{
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? pending;
    private OrganizationAdministrationResponse? view;
    private CollectionQuery query = new();
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private string? contextKey;
    private long revision;
    private bool initialized;
    private bool disposed;
    private bool interactive;
    private string CurrentContextKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}|{Context.Current?.TenantId:D}|{Context.Current?.WorkspaceId:D}";

    private IReadOnlyList<LocalAccountResponse> localAccounts = [];
    private bool canManageLocalAccounts;
    private bool savingAccount;
    private bool accountError;
    private string newUserName = "", newDisplayName = "", newPassword = "";
    private string? newEmail, accountMessage;
    private EditContext? accountContext;
    private EditContext AccountContext => accountContext ??= new(this);
    private bool invalidAccount;
    private Guid newWorkspaceId;
    private string newRole = BuiltInIdentityRoleNames.Member;

    // This endpoint returns complete authorized members and role assignments.
    private IReadOnlyList<OrganizationMemberResponse> Filtered => (view?.Members ?? []).Where(member =>
        (string.IsNullOrWhiteSpace(query.Search) || member.Principal.DisplayName.Contains(query.Search, StringComparison.OrdinalIgnoreCase) || member.Principal.Id.ToString("D").Contains(query.Search, StringComparison.OrdinalIgnoreCase) || (member.Principal.Email?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ?? false))
        && (query.Filter("membership").Value.Length == 0 || member.Membership.Status.ToString() == query.Filter("membership").Value)
        && ((query.Filter("role").Value.Length == 0 && query.Filter("scope").Value.Length == 0)
            || member.Roles.Any(assigned => (query.Filter("role").Value.Length == 0 || assigned.Role.Id.ToString("D") == query.Filter("role").Value)
                && (query.Filter("scope").Value.Length == 0 || assigned.Scope == query.Filter("scope").Value)))).ToArray();
    private IReadOnlyList<OrganizationMemberResponse> PageItems => Filtered.Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToArray();
    private CollectionPagination Pagination => new(query.Page, query.PageSize, query.Page * query.PageSize < Filtered.Count) { Total = Filtered.Count, SupportsDirectNavigation = true };

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
        if (scope != contextKey) { ClearContext(); query = new(); contextKey = scope; }
        if (Context.Error is not null || !Context.HasPermission(AuthorizationPermissions.AuthorizationRead))
        {
            ClearContext();
            loadState = new(Context.Error is null ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed);
            return;
        }
        loadState = new(view is null ? ResourceLoadStatus.Loading : ResourceLoadStatus.Refreshing) { HasContent = view is not null };
        var primaryLoaded = false;
        try
        {
            var result = await Client.GetOrganizationAsync(token);
            if (!IsCurrent(requestRevision, scope)) return;
            view = result;
            primaryLoaded = true;
            CorrectPage();
            canManageLocalAccounts = false;
            localAccounts = [];
            var capabilities = await Client.GetCapabilitiesAsync(token);
            if (!IsCurrent(requestRevision, scope)) return;
            var accounts = capabilities.SupportsLocalAccounts && capabilities.IsPlatformAdministrator
                ? await Client.GetLocalAccountsAsync(token) : [];
            if (!IsCurrent(requestRevision, scope)) return;
            if (newWorkspaceId == Guid.Empty || !result.Workspaces.Any(workspace => workspace.Id == newWorkspaceId)) newWorkspaceId = capabilities.WorkspaceId;
            localAccounts = accounts;
            canManageLocalAccounts = capabilities.SupportsLocalAccounts && capabilities.IsPlatformAdministrator;
            CorrectPage();
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, scope)) return;
            var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
            var forbidden = !primaryLoaded && status is (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
            canManageLocalAccounts = false;
            localAccounts = [];
            if (forbidden) ClearContext();
            loadState = new(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.Failed)
            {
                HasContent = view is not null,
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
            // Organization identity metadata is reauthorized on every context/role notification.
            ClearContext();
            var loading = LoadAsync();
            StateHasChanged();
            await loading;
            StateHasChanged();
        });
    }
    private void ClearContext()
    {
        view = null;
        localAccounts = [];
        canManageLocalAccounts = false;
        newUserName = newDisplayName = newPassword = "";
        newEmail = accountMessage = null;
        newWorkspaceId = Guid.Empty;
        newRole = BuiltInIdentityRoleNames.Member;
        accountContext = null;
        invalidAccount = false;
        savingAccount = false;
    }

    private async Task CreateAccountAsync()
    {
        if (invalidAccount || savingAccount || !canManageLocalAccounts) return;
        var requestRevision = revision;
        var scope = CurrentContextKey;
        savingAccount = true; accountMessage = null;
        try
        {
            var account = await Client.CreateLocalAccountAsync(new(newUserName, newPassword, newDisplayName, newEmail, newWorkspaceId, newRole), cancellation.Token);
            if (!IsCurrent(requestRevision, scope)) return;
            accountError = false; accountMessage = T("AccountCreated", account.UserName);
            newUserName = newDisplayName = newPassword = ""; newEmail = null;
            await LoadAsync();
        }
        catch (AgentstrationApiException exception) { if (IsCurrent(requestRevision, scope)) { accountError = true; accountMessage = exception.Message; } }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { if (CurrentContextKey == scope) savingAccount = false; }
    }

    private async Task SetEnabledAsync(LocalAccountResponse account, bool enabled)
    {
        if (!canManageLocalAccounts || savingAccount) return;
        var requestRevision = revision;
        var scope = CurrentContextKey;
        savingAccount = true;
        try
        {
            await Client.SetLocalAccountEnabledAsync(account.AccountId, enabled, cancellation.Token);
            if (!IsCurrent(requestRevision, scope)) return;
            var accounts = await Client.GetLocalAccountsAsync(cancellation.Token);
            if (!IsCurrent(requestRevision, scope)) return;
            localAccounts = accounts; accountError = false; accountMessage = T(enabled ? "AccountEnabled" : "AccountDisabled", account.UserName);
        }
        catch (AgentstrationApiException exception) { if (IsCurrent(requestRevision, scope)) { accountError = true; accountMessage = exception.Message; } }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { if (CurrentContextKey == scope) savingAccount = false; }
    }

    public void Dispose() { disposed = true; revision++; Context.Changed -= ContextChanged; pending?.Cancel(); pending?.Dispose(); cancellation.Cancel(); cancellation.Dispose(); GC.SuppressFinalize(this); }
}
