using System.Net.Http.Json;

namespace Agentstration.Web.Components.State;

public enum UserTheme { System, Light, Dark }

public sealed record UserPreferences(UserTheme Theme, DateTimeOffset UpdatedAt);

public interface IUserPreferencesClient
{
    Task<UserPreferences> GetAsync(CancellationToken cancellationToken);
    Task<UserPreferences> UpdateAsync(UserTheme theme, CancellationToken cancellationToken);
}

public sealed class HttpUserPreferencesClient(HttpClient httpClient) : IUserPreferencesClient
{
    public async Task<UserPreferences> GetAsync(CancellationToken cancellationToken)
    {
        var response = await httpClient.GetFromJsonAsync<UserPreferencesResponse>(
            "api/identity/preferences",
            cancellationToken) ?? throw new InvalidOperationException("The preferences API returned no payload.");
        return Map(response);
    }

    public async Task<UserPreferences> UpdateAsync(UserTheme theme, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync(
            "api/identity/preferences",
            new UpdateUserPreferencesRequest(theme.ToString()),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<UserPreferencesResponse>(cancellationToken)
            ?? throw new InvalidOperationException("The preferences API returned no payload.");
        return Map(value);
    }

    private static UserPreferences Map(UserPreferencesResponse response)
    {
        if (!Enum.TryParse<UserTheme>(response.Theme, true, out var theme) || !Enum.IsDefined(theme))
            throw new InvalidOperationException($"The preferences API returned unsupported theme '{response.Theme}'.");
        return new UserPreferences(theme, response.UpdatedAt);
    }

    private sealed record UpdateUserPreferencesRequest(string Theme);
    private sealed record UserPreferencesResponse(string Theme, DateTimeOffset UpdatedAt);
}

internal sealed class EmptyUserPreferencesClient(TimeProvider timeProvider) : IUserPreferencesClient
{
    public Task<UserPreferences> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new UserPreferences(UserTheme.System, timeProvider.GetUtcNow()));

    public Task<UserPreferences> UpdateAsync(UserTheme theme, CancellationToken cancellationToken) =>
        Task.FromResult(new UserPreferences(theme, timeProvider.GetUtcNow()));
}
