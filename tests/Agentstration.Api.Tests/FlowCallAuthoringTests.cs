using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure.Flows;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public void NamedOutputsRoundTripThroughJsonAndYamlAndExposeEffectiveSchemas()
    {
        var successSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { answer = new { type = "string" } } });
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new OutputFlowStepDefinition
                {
                    Name = "approved",
                    DisplayName = "Approved",
                    Outcome = FlowOutputOutcome.Success,
                    Schema = successSchema,
                    OutputMapping = JsonSerializer.SerializeToElement("${transition.output}")
                },
                new OutputFlowStepDefinition
                {
                    Name = "rejected",
                    DisplayName = "Rejected",
                    Outcome = FlowOutputOutcome.Error,
                    Code = "REJECTED",
                    Message = "The request was rejected."
                }
            ],
            Transitions = [new("input-approved", "input", "completed", "approved")]
        };

        var json = JsonSerializer.Serialize(graph, JsonOptions);
        var jsonRestored = JsonSerializer.Deserialize<FlowGraphDefinition>(json, JsonOptions)!;
        var yaml = FlowDraftService.ToYaml(graph);
        var yamlRestored = new FlowDraftService(null!, null!, null!, TimeProvider.System).ParseSource(yaml, "yaml");

        CollectionAssert.AreEquivalent(new[] { "approved", "rejected" }, jsonRestored.GetOutputs().Select(output => output.Name).ToArray());
        Assert.AreEqual(FlowOutputOutcome.Success, jsonRestored.GetOutputs().Single(output => output.Name == "approved").Outcome);
        Assert.AreEqual("string", yamlRestored.GetOutputs().Single(output => output.Name == "approved").Schema?.GetProperty("properties").GetProperty("answer").GetProperty("type").GetString());
        StringAssert.Contains(json, "\"outcome\":\"success\"");
        StringAssert.Contains(yaml, "outcome: success");
    }

    [TestMethod]
    public void LegacyOutputAndFailureRemainReadableWithTheirPreviousDefaults()
    {
        const string json = """
            {"entryStep":"input","steps":[{"type":"input","name":"input"},{"type":"output","name":"output"},{"type":"failure","name":"failure"}],"transitions":[{"id":"done","fromStep":"input","event":"completed","toStep":"output"}]}
            """;

        var restored = JsonSerializer.Deserialize<FlowGraphDefinition>(json, JsonOptions)!;
        var output = Assert.IsInstanceOfType<OutputFlowStepDefinition>(restored.Steps[1]);
        var failure = Assert.IsInstanceOfType<FailureFlowStepDefinition>(restored.Steps[2]);

        Assert.IsNull(output.Outcome);
        Assert.AreEqual(FlowOutputOutcome.Success, restored.GetOutputs().Single(value => value.Name == "output").Outcome);
        Assert.AreEqual("FLOW_FAILED", failure.Code);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(restored, JsonOptions));
        var legacyOutput = serialized.RootElement.GetProperty("steps")[1];
        Assert.IsFalse(legacyOutput.TryGetProperty("outcome", out _));
        Assert.IsFalse(legacyOutput.TryGetProperty("schema", out _));
        Assert.IsFalse(legacyOutput.TryGetProperty("code", out _));
        Assert.IsFalse(legacyOutput.TryGetProperty("message", out _));
    }

    [TestMethod]
    public async Task OutputSchemaInferenceRejectsIncompatibleIncomingContracts()
    {
        var firstSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { first = new { type = "string" } } });
        var secondSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { second = new { type = "number" } } });
        var graph = new FlowGraphDefinition
        {
            EntryStep = "first",
            Steps =
            [
                new InputFlowStepDefinition { Name = "first", Schema = firstSchema },
                new InputFlowStepDefinition { Name = "second", Schema = secondSchema },
                new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success }
            ],
            Transitions =
            [
                new("first-completed", "first", "completed", "completed"),
                new("second-completed", "second", "completed", "completed")
            ]
        };

        var result = await new FlowGraphValidator(new FlowCallResolverStub(null!)).ValidateAsync(
            graph,
            new FlowValidationContext(ResolveResources: false),
            default);

        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "flow_output_schema_ambiguous"));
        Assert.IsNull(graph.GetOutputs().Single(output => output.Name == "completed").Schema);
    }

    [TestMethod]
    public void FlowCallRoundTripsThroughJsonAndYamlAndContributesToTheDefinitionHash()
    {
        var graph = Graph(new FlowCallStepDefinition
        {
            Name = "analyze",
            Flow = new("news-analysis", FlowCallVersionStrategy.Exact, "2.1.0", new("pack.news")),
            InputMapping = JsonSerializer.SerializeToElement(new { article = "${input.article}" })
        });

        var json = JsonSerializer.Serialize(graph, JsonOptions);
        var restored = JsonSerializer.Deserialize<FlowGraphDefinition>(json, JsonOptions)!;
        var yaml = FlowDraftService.ToYaml(graph);
        var parser = new FlowDraftService(null!, null!, null!, TimeProvider.System);
        var yamlRestored = parser.ParseSource(yaml, "yaml");

        StringAssert.Contains(json, "\"type\":\"flow\"");
        StringAssert.Contains(json, "\"versionStrategy\":\"exact\"");
        var call = Assert.IsInstanceOfType<FlowCallStepDefinition>(restored.Steps[1]);
        Assert.AreEqual(FlowCallVersionStrategy.Exact, call.Flow.VersionStrategy);
        Assert.AreEqual("pack.news", call.Flow.Namespace?.Value);
        Assert.AreEqual(FlowDefinitionHash.Compute(graph), FlowDefinitionHash.Compute(yamlRestored));
        Assert.AreNotEqual(FlowDefinitionHash.Compute(graph), FlowDefinitionHash.Compute(graph with
        {
            Steps = graph.Steps.Select(step => step is FlowCallStepDefinition current
                ? current with { Flow = current.Flow with { Version = "2.2.0" } }
                : step).ToArray()
        }));
    }

    [TestMethod]
    public async Task FlowCallValidationUsesWorkspaceVersionSchemasAndRejectsIncompatibleMappings()
    {
        var inputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { article = new { type = "string" }, locale = new { type = "string" } },
            required = new[] { "article" }
        });
        var resolver = new FlowCallResolverStub(new(new("analysis", new("pack.news")), "3.0.0", inputSchema, JsonSerializer.SerializeToElement(new { type = "object" })));
        var graph = Graph(new FlowCallStepDefinition
        {
            Name = "analyze",
            Flow = new("analysis", FlowCallVersionStrategy.Active, Namespace: new("pack.news")),
            InputMapping = JsonSerializer.SerializeToElement(new { unexpected = "${input.article}" })
        });

        var result = await new FlowGraphValidator(resolver).ValidateAsync(
            graph,
            new FlowValidationContext(true, TestScope.WorkspaceId, new("parent")),
            default);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "flow_input_mapping_unknown"));
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "flow_input_mapping_required"));
        Assert.AreEqual(TestScope.WorkspaceId, resolver.WorkspaceId);
        Assert.AreEqual(ResourceNamespace.Default, resolver.OwnerNamespace);
    }

    [TestMethod]
    [DataRow("${input}")]
    [DataRow("${transition.output}")]
    public async Task FlowCallValidationAcceptsCompleteInputPassthrough(string inputMapping)
    {
        var inputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { article = new { type = "string" } },
            required = new[] { "article" }
        });
        var resolver = new FlowCallResolverStub(new(new("analysis"), "3.0.0", inputSchema, JsonSerializer.SerializeToElement(new { type = "object" })));
        var graph = Graph(new FlowCallStepDefinition
        {
            Name = "analyze",
            Flow = new("analysis"),
            InputMapping = JsonSerializer.SerializeToElement(inputMapping)
        });

        var result = await new FlowGraphValidator(resolver).ValidateAsync(
            graph,
            new FlowValidationContext(true, TestScope.WorkspaceId, new("parent")),
            default);

        Assert.IsTrue(result.IsValid, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
    }

    [TestMethod]
    public async Task FlowCallValidationRejectsTransitionsThatDoNotMatchNamedOutputs()
    {
        var outputs = new[]
        {
            new FlowOutputDefinition("completed", "Completed", FlowOutputOutcome.Success, null),
            new FlowOutputDefinition("error", "Error", FlowOutputOutcome.Error, null)
        };
        var resolver = new FlowCallResolverStub(new(new("analysis"), "3.0.0", null, null, outputs));
        var graph = Graph(new FlowCallStepDefinition { Name = "analyze", Flow = new("analysis") });
        graph = graph with
        {
            Transitions = graph.Transitions
                .Select(transition => transition.Id == "call-output" ? transition with { Event = "failed" } : transition)
                .ToArray()
        };

        var result = await new FlowGraphValidator(resolver).ValidateAsync(
            graph,
            new FlowValidationContext(true, TestScope.WorkspaceId, new("parent")),
            default);

        var issue = result.Issues.Single(issue => issue.Code == "transition_event_invalid");
        Assert.AreEqual("call-output", issue.TransitionId);
        Assert.AreEqual("analyze", issue.StepId);
    }

    [TestMethod]
    public async Task RepositoryResolverFindsNamespacedPublishedVersionsAndIndirectCycles()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var parent = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand(
            "parent", null, "1.0.0", true, PlaceholderDefinition(), Graph: Graph()), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, parent.Value.Id, "1.0.0", true, default);

        var childGraph = Graph(new FlowCallStepDefinition
        {
            Name = "parent-call",
            Flow = new("parent", FlowCallVersionStrategy.Exact, "1.0.0"),
            InputMapping = JsonSerializer.SerializeToElement(new { })
        });
        var child = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand(
            "child", null, "1.0.0", true, PlaceholderDefinition(), Graph: childGraph), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, child.Value.Id, "1.0.0", true, default);

        var resolver = new ManagementFlowResourceReferenceResolver(null!, fixture.Repository);
        var target = await resolver.ResolveFlowAsync(TestScope.WorkspaceId, ResourceNamespace.Default, new("child"), default);

        Assert.IsNotNull(target);
        Assert.AreEqual("1.0.0", target.Version);
        CollectionAssert.Contains(target.Outputs!.Select(output => output.Name).ToArray(), "output");
        Assert.IsTrue(await resolver.CreatesFlowCycleAsync(TestScope.WorkspaceId, parent.Value.Id, target, default));
        var otherWorkspace = new WorkspaceId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        Assert.IsNull(await resolver.ResolveFlowAsync(otherWorkspace, ResourceNamespace.Default, new("child"), default));
    }

    private static FlowGraphDefinition Graph(FlowCallStepDefinition? call = null)
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { article = new { type = "string" } } });
        var steps = new List<FlowStepDefinition> { new InputFlowStepDefinition { Name = "input", Schema = schema } };
        if (call is not null) steps.Add(call);
        steps.Add(new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement(new { result = call is null ? "${input.article}" : $"${{steps.{call.Name}.output}}" }) });
        if (call is not null) steps.Add(new OutputFlowStepDefinition { Name = "error", Outcome = FlowOutputOutcome.Error });
        var transitions = call is null
            ? new[] { new FlowTransitionDefinition("input-output", "input", "completed", "output") }
            : new[]
            {
                new FlowTransitionDefinition("input-call", "input", "completed", call.Name),
                new FlowTransitionDefinition("call-output", call.Name, "completed", "output"),
                new FlowTransitionDefinition("call-error", call.Name, "error", "error")
            };
        return new FlowGraphDefinition { EntryStep = "input", InputSchema = schema, OutputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { result = new { } } }), Steps = steps, Transitions = transitions };
    }

    private static RoutingFlowDefinition PlaceholderDefinition() =>
        new(FlowRoutingStrategy.Deterministic, [new(FlowTargetKind.Agent, "placeholder")]);

    private sealed class FlowCallResolverStub(ResolvedFlowCall resolved) : IFlowResourceReferenceResolver
    {
        public WorkspaceId? WorkspaceId { get; private set; }
        public ResourceNamespace? OwnerNamespace { get; private set; }
        public Task<bool> ExistsAsync(string resourceId, ResourceNamespace? @namespace, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<ResolvedFlowCall?> ResolveFlowAsync(WorkspaceId workspaceId, ResourceNamespace ownerNamespace, FlowCallReference reference, CancellationToken cancellationToken)
        {
            WorkspaceId = workspaceId;
            OwnerNamespace = ownerNamespace;
            return Task.FromResult<ResolvedFlowCall?>(resolved);
        }
    }
}
