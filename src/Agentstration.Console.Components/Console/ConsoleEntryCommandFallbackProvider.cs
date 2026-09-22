using Agentstration.Web.Components;
using Agentstration.Work;

namespace Agentstration.Web.Console;

public sealed class ConsoleEntryCommandFallbackProvider(
    IEntryAdministrationApiClient entries,
    ILogger<ConsoleEntryCommandFallbackProvider> logger) : ICommandPaletteFallbackProvider
{
    public async Task<CommandPaletteFallbackResult?> ResolveAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        try
        {
            var discovered = await entries.GetConsoleEntriesAsync(cancellationToken);
            var primary = discovered
                .Where(value => value.Exposure.Console.Role == EntryConsoleRole.Primary
                    && value.Execution?.CanInvoke == true)
                .Take(2)
                .ToArray();
            if (primary.Length != 1)
            {
                if (primary.Length > 1)
                    logger.LogWarning("Console command fallback is disabled because multiple accessible executable primary Entries are configured.");
                return null;
            }

            var entry = primary[0];
            return new CommandPaletteFallbackResult(
                entry.DisplayName,
                ConsoleEntryInteractionNavigation.Build(entry.WorkspaceId, entry.Namespace, entry.Name, query),
                "✦",
                entry.Description);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "The primary Console Entry could not be resolved for command fallback.");
            return null;
        }
    }
}
