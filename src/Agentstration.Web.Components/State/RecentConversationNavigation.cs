namespace Agentstration.Web.Components.State;

public sealed record RecentConversationNavigationItem(string Title, string Url);

public interface IRecentConversationNavigationProvider
{
    Task<IReadOnlyList<RecentConversationNavigationItem>> ListAsync(
        string workspaceName,
        string? dashboardName,
        CancellationToken cancellationToken);
}

internal sealed class EmptyRecentConversationNavigationProvider : IRecentConversationNavigationProvider
{
    public Task<IReadOnlyList<RecentConversationNavigationItem>> ListAsync(
        string workspaceName,
        string? dashboardName,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RecentConversationNavigationItem>>([]);
}
