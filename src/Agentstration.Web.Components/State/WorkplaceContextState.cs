namespace Agentstration.Web.Components.State;

public sealed record WorkplaceContextSnapshot(
    string WorkspaceName,
    string WorkspaceDisplayName,
    string? OrganizationName,
    string? OrganizationDisplayName,
    string? UserDisplayName);

public sealed class WorkplaceContextState
{
    public event Action? Changed;

    public WorkplaceContextSnapshot? Current { get; private set; }

    public void SetWorkspace(
        string workspaceName,
        string workspaceDisplayName,
        string? organizationName = null,
        string? organizationDisplayName = null,
        string? userDisplayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDisplayName);

        var next = new WorkplaceContextSnapshot(
            workspaceName,
            workspaceDisplayName,
            organizationName,
            organizationDisplayName,
            userDisplayName);
        if (next == Current) return;

        Current = next;
        Changed?.Invoke();
    }
}
