using Agentstration.Flows;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Resources;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class RuntimeExecutionMaterialResolverTests
{
    [TestMethod]
    public void GraphMaterialIncludesRoutedAndStaticAgentsButNotDynamicExpressions()
    {
        var graph = new FlowGraphDefinition
        {
            EntryStep = "router",
            Steps =
            [
                new RouterFlowStepDefinition
                {
                    Name = "router",
                    Candidates =
                    [
                        new("sql", new("sql-expert")),
                        new("dotnet", new("dotnet-expert"))
                    ],
                    Fallback = new("sql-expert")
                },
                new AgentFlowStepDefinition
                {
                    Name = "dynamic-agent",
                    Agent = new("${steps.router.output.selectedAgent}")
                },
                new AgentFlowStepDefinition { Name = "reviewer", Agent = new("reviewer") }
            ]
        };
        var version = new FlowVersion(ResourceScopeRef.Workspace(Guid.NewGuid()),
            new FlowId("routed", ResourceNamespace.Default), "1.0", null,
            new DirectFlowDefinition(new(FlowTargetKind.Agent, "unused")),
            new Dictionary<string, string>(), DateTimeOffset.UtcNow, graph, "sha256:routed");

        var references = RuntimeExecutionMaterialResolver.ExecutionAgentReferences(version);

        CollectionAssert.AreEquivalent(
            new[] { "reviewer", "sql-expert", "dotnet-expert" },
            references.Select(value => value.Target.Id).ToArray());
        Assert.IsFalse(references.Any(value => value.Target.Id.StartsWith("${", StringComparison.Ordinal)));
        Assert.AreEqual(1, references.Count(value => value.Target.Id == "sql-expert"));
    }
}
