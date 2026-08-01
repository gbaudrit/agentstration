namespace Agentstration.Web.Components.State;

public sealed class NavigationState
{
    public bool IsSidebarCollapsed { get; private set; }
    public event Action? Changed;

    public void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        Changed?.Invoke();
    }
}

