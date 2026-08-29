namespace Agentstration.Web.Components.State;

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
    Guid TenantId,
    string TenantName,
    string TenantDisplayName,
    Guid WorkspaceId,
    string WorkspaceName,
    string WorkspaceDisplayName,
    IReadOnlySet<string> Permissions,
    IReadOnlyList<ConsoleWorkspaceOption> Workspaces);

public interface IConsoleContextProvider
{
    Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken);
}

public sealed class ConsoleContextState(IConsoleContextProvider provider)
{
    public event Action? Changed;
    public ConsoleContextSnapshot? Current { get; private set; }
    public bool IsLoading { get; private set; }
    public string? Error { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        Error = null;
        try { Current = await provider.GetAsync(cancellationToken); }
        catch (Exception exception) { Error = exception.Message; }
        finally { IsLoading = false; Changed?.Invoke(); }
    }

    public bool HasPermission(string permission) => Current?.Permissions.Contains(permission) == true;
}

internal sealed class EmptyConsoleContextProvider : IConsoleContextProvider
{
    public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
        Guid.Empty, "Local User", Guid.Empty, "dev", "Development", Guid.Empty, "default", "Default workspace",
        new HashSet<string>(StringComparer.Ordinal), [new(Guid.Empty, Guid.Empty, "dev", "Development", "default", "Default workspace")]));
}
