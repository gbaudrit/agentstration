using Agentstration.Web.Components.State;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Workplace.Client;
using Agentstration.Workplace.Web.Components.Pages;
using Microsoft.Extensions.Localization;

namespace Agentstration.Workplace.Web;

public sealed class WorkplaceRecentConversationNavigationProvider(
    IWorkplaceApiClient api,
    IStringLocalizer<WorkplacePageStrings> localizer) : IRecentConversationNavigationProvider
{
    public async Task<IReadOnlyList<RecentConversationNavigationItem>> ListAsync(
        string workspaceName,
        string? dashboardName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dashboardName))
            dashboardName = (await api.GetDefaultDashboardAsync(workspaceName, cancellationToken)).Name;

        var workspace = Uri.EscapeDataString(workspaceName);
        var dashboard = Uri.EscapeDataString(dashboardName);
        return (await api.ListInteractionsAsync(workspaceName, 5, cancellationToken))
            .Select(interaction => new RecentConversationNavigationItem(
                Title(interaction),
                $"/w/{workspace}/d/{dashboard}/conversations/{interaction.Id}"))
            .ToArray();
    }

    private string Title(InteractionResponse interaction) =>
        interaction.Messages.FirstOrDefault(message => message.Role == ConversationRole.User)?.Content
        ?? localizer["Conversation"];
}
