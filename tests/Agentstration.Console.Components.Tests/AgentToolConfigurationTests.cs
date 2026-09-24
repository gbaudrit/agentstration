using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Web.Components.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class AgentToolConfigurationTests
{
    [TestMethod]
    public void ToolsAreGroupedByMcpWithCatalogAndProviderNavigation()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var rendered = context.Render<AgentToolConfiguration>(parameters => parameters
            .Add(component => component.Providers, [Provider("notifications", "Notifications MCP"), Provider("workspace", "Workspace MCP")])
            .Add(component => component.Tools,
            [
                Tool("notifications.send", "Send notification", "notifications", "Creates an in-product notification for a workspace member."),
                Tool("workspace.list", "List workspaces", "workspace"),
                Tool("notifications.history", "Notification history", "notifications")
            ]));

        Assert.AreEqual("/tools", rendered.Find("[data-testid='manage-mcp-tools']").GetAttribute("href"));
        Assert.AreEqual(2, rendered.FindAll(".agent-tool-group").Count);
        Assert.AreEqual(2, rendered.Find("[data-provider='notifications']").QuerySelectorAll(".agent-tool-option").Length);
        Assert.AreEqual(
            "/tools/providers/notifications",
            rendered.Find("[data-provider='notifications'] a").GetAttribute("href"));
        StringAssert.Contains(rendered.Find("[data-provider='workspace']").TextContent, "Workspace MCP");
        Assert.AreEqual(
            "Creates an in-product notification for a workspace member.",
            rendered.Find("[role='tooltip']").TextContent.Trim());
        Assert.AreEqual(
            "/tools/notifications.send",
            rendered.FindAll(".agent-tool-name").Single(link => link.TextContent.Trim() == "Send notification").GetAttribute("href"));
    }

    [TestMethod]
    public async Task SelectionReturnsTheCompleteAgentToolSet()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        IReadOnlyList<string>? selected = null;

        var rendered = context.Render<AgentToolConfiguration>(parameters => parameters
            .Add(component => component.Providers, [Provider("notifications", "Notifications MCP")])
            .Add(component => component.Tools,
            [
                Tool("notifications.send", "Send notification", "notifications"),
                Tool("notifications.history", "Notification history", "notifications")
            ])
            .Add(component => component.SelectedToolIds, ["notifications.send"])
            .Add(component => component.SelectedToolIdsChanged, values => selected = values));

        await rendered.FindAll("input[type='checkbox']").Single(input => !input.HasAttribute("checked")).ChangeAsync(true);

        CollectionAssert.AreEqual(
            new[] { "notifications.send", "notifications.history" },
            selected?.ToArray());
    }

    private static ToolProviderResource Provider(string name, string displayName) => new()
    {
        ApiVersion = "agentstration.io/v1",
        Kind = ToolResourceKinds.ToolProvider,
        Metadata = new ResourceMetadata { Name = name },
        Definition = new ToolProviderProperties
        {
            DisplayName = displayName,
            ProviderType = ToolProviderType.Mcp,
            Mcp = new McpToolProviderConfiguration()
        }
    };

    private static ToolResource Tool(string name, string displayName, string provider, string? description = null) => new()
    {
        ApiVersion = "agentstration.io/v1",
        Kind = ToolResourceKinds.Tool,
        Metadata = new ResourceMetadata { Name = name },
        Definition = new ToolResourceProperties
        {
            DisplayName = displayName,
            Description = description,
            Provider = new ResourceReference(provider),
            Enabled = true,
            Discovery = new ToolDiscoveryState
            {
                Available = true,
                FirstSeenAt = DateTimeOffset.UnixEpoch,
                LastSeenAt = DateTimeOffset.UnixEpoch
            }
        }
    };
}
