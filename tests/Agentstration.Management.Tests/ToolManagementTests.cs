using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;

namespace Agentstration.Management.Tests;

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
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.ToolProvider,
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
            Kind = ResourceKinds.ToolProvider,
            ApiVersion = ManagementApiVersions.CoreV1,
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
        Kind = ResourceKinds.Tool,
        ApiVersion = ManagementApiVersions.CoreV1,
        Definition = properties
    };
}
