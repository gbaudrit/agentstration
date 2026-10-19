using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class AssistantDiagnosticsToolTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task WorkspaceInspectionIsBoundedReadOnlyAndRejectsUndeclaredArguments()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var tool = factory.Services.GetServices<IInternalMcpToolHandler>()
            .Single(value => value.Definition.Name == AgentstrationInternalTools.AssistantDiagnosticsInspect);
        var invocation = new InternalMcpToolInvocation(
            context.TenantId, new WorkspaceId(context.WorkspaceId), context.PrincipalId, "diagnostics-1", "correlation-1",
            JsonSerializer.SerializeToElement(new { area = "workspace" }), ToolDefinitionCallerKind.Agent);

        var result = await tool.ExecuteAsync(invocation, default);

        Assert.IsNotNull(result);
        Assert.AreEqual("available", result.Value.GetProperty("availability").GetString());
        Assert.IsTrue(result.Value.GetProperty("evidence").GetArrayLength() > 0);
        var serialized = result.Value.GetRawText();
        Assert.IsFalse(serialized.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(serialized.Contains("secretValue", StringComparison.OrdinalIgnoreCase));
        var schema = tool.Definition.InputSchema.GetRawText();
        Assert.IsFalse(schema.Contains("tenantId", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("workspaceId", StringComparison.OrdinalIgnoreCase));

        var invalid = invocation with { Arguments = JsonSerializer.SerializeToElement(new { area = "workspace", path = "../secrets" }) };
        var exception = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => tool.ExecuteAsync(invalid, default));
        Assert.AreEqual("assistant_diagnostics_argument_unknown", exception.Code);
    }

    [TestMethod]
    public async Task FlowRunInspectionRequiresAnIdentifier()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var tool = factory.Services.GetServices<IInternalMcpToolHandler>()
            .Single(value => value.Definition.Name == AgentstrationInternalTools.AssistantDiagnosticsInspect);
        var invocation = new InternalMcpToolInvocation(
            context.TenantId, new WorkspaceId(context.WorkspaceId), context.PrincipalId, "diagnostics-2", null,
            JsonSerializer.SerializeToElement(new { area = "flow-run" }), ToolDefinitionCallerKind.Agent);

        var exception = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => tool.ExecuteAsync(invocation, default));
        Assert.AreEqual("assistant_diagnostics_flow_run_required", exception.Code);
    }
}
