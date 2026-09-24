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
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolsClient>(new ToolsClient());
        context.Services.AddSingleton<IToolCategoriesClient>(new CategoriesClient());

        var rendered = context.Render<ToolCategoryEditor>(parameters => parameters.Add(component => component.Name, "operations"));

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find("[data-testid='category-members']").TextContent, "Operations MCP");
            StringAssert.Contains(rendered.Find("[data-testid='category-members']").TextContent, "Documentation MCP");
            StringAssert.Contains(rendered.Find("[data-testid='category-members']").TextContent, "removed.tool");
            Assert.AreEqual("/tools/operations.run", rendered.Find("[data-provider='operations'] .tool-category-member a").GetAttribute("href"));
            var providerBlocks = rendered.FindAll(".tool-category-provider[data-provider]");
            Assert.HasCount(2, providerBlocks);
            var operationsBlock = providerBlocks.Single(block => block.GetAttribute("data-provider") == "operations");
            var documentationBlock = providerBlocks.Single(block => block.GetAttribute("data-provider") == "documentation");
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

    private sealed class ToolsClient : IToolsClient
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
            Definition = new ToolResourceProperties
            {
                DisplayName = "Search documentation",
                Provider = new("documentation"),
                Enabled = true,
                Discovery = new() { Available = true, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch }
            }
        };
        public Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ToolResource>>([Tool, DocumentationTool]);
        public Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ToolProviderResource>>([
            new ToolProviderResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolProvider, Metadata = new ResourceMetadata { Name = "operations" }, Definition = new ToolProviderProperties { DisplayName = "Operations MCP", ProviderType = ToolProviderType.Mcp } },
            new ToolProviderResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolProvider, Metadata = new ResourceMetadata { Name = "documentation" }, Definition = new ToolProviderProperties { DisplayName = "Documentation MCP", ProviderType = ToolProviderType.Mcp } }
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
