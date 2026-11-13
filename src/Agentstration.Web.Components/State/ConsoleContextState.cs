namespace Agentstration.Web.Components.State;

public enum ConsoleContextKind
{
    Instance,
    Tenant,
    Workspace
}

public sealed record ConsoleContextOption(
    string ScopeRef,
    ConsoleContextKind Kind,
    Guid? TenantId,
    string? TenantName,
    string? TenantDisplayName,
    Guid? WorkspaceId,
    string? WorkspaceName,
    string DisplayName);

public sealed record ConsoleWorkspaceOption(
    Guid Id,
    Guid TenantId,
    string TenantName,
    string TenantDisplayName,
    string Name,
    string DisplayName);

public sealed record ConsoleContextSnapshot(
    Guid UserId,
    string UserDisplayName,
    string SelectedScopeRef,
    ConsoleContextKind SelectedKind,
    Guid TenantId,
    string TenantName,
    string TenantDisplayName,
    Guid WorkspaceId,
    string WorkspaceName,
    string WorkspaceDisplayName,
    IReadOnlySet<string> Permissions,
    IReadOnlyList<ConsoleWorkspaceOption> Workspaces,
    IReadOnlyList<ConsoleContextOption> Contexts)
{
    public ConsoleContextSnapshot(
        Guid userId,
        string userDisplayName,
        Guid tenantId,
        string tenantName,
        string tenantDisplayName,
        Guid workspaceId,
        string workspaceName,
        string workspaceDisplayName,
        IReadOnlySet<string> permissions,
        IReadOnlyList<ConsoleWorkspaceOption> workspaces)
        : this(userId, userDisplayName, $"/workspaces/{workspaceId:D}", ConsoleContextKind.Workspace,
            tenantId, tenantName, tenantDisplayName, workspaceId, workspaceName, workspaceDisplayName,
            permissions, workspaces,
            [new($"/workspaces/{workspaceId:D}", ConsoleContextKind.Workspace, tenantId, tenantName,
                tenantDisplayName, workspaceId, workspaceName, workspaceDisplayName)])
    {
    }
}

public interface IConsoleContextProvider
{
    Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken);
}

public sealed class ConsoleContextState(IConsoleContextProvider provider)
{
    private readonly object loadLock = new();
    private Task? loadTask;
    private string? loadingScopeRef;
    private long loadRevision;
    public event Action? Changed;
    public ConsoleContextSnapshot? Current { get; private set; }
    public bool IsLoading { get; private set; }
    public string? Error { get; private set; }

    public Task LoadAsync(CancellationToken cancellationToken) => LoadAsync(null, cancellationToken);

    public Task LoadAsync(string? scopeRef, CancellationToken cancellationToken)
    {
        lock (loadLock)
        {
            if (loadTask is { IsCompleted: false } && (scopeRef is null || loadingScopeRef == scopeRef)) return loadTask;
            loadingScopeRef = scopeRef;
            var revision = ++loadRevision;
            var task = LoadCoreAsync(revision, cancellationToken);
            if (revision == loadRevision) loadTask = task.IsCompleted ? null : task;
            return task;
        }
    }

    public Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        lock (loadLock)
            return Current is not null ? Task.CompletedTask : LoadAsync(cancellationToken);
    }

    private async Task LoadCoreAsync(long revision, CancellationToken cancellationToken)
    {
        IsLoading = true;
        Error = null;
        try
        {
            var current = await provider.GetAsync(cancellationToken);
            lock (loadLock) { if (revision == loadRevision) Current = current; }
        }
        catch (Exception exception)
        {
            lock (loadLock) { if (revision == loadRevision) Error = exception.Message; }
        }
        finally
        {
            var notify = false;
            lock (loadLock)
            {
                if (revision == loadRevision) { IsLoading = false; loadTask = null; notify = true; }
            }
            if (notify) Changed?.Invoke();
        }
    }

    public bool HasPermission(string permission) => Current?.Permissions.Contains(permission) == true;
}

internal sealed class EmptyConsoleContextProvider : IConsoleContextProvider
{
    public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
        Guid.Empty, "Local User", "/workspaces/00000000-0000-0000-0000-000000000000", ConsoleContextKind.Workspace,
        Guid.Empty, "dev", "Development", Guid.Empty, "default", "Default workspace",
        new HashSet<string>(StringComparer.Ordinal),
        [new(Guid.Empty, Guid.Empty, "dev", "Development", "default", "Default workspace")],
        [new("/workspaces/00000000-0000-0000-0000-000000000000", ConsoleContextKind.Workspace,
            Guid.Empty, "dev", "Development", Guid.Empty, "default", "Default workspace")]));
}
