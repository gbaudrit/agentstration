using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ResourcePlanningMcpToolTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task PlanningToolsUseTrustedScopeAndRejectResourceArguments()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var handlers = factory.Services.GetServices<IInternalMcpToolHandler>().ToArray();
        var create = handlers.Single(value => value.Definition.Name == AgentstrationInternalTools.ResourcePlanCreate);
        var get = handlers.Single(value => value.Definition.Name == AgentstrationInternalTools.ResourcePlanGet);
        var schema = create.Definition.InputSchema.GetRawText();
        Assert.IsFalse(schema.Contains("scopeRef", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("apiVersion", StringComparison.OrdinalIgnoreCase));

        var result = await create.ExecuteAsync(Invocation(context, "create-1", JsonSerializer.SerializeToElement(new
        {
            title = "Support",
            goal = "Design support",
            plan = new
            {
                solution = new { summary = "Support", outcomes = new[] { "Resolve requests" } },
                roles = new[] { new { logicalId = "triage", displayName = "Triage", purpose = "Classify", responsibilities = new[] { "Classify" }, capabilities = new[] { "Text analysis" } } }
            }
        })), default);
        var planId = result!.Value.GetProperty("plan").GetProperty("id").GetProperty("value").GetGuid();

        var crossScope = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => get.ExecuteAsync(
            Invocation(context, "get-1", JsonSerializer.SerializeToElement(new { planId }), WorkspaceId.New()), default));
        Assert.AreEqual("resource_plan_not_found", crossScope.Code);

        var directMutation = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => create.ExecuteAsync(
            Invocation(context, "create-2", JsonSerializer.SerializeToElement(new { title = "Unsafe", goal = "Mutate", plan = new { }, resource = new { kind = "Agent" } })), default));
        Assert.AreEqual("resource_planning_argument_unknown", directMutation.Code);
    }

    private static InternalMcpToolInvocation Invocation(RequestContext context, string callId, JsonElement arguments, WorkspaceId? workspaceId = null) => new(
        context.TenantId, workspaceId ?? new WorkspaceId(context.WorkspaceId), context.PrincipalId, callId, "correlation-1", arguments,
        ToolDefinitionCallerKind.Agent, "agent:planner", "run-1", "step-1");
}
