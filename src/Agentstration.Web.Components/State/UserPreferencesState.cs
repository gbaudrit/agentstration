namespace Agentstration.Web.Components.State;

public sealed class UserPreferencesState
{
    public bool IsDarkTheme { get; private set; }
    public event Action? Changed;

    public void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        Changed?.Invoke();
    }
}

