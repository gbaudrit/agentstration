using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Tools.Tests;

[TestClass]
public sealed class ToolManagementTests
{
    [TestMethod]
    public void ToolResourceSeparatesPersistentIdentityFromAepToolType()
    {
        var resource = Tool(new ToolResourceProperties
        {
            DisplayName = "Search",
            ToolType = new ToolTypeReference("extension.search", "document-search")
        });

        ToolManagementService.ValidateTool(resource);

        Assert.AreEqual("search", resource.Metadata.Name);
        Assert.AreEqual("document-search", resource.Definition.ToolType!.Id);
    }

    [TestMethod]
    public void ManuallyConfiguredToolRequiresToolType()
    {
        var missing = Tool(new ToolResourceProperties { DisplayName = "Search" });
        Assert.Throws<ToolResourceValidationException>(() => ToolManagementService.ValidateTool(missing));
    }

    [TestMethod]
    public void McpConfigurationBelongsToToolProvider()
    {
        var valid = new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            Metadata = new ResourceMetadata { Name = "local" },
            Definition = new ToolProviderProperties
            {
                DisplayName = "Local MCP",
                ProviderType = ToolProviderType.Mcp,
                Mcp = new McpToolProviderConfiguration
                {
                    Transport = McpToolProviderTransport.StreamableHttp,
                    Endpoint = new Uri("https://example.test/mcp")
                }
            }
        };

        ToolManagementService.ValidateProvider(valid);
    }

    [TestMethod]
    public void McpProviderRequiresAbsoluteHttpEndpoint()
    {
        var resource = new ToolProviderResource
        {
            Metadata = new ResourceMetadata { Name = "local" },
            Kind = ToolResourceKinds.ToolProvider,
            ApiVersion = ResourceApiVersions.CoreV1,
            Definition = new ToolProviderProperties
            {
                DisplayName = "Local MCP",
                ProviderType = ToolProviderType.Mcp,
                Mcp = new McpToolProviderConfiguration
                {
                    Transport = McpToolProviderTransport.StreamableHttp,
                    Endpoint = new Uri("file:///tmp/mcp")
                }
            }
        };

        Assert.Throws<ToolResourceValidationException>(() => ToolManagementService.ValidateProvider(resource));
    }

    [TestMethod]
    public void ToolCategoryHasCanonicalJsonAndYamlSerialization()
    {
        var resource = new ToolCategoryResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolCategory,
            Metadata = new ResourceMetadata { Name = "knowledge-tools" },
            Definition = new ToolCategoryProperties
            {
                DisplayName = "Knowledge tools",
                Description = "Cross-provider authoring helpers.",
                Tools =
                [
                    new ResourceReference("search.documents"),
                    new ResourceReference("summarize", @namespace: new ResourceNamespace("team-a"))
                ]
            }
        };

        var json = ResourceManifestSerializer.ToJson(resource);
        var yaml = ResourceManifestSerializer.ToYaml(resource);
        var fromJson = ResourceManifestSerializer.FromJson<ToolCategoryResource>(json);
        var fromYaml = ResourceManifestSerializer.FromYaml<ToolCategoryResource>(yaml);

        Assert.AreEqual(ToolResourceKinds.ToolCategory, fromJson.Kind);
        Assert.AreEqual(resource.Definition.DisplayName, fromJson.Definition.DisplayName);
        Assert.AreEqual(resource.Definition.Description, fromYaml.Definition.Description);
        CollectionAssert.AreEqual(
            resource.Definition.Tools.Select(value => value.Name).ToArray(),
            fromYaml.Definition.Tools.Select(value => value.Name).ToArray());
        StringAssert.Contains(yaml, "kind: ToolCategory");
        StringAssert.Contains(yaml, "displayName: Knowledge tools");
    }

    private static ToolResource Tool(ToolResourceProperties properties) => new()
    {
        Metadata = new ResourceMetadata { Name = "search" },
        Kind = ToolResourceKinds.Tool,
        ApiVersion = ResourceApiVersions.CoreV1,
        Definition = properties
    };
}
