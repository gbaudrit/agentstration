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
        Assert.AreEqual("document-search", resource.Properties.ToolType!.Id);
    }

    [TestMethod]
    public void ToolResourceRequiresExactlyOneValidMapping()
    {
        var missing = Tool(new ToolResourceProperties { DisplayName = "Search" });
        var both = Tool(new ToolResourceProperties
        {
            DisplayName = "Search",
            ToolType = new ToolTypeReference("extension.search", "search"),
            Mcp = new DirectMcpToolReference(new ResourceReference(ServerId()), "search")
        });

        Assert.Throws<ToolResourceValidationException>(() => ToolManagementService.ValidateTool(missing));
        Assert.Throws<ToolResourceValidationException>(() => ToolManagementService.ValidateTool(both));
    }

    [TestMethod]
    public void DirectMcpMappingRequiresCanonicalMcpServerResource()
    {
        var valid = Tool(new ToolResourceProperties
        {
            DisplayName = "Search",
            Mcp = new DirectMcpToolReference(new ResourceReference(ServerId()), "search")
        });
        var invalid = valid with
        {
            Properties = valid.Properties with
            {
                Mcp = new DirectMcpToolReference(new ResourceReference(""), "search")
            }
        };

        ToolManagementService.ValidateTool(valid);
        Assert.Throws<ToolResourceValidationException>(() => ToolManagementService.ValidateTool(invalid));
    }

    [TestMethod]
    public void McpServerRequiresAbsoluteHttpEndpoint()
    {
        var resource = new McpServerResource
        {
            Id = ServerId(),
            Name = "local",
            Kind = ResourceKinds.McpServer,
            ApiVersion = ManagementApiVersions.CoreV1,
            Properties = new McpServerProperties { Endpoint = new Uri("file:///tmp/mcp") }
        };

        Assert.Throws<ToolResourceValidationException>(() => ToolManagementService.ValidateMcpServer(resource));
    }

    private static ToolResource Tool(ToolResourceProperties properties) => new()
    {
        Name = "search",
        Kind = ResourceKinds.Tool,
        ApiVersion = ManagementApiVersions.CoreV1,
        Properties = properties
    };

    private static string ServerId() => "local";
}
