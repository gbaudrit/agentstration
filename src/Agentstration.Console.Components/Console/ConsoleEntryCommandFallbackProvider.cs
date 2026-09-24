using Agentstration.Web.Components;
using Agentstration.Work;

namespace Agentstration.Web.Console;

public sealed class ConsoleEntryCommandFallbackProvider(
    IEntryAdministrationApiClient entries,
    ILogger<ConsoleEntryCommandFallbackProvider> logger) : ICommandPaletteFallbackProvider
{
    public async Task<IReadOnlyList<CommandPaletteFallbackResult>> ResolveAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        try
        {
            var discovered = await entries.GetConsoleEntriesAsync(cancellationToken);
            return discovered
                .Where(value => value.Exposure.Console.Role == EntryConsoleRole.Fallback
                    && value.Execution?.CanInvoke == true)
                .Select(entry => new CommandPaletteFallbackResult(
                    entry.DisplayName,
                    ConsoleEntryInteractionNavigation.Build(entry.WorkspaceId, entry.Namespace, entry.Name, query),
                    "sparkle",
                    entry.Description))
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "The Console fallback Entries could not be resolved for command fallback.");
            return [];
        }
    }
}
