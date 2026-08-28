using System.Globalization;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed class PrincipalPreferencesService(IIdentityStore store, TimeProvider timeProvider)
{
    public async Task<PrincipalPreferences> GetAsync(Guid principalId, CancellationToken cancellationToken)
    {
        await EnsureActivePrincipalAsync(principalId, cancellationToken);
        return await store.GetPrincipalPreferencesAsync(principalId, cancellationToken)
            ?? new PrincipalPreferences(principalId, ThemePreference.System, timeProvider.GetUtcNow(), null);
    }

    public async Task<PrincipalPreferences> UpdateAsync(
        Guid principalId,
        string theme,
        string? language,
        CancellationToken cancellationToken)
    {
        await EnsureActivePrincipalAsync(principalId, cancellationToken);
        if (!Enum.TryParse<ThemePreference>(theme, true, out var parsedTheme)
            || !Enum.IsDefined(parsedTheme))
            throw new ArgumentException("Theme must be one of: System, Light, Dark.", nameof(theme));

        var normalizedLanguage = NormalizeLanguage(language);
        var preferences = new PrincipalPreferences(principalId, parsedTheme, timeProvider.GetUtcNow(), normalizedLanguage);
        await store.UpsertPrincipalPreferencesAsync(preferences, cancellationToken);
        return preferences;
    }

    private static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        try
        {
            return CultureInfo.GetCultureInfo(language.Trim()).Name;
        }
        catch (CultureNotFoundException exception)
        {
            throw new ArgumentException("Language must be a valid BCP 47 culture name.", nameof(language), exception);
        }
    }

    private async Task EnsureActivePrincipalAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var principal = await store.GetPrincipalAsync(principalId, cancellationToken);
        if (principal?.Status != PrincipalStatus.Active)
            throw new InvalidOperationException("The current Principal is unavailable.");
    }
}
