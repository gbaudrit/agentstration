using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ToolCategoryConsoleTests
{
    [TestMethod]
    public void EditorShowsProviderProvenanceMissingMembersAndSafeDeleteExplanation()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolsClient>(new ToolsClient());
        context.Services.AddSingleton<IToolCategoriesClient>(new CategoriesClient());

        var rendered = context.Render<ToolCategoryEditor>(parameters => parameters.Add(component => component.Name, "operations"));

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find("[data-testid='category-members']").TextContent, "Operations MCP");
            StringAssert.Contains(rendered.Find("[data-testid='category-members']").TextContent, "Documentation MCP");
            StringAssert.Contains(rendered.Find("[data-testid='category-members']").TextContent, "removed.tool");
            Assert.AreEqual("/tools/operations.run?namespace=default&scopeRef=%2Fworkspaces%2Faaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa&context=%2Fworkspaces%2Faaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", rendered.Find("[data-provider='/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa|default/operations'] .tool-category-member a").GetAttribute("href"));
            var providerBlocks = rendered.FindAll(".tool-category-provider[data-provider]");
            Assert.HasCount(2, providerBlocks);
            var operationsBlock = providerBlocks.Single(block => block.GetAttribute("data-provider") == "/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa|default/operations");
            var documentationBlock = providerBlocks.Single(block => block.GetAttribute("data-provider") == "/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa|default/documentation");
            StringAssert.Contains(operationsBlock.TextContent, "Run operation");
            Assert.IsFalse(operationsBlock.TextContent.Contains("Search documentation", StringComparison.Ordinal));
            StringAssert.Contains(documentationBlock.TextContent, "Search documentation");
            Assert.IsFalse(documentationBlock.TextContent.Contains("Run operation", StringComparison.Ordinal));
        });

        rendered.Find("button.button-danger").Click();
        var deleteExplanation = rendered.Find("[role='dialog']").TextContent;
        Assert.IsTrue(deleteExplanation.Contains("ne seront pas modifiés", StringComparison.Ordinal)
            || deleteExplanation.Contains("will not be changed", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProviderAndToolHomonymsKeepSeparateGroupsLinksAndSelectionIds()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolCategoriesClient>(new CategoriesClient());
        var firstScope = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var secondScope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var spaces = new[] { ResourceNamespace.Parse("team-a"), ResourceNamespace.Parse("team-b") };
        var owners = new[] { firstScope, secondScope };
        var providers = owners.Select((scope, index) => new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            Metadata = new() { Name = "same-provider", Namespace = spaces[index] },
            ScopeRef = scope,
            Definition = new() { DisplayName = "Same provider", ProviderType = ToolProviderType.Mcp }
        }).ToArray();
        var tools = owners.Select((scope, index) => ToolsClient.Tool with
        {
            Metadata = new() { Name = "same-tool", Namespace = spaces[index] },
            ScopeRef = scope,
            Definition = ToolsClient.Tool.Definition with { Provider = new("same-provider", scope, spaces[index]) }
        }).ToArray();
        context.Services.AddSingleton<IToolsClient>(new ToolsClient(tools, providers));
        var cut = context.Render<ToolCategoryEditor>(parameters => parameters.Add(value => value.Name, "operations"));
        var groups = cut.FindAll(".tool-category-provider[data-provider]");
        Assert.HasCount(2, groups);
        var inputs = groups.Select(group => group.QuerySelector(".tool-category-member input")!.Id).ToArray();
        Assert.AreNotEqual(inputs[0], inputs[1]);
        for (var index = 0; index < groups.Count; index++)
        {
            Assert.HasCount(1, groups[index].QuerySelectorAll(".tool-category-member"));
            var link = groups[index].QuerySelector(".collection-identity a")!.GetAttribute("href")!;
            StringAssert.Contains(link, $"namespace={spaces[index].Value}");
            StringAssert.Contains(link, $"scopeRef={Uri.EscapeDataString(owners[index].Value)}");
        }
    }

    private sealed class CategoriesClient : IToolCategoriesClient
    {
        private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        private static readonly ToolCategoryResource Category = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolCategory,
            Metadata = new ResourceMetadata { Name = "operations" },
            ScopeRef = Scope,
            Definition = new ToolCategoryProperties
            {
                DisplayName = "Operations",
                Tools = [new("operations.run", Scope), new("removed.tool", Scope)]
            }
        };
        public Task<ResourceSnapshot<ToolCategoryResource>> GetCategoryAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(new ResourceSnapshot<ToolCategoryResource>(Category, "\"etag\""));
        public Task<IReadOnlyList<ToolCategoryMember>> GetMembersAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ToolCategoryMember>>([new(Category.Definition.Tools[0], ToolsClient.Tool, "available"), new(Category.Definition.Tools[1], null, "missing")]);
        public Task<IReadOnlyList<ToolCategoryResource>> GetCategoriesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ToolCategoryResource>>([Category]);
        public Task<ResourceSnapshot<ToolCategoryResource>> CreateCategoryAsync(CreateToolCategoryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolCategoryResource>> UpdateCategoryAsync(string name, PutToolCategoryRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteCategoryAsync(string name, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ToolsClient(IReadOnlyList<ToolResource>? tools = null, IReadOnlyList<ToolProviderResource>? providers = null) : IToolsClient
    {
        internal static readonly ToolResource Tool = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.Tool,
            Metadata = new ResourceMetadata { Name = "operations.run" },
            ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            Definition = new ToolResourceProperties
            {
                DisplayName = "Run operation",
                Provider = new("operations"),
                Enabled = true,
                Discovery = new() { Available = true, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch }
            }
        };
        private static readonly ToolResource DocumentationTool = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.Tool,
            Metadata = new ResourceMetadata { Name = "documentation.search" },
            ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            Definition = new ToolResourceProperties
            {
                DisplayName = "Search documentation",
                Provider = new("documentation"),
                Enabled = true,
                Discovery = new() { Available = true, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch }
            }
        };
        public Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ToolResource>>(tools ?? [Tool, DocumentationTool]);
        public Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ToolProviderResource>>(providers ?? [
            new ToolProviderResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolProvider, ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")), Metadata = new ResourceMetadata { Name = "operations" }, Definition = new ToolProviderProperties { DisplayName = "Operations MCP", ProviderType = ToolProviderType.Mcp } },
            new ToolProviderResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolProvider, ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")), Metadata = new ResourceMetadata { Name = "documentation" }, Definition = new ToolProviderProperties { DisplayName = "Documentation MCP", ProviderType = ToolProviderType.Mcp } }
        ]);
        public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolConnectionTestResponse> TestAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolDiscoveryDiffResponse> RefreshAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
