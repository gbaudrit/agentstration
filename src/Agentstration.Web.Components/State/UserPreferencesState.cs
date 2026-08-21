namespace Agentstration.Web.Components.State;

public sealed class UserPreferencesState(IUserPreferencesClient client)
{
    private bool systemDarkTheme;

    public UserTheme Theme { get; private set; } = UserTheme.System;
    public bool IsDarkTheme => Theme == UserTheme.Dark || Theme == UserTheme.System && systemDarkTheme;
    public bool IsSaving { get; private set; }
    public bool IsLoaded { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var preferences = await client.GetAsync(cancellationToken);
            Theme = preferences.Theme;
            IsLoaded = true;
            Error = null;
        }
        catch (HttpRequestException)
        {
            Error = "Theme preference could not be loaded.";
        }
        Changed?.Invoke();
    }

    public void SetSystemTheme(bool isDark)
    {
        systemDarkTheme = isDark;
        if (Theme == UserTheme.System) Changed?.Invoke();
    }

    public async Task ToggleThemeAsync(CancellationToken cancellationToken)
    {
        var theme = IsDarkTheme ? UserTheme.Light : UserTheme.Dark;
        await SetThemeAsync(theme, cancellationToken);
    }

    public async Task SetThemeAsync(UserTheme theme, CancellationToken cancellationToken)
    {
        if (IsSaving || Theme == theme) return;
        var previous = Theme;
        Theme = theme;
        IsSaving = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            Theme = (await client.UpdateAsync(Theme, cancellationToken)).Theme;
            IsLoaded = true;
        }
        catch (HttpRequestException)
        {
            Theme = previous;
            Error = "Theme preference could not be saved.";
        }
        finally
        {
            IsSaving = false;
            Changed?.Invoke();
        }
    }
}

