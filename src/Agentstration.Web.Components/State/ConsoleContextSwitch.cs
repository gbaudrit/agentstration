namespace Agentstration.Web.Components.State;

public sealed record ConsoleContextSwitchRequest(
    Uri Location, string CurrentScopeRef, string TargetScopeRef, string FallbackLocation,
    string? ResourceOwnerScopeRef = null, bool ResourcePage = false);

public sealed record ConsoleContextSwitchDestination(string Location, bool ResourceUnavailable = false);

/// <summary>Resolves a destination without changing the active context or any resource.</summary>
public interface IConsoleContextSwitchResolver
{
    Task<ConsoleContextSwitchDestination> ResolveAsync(ConsoleContextSwitchRequest request, CancellationToken cancellationToken);
}

internal sealed class DefaultConsoleContextSwitchResolver : IConsoleContextSwitchResolver
{
    public Task<ConsoleContextSwitchDestination> ResolveAsync(ConsoleContextSwitchRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ConsoleContextSwitchDestination(request.FallbackLocation));
}
