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

    private static ToolResource Tool(ToolResourceProperties properties) => new()
    {
        Metadata = new ResourceMetadata { Name = "search" },
        Kind = ToolResourceKinds.Tool,
        ApiVersion = ResourceApiVersions.CoreV1,
        Definition = properties
    };
}
