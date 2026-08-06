using Agentstration.Web.Components;
using Agentstration.Web.Components.Models;
using Agentstration.Management.Abstractions;

namespace Agentstration.Web.Console;

public sealed class ConsoleResourceSearchProvider(
    IManagementApiClient management,
    IModelProfilesClient modelProfiles,
    IModelProvidersClient modelProviders,
    IRuntimeProfilesClient runtimeProfiles,
    IFlowApiClient flows,
    IRuntimeApiClient runtime,
    IWorkApiClient work,
    TimeProvider timeProvider,
    ILogger<ConsoleResourceSearchProvider> logger) : IResourceSearchProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private IReadOnlyList<ResourceSearchResult> cache = [];
    private DateTimeOffset loadedAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<ResourceSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var normalized = query.Trim();
        if (normalized.Length < 2) return [];

        await EnsureCacheAsync(cancellationToken);
        return cache
            .Where(item => Matches(item, normalized))
            .OrderByDescending(item => item.Label.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.ResourceType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    private async Task EnsureCacheAsync(CancellationToken cancellationToken)
    {
        if (timeProvider.GetUtcNow() - loadedAt < CacheDuration) return;

        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (timeProvider.GetUtcNow() - loadedAt < CacheDuration) return;

            var agentsTask = SafeLoadAsync("agents", token => management.GetAgentsAsync(token), cancellationToken);
            var profilesTask = SafeLoadAsync("model profiles", token => modelProfiles.GetModelProfilesAsync(null, null, null, token), cancellationToken);
            var providersTask = SafeLoadAsync("model providers", token => modelProviders.GetModelProvidersAsync(token), cancellationToken);
            var runtimeProfilesTask = SafeLoadAsync("runtime profiles", token => runtimeProfiles.GetRuntimeProfilesAsync(token), cancellationToken);
            var flowsTask = SafeLoadAsync("flows", token => flows.GetFlowsAsync(token), cancellationToken);
            var runtimesTask = SafeLoadAsync("runtimes", token => runtime.GetInstancesAsync(token), cancellationToken);
            var executionsTask = SafeLoadAsync("executions", token => runtime.GetExecutionsAsync(token), cancellationToken);
            var workTask = SafeLoadAsync("work items", token => work.GetWorkItemsAsync(token), cancellationToken);

            await Task.WhenAll(agentsTask, profilesTask, providersTask, runtimeProfilesTask, flowsTask, runtimesTask, executionsTask, workTask);

            cache =
            [
                .. agentsTask.Result.Select(ToResult),
                .. profilesTask.Result.Select(item => new ResourceSearchResult(item.Properties.DisplayName, "Model profile", item.Id, $"/modelprofiles/{Escape(item.ResourceGroup)}/{Escape(item.Name)}", item.Properties.Status, "◇", $"{item.Name} {item.Properties.Model.Name}")),
                .. providersTask.Result.Select(item => new ResourceSearchResult(item.Properties.DisplayName, "Model provider", item.Id, $"/modelproviders/{Escape(item.ResourceGroup)}/{Escape(item.Name)}", item.Properties.Status, "⬡", $"{item.Name} {item.Properties.ProviderType}")),
                .. runtimeProfilesTask.Result.Select(item => new ResourceSearchResult(item.Properties.DisplayName, "Runtime profile", item.Id, $"/runtimeprofiles/{Escape(item.ResourceGroup)}/{Escape(item.Name)}", "Configured", "◈", $"{item.Name} {item.Properties.RuntimeType}")),
                .. flowsTask.Result.Select(item => new ResourceSearchResult(item.Name, "Flow", item.Id, $"/flows/{Escape(item.Id)}", item.Status, "⌘", $"{item.Kind} {item.Version}")),
                .. runtimesTask.Result.Select(item => new ResourceSearchResult(item.Id, "Runtime", item.Id, "/runtime", item.Status, "◉", $"{item.Agent} {item.Location}")),
                .. executionsTask.Result.Select(item => new ResourceSearchResult(item.Id, "Execution", item.Id, $"/runs/{Escape(item.Id)}", item.Status, "▶", $"{item.Agent} {item.Flow}")),
                .. workTask.Result.Select(item => new ResourceSearchResult(item.Title, "Task", item.Id.ToString(), $"/tasks/{item.Id}", item.Status, "✓", $"{item.Type} {item.Owner}"))
            ];
            loadedAt = timeProvider.GetUtcNow();
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<IReadOnlyList<T>> SafeLoadAsync<T>(string source, Func<CancellationToken, Task<IReadOnlyList<T>>> load, CancellationToken cancellationToken)
    {
        try { return await load(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(exception, "Command palette resource source {ResourceSource} is unavailable.", source);
            return [];
        }
    }

    private static ResourceSearchResult ToResult(AgentSummary item)
    {
        var identifier = ResourceIdentifier.Parse(item.Id);
        return new ResourceSearchResult(item.Name, "Agent", item.Id, $"/agents/{Escape(identifier.ResourceGroup)}/{Escape(identifier.Name)}", StatusPresentation.Label(item.Status), "◎", $"{identifier.Name} {item.ModelProfile}");
    }

    private static bool Matches(ResourceSearchResult item, string query) =>
        item.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.ResourceType.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Identifier.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Status.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (item.SearchText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
