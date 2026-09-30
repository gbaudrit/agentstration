using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Web.Components.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class AgentToolSetConfigurationTests
{
    [TestMethod]
    public async Task FullAssignmentPinsThePublishedVersion()
    {
        using var context = CreateContext();
        IReadOnlyList<AgentToolSetSelection>? selected = null;
        var rendered = context.Render<AgentToolSetConfiguration>(parameters => parameters
            .Add(component => component.Versions, [Version("documentation", "1.2.0")])
            .Add(component => component.SelectedChanged, value => selected = value));

        await rendered.Find("input[type='checkbox']").ChangeAsync(true);

        Assert.HasCount(1, selected!);
        Assert.AreEqual("documentation", selected![0].ToolSet.Name);
        Assert.AreEqual("1.2.0", selected[0].Version);
        Assert.HasCount(0, selected[0].Members);
        StringAssert.Contains(rendered.Markup, "/tools/sets/documentation");
    }

    [TestMethod]
    public async Task MemberSelectionCreatesAnExplicitSubset()
    {
        using var context = CreateContext();
        IReadOnlyList<AgentToolSetSelection>? selected = null;
        var version = Version("documentation", "1.2.0");
        var rendered = context.Render<AgentToolSetConfiguration>(parameters => parameters
            .Add(component => component.Versions, [version])
            .Add(component => component.Selected,
            [
                new AgentToolSetSelection
                {
                    ToolSet = new ResourceReference("documentation"),
                    Version = "1.2.0"
                }
            ])
            .Add(component => component.SelectedChanged, value => selected = value));

        var memberInputs = rendered.FindAll("input[type='checkbox']").Skip(1).ToArray();
        await memberInputs[0].ChangeAsync(false);

        Assert.HasCount(1, selected!);
        CollectionAssert.AreEqual(new[] { "documentation.read" }, selected![0].Members.ToArray());
    }

    [TestMethod]
    public async Task SelectingANewVersionReplacesThePreviousVersion()
    {
        using var context = CreateContext();
        IReadOnlyList<AgentToolSetSelection>? selected = null;
        var rendered = context.Render<AgentToolSetConfiguration>(parameters => parameters
            .Add(component => component.Versions, [Version("documentation", "1.0.0"), Version("documentation", "2.0.0")])
            .Add(component => component.Selected,
            [
                new AgentToolSetSelection
                {
                    ToolSet = new ResourceReference("documentation"),
                    Version = "1.0.0"
                }
            ])
            .Add(component => component.SelectedChanged, value => selected = value));

        var versionTwo = rendered.Find("[data-version='2.0.0'] input[type='checkbox']");
        await versionTwo.ChangeAsync(true);

        Assert.HasCount(1, selected!);
        Assert.AreEqual("2.0.0", selected![0].Version);
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }

    private static ToolSetVersionResource Version(string name, string version) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ToolResourceKinds.ToolSetVersion,
        Metadata = new ResourceMetadata { Name = $"{name}-{version}" },
        ToolSetUid = Guid.NewGuid(),
        ToolSetName = name,
        ToolSetGeneration = 1,
        Version = version,
        DefinitionHash = "sha256:test",
        PublishedAt = DateTimeOffset.UnixEpoch,
        Members =
        [
            Member("documentation.search", "search"),
            Member("documentation.read", "read")
        ]
    };

    private static PublishedToolSetMember Member(string name, string capability) => new()
    {
        Capability = capability,
        Route = capability,
        ToolName = name,
        ToolNamespace = ResourceNamespace.Default,
        ToolUid = Guid.NewGuid(),
        ToolGeneration = 1,
        ProviderName = "documentation-provider",
        ProviderNamespace = ResourceNamespace.Default,
        ExternalToolId = capability,
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object" })
    };
}
