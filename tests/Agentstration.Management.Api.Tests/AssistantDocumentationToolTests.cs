using System.Text.Json;
using Agentstration.Infrastructure.Assistant;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class AssistantDocumentationToolTests
{
    [TestMethod]
    public async Task SearchReturnsBoundedEvidenceAndLocalPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentstration-docs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "workspaces.md"), "# Workspaces\n\n## Ownership\n\nA Workspace is the agentic ownership and execution boundary.");
            var result = await new AssistantDocumentationCatalog(root).SearchAsync("workspace ownership", 3, default);

            Assert.AreEqual("available", result.Availability);
            var match = result.Matches.Single();
            Assert.AreEqual("workspaces.md", match.Path);
            Assert.AreEqual("Workspaces", match.Title);
            Assert.AreEqual("Ownership", match.Section);
            StringAssert.Contains(match.Snippet, "ownership and execution boundary");
            Assert.IsLessThanOrEqualTo(1201, match.Snippet.Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task ToolRejectsUnknownArgumentsAndReportsUnavailableCatalog()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"agentstration-missing-docs-{Guid.NewGuid():N}");
        var tool = new AssistantDocumentationMcpTool(new AssistantDocumentationCatalog(missing));
        var invocation = new InternalMcpToolInvocation(
            Guid.NewGuid(), WorkspaceId.New(), Guid.NewGuid(), "call", null,
            JsonSerializer.SerializeToElement(new { query = "bootstrap profile" }), ToolDefinitionCallerKind.Agent);

        var result = await tool.ExecuteAsync(invocation, default);
        Assert.IsNotNull(result);
        Assert.AreEqual("unavailable", result.Value.GetProperty("availability").GetString());

        var invalid = invocation with { Arguments = JsonSerializer.SerializeToElement(new { query = "bootstrap", path = "../secret" }) };
        var exception = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(async () => await tool.ExecuteAsync(invalid, default));
        Assert.AreEqual("assistant_documentation_argument_unknown", exception.Code);
    }
}
