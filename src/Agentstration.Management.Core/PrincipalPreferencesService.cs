using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed class PrincipalPreferencesService(IIdentityStore store, TimeProvider timeProvider)
{
    public async Task<PrincipalPreferences> GetAsync(Guid principalId, CancellationToken cancellationToken)
    {
        await EnsureActivePrincipalAsync(principalId, cancellationToken);
        return await store.GetPrincipalPreferencesAsync(principalId, cancellationToken)
            ?? new PrincipalPreferences(principalId, ThemePreference.System, timeProvider.GetUtcNow());
    }

    public async Task<PrincipalPreferences> UpdateAsync(
        Guid principalId,
        string theme,
        CancellationToken cancellationToken)
    {
        await EnsureActivePrincipalAsync(principalId, cancellationToken);
        if (!Enum.TryParse<ThemePreference>(theme, true, out var parsedTheme)
            || !Enum.IsDefined(parsedTheme))
            throw new ArgumentException("Theme must be one of: System, Light, Dark.", nameof(theme));

        var preferences = new PrincipalPreferences(principalId, parsedTheme, timeProvider.GetUtcNow());
        await store.UpsertPrincipalPreferencesAsync(preferences, cancellationToken);
        return preferences;
    }

    private async Task EnsureActivePrincipalAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var principal = await store.GetPrincipalAsync(principalId, cancellationToken);
        if (principal?.Status != PrincipalStatus.Active)
            throw new InvalidOperationException("The current Principal is unavailable.");
    }
}
