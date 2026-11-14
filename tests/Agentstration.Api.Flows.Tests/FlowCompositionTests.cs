using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task CompositionDiagnosticsIdentifyTheFailingBoundaryWithoutCopyingInputValues()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var union = JsonSerializer.Deserialize<JsonElement>("""{"type":["object","array","string","number","boolean","null"]}""");
        var child = CompositionChild() with { OutputSchema = union };
        child = child with { Steps = child.Steps.Select(step => step is OutputFlowStepDefinition output ? output with { Schema = union } : step).ToArray() };
        await CreatePublishedGraphAsync(fixture, "union-output", child);
        var parent = await CreatePublishedGraphAsync(fixture, "typed-boundary", CompositionTemplate());
        var template = (await fixture.Service.GetVersionAsync(TestScope.WorkspaceId, parent.Value.Id, "1.0.0", default))!.Value;
        var error = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => new FlowCompositionService(fixture.Service).ResolveAsync(template,
            new Dictionary<string, IReadOnlyList<FlowCompositionCall>> { ["prepare"] = [new() { Flow = new("union-output"),
                Parameters = JsonSerializer.SerializeToElement(new { label = "private-instance-value" }) }] }, TestScope.AccessScopeRef, default));
        Assert.AreEqual("flow_composition_contract_incompatible", error.Code);
        Assert.IsNotNull(error.CompositionDiagnostic);
        Assert.AreEqual("prepare", error.CompositionDiagnostic.Point);
        Assert.AreEqual("output", error.CompositionDiagnostic.Boundary);
        Assert.AreEqual("1.0.0", error.CompositionDiagnostic.Version);
        Assert.AreEqual("object|array|string|number|boolean|null", error.CompositionDiagnostic.ActualType);
        Assert.AreEqual("object", error.CompositionDiagnostic.ExpectedType);
        Assert.DoesNotContain("private-instance-value", JsonSerializer.Serialize(error.CompositionDiagnostic));
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public async Task OnlyGovernedBoundaryCallsCanSupplyAnUndeclaredOperationOutputContract(bool direct, bool supplyContract, bool declareIncompatibleOutput)
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var child = CompositionChild() with { OutputSchema = declareIncompatibleOutput ? JsonSerializer.SerializeToElement(new { type = "array" }) : null };
        child = child with
        {
            Steps = [child.Steps.OfType<InputFlowStepDefinition>().Single(),
                new TransformFlowStepDefinition { Name = "read", Schema = JsonSerializer.SerializeToElement(new { }), Mapping = JsonSerializer.SerializeToElement(new { acquired = true }) },
                new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.read.output}") }],
            Transitions = [new("input-read", "input", "completed", "read"), new("read-output", "read", "completed", "output")]
        };
        await CreatePublishedGraphAsync(fixture, "dynamic-operation", child);
        var parent = await CreatePublishedGraphAsync(fixture, "dynamic-boundary", CompositionTemplate());
        var template = (await fixture.Service.GetVersionAsync(TestScope.WorkspaceId, parent.Value.Id, "1.0.0", default))!.Value;
        var bindings = new Dictionary<string, IReadOnlyList<FlowCompositionCall>>
        {
            ["prepare"] = [new() { Flow = new("dynamic-operation")
                { OutputSchema = supplyContract ? JsonSerializer.SerializeToElement(new { type = "object" }) : null },
                PassInputDirectly = direct,
                Parameters = direct ? JsonSerializer.SerializeToElement(new { }) : JsonSerializer.SerializeToElement(new { label = "literal" }) }]
        };
        var compiler = new FlowCompositionService(fixture.Service);
        if (direct && supplyContract && !declareIncompatibleOutput)
        {
            var compiled = await compiler.ResolveAsync(template, bindings, TestScope.AccessScopeRef, default);
            Assert.IsNotNull(compiled.Receipt!.Bindings["prepare"].Single().Flow!.OutputSchema);
            Assert.IsTrue(compiled.Definition.Graph!.Steps.OfType<TransformFlowStepDefinition>().Any(step => step.Name == "extensions" && step.Schema is not null));
        }
        else
        {
            var error = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => compiler.ResolveAsync(template, bindings, TestScope.AccessScopeRef, default));
            Assert.AreEqual("flow_composition_contract_incompatible", error.Code);
        }
    }

    [TestMethod]
    [DataRow(65_537, true)]
    [DataRow(1_048_576, true)]
    [DataRow(1_048_577, false)]
    public void CompositionValuesHaveAnIndependentBoundedPayloadBudget(int length, bool accepted)
    {
        using var document = JsonDocument.Parse("{\"value\":\"" + new string('x', length - 12) + "\"}");
        Assert.AreEqual(length, document.RootElement.GetRawText().Length);
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        if (accepted) FlowCompositionSchema.ValidateValue(schema, document.RootElement);
        else
        {
            var error = Assert.ThrowsExactly<FlowValidationException>(() => FlowCompositionSchema.ValidateValue(schema, document.RootElement));
            Assert.AreEqual("flow_composition_value_too_large", error.Code);
        }
    }

    [TestMethod]
    public async Task CompositionParametersKeepTheirSmallerLiteralBudget()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "bounded-parameters-child", CompositionChild());
        var parent = await CreatePublishedGraphAsync(fixture, "bounded-parameters-parent", CompositionTemplate());
        var template = (await fixture.Service.GetVersionAsync(TestScope.WorkspaceId, parent.Value.Id, "1.0.0", default))!.Value;
        var error = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => new FlowCompositionService(fixture.Service).ResolveAsync(template,
            new Dictionary<string, IReadOnlyList<FlowCompositionCall>>
            {
                ["prepare"] = [new() { Flow = new("bounded-parameters-child"), Parameters = JsonSerializer.SerializeToElement(new { label = new string('x', 65_536) }) }]
            }, TestScope.AccessScopeRef, default));
        Assert.AreEqual("flow_composition_parameters_invalid", error.Code);
    }

    [TestMethod]
    public async Task ChildValidationRejectionsRemainGovernedFailuresAndAllowAValidRetry()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "child", ChildGraph());
        var parent = await CreatePublishedGraphAsync(fixture, "parent", ParentGraph());
        var runs = Service(fixture, new TestFlowRunQueue());
        var input = JsonSerializer.SerializeToElement(new { article = "valid" });
        var root = await runs.CreateAsync(parent.Value.Id, "1.0.0", "local", FlowRunTrigger.Manual, "tester", "validation", input, TestScope, default);
        var gateway = new RuntimeWorkerOperationGateway(null!, null!, null!, null!, fixture.Repository, null!, null!, null!, runs,
            TimeProvider.System, new Agentstration.Identity.CurrentRequestContext(), NullLogger<RuntimeWorkerOperationGateway>.Instance);
        var error = await Assert.ThrowsExactlyAsync<RuntimeGovernedOperationException>(() => gateway.CreateOrGetChildFlowAsync(
            TestScope.ScopeRef, TestScope.WorkspaceId, root.Value.Id, "analyze", JsonSerializer.SerializeToElement(new { article = 42 }), "flowCall", null, default));
        Assert.AreEqual("flow_input_type_invalid", error.Code);
        Assert.IsInstanceOfType<FlowValidationException>(error.InnerException);
        Assert.HasCount(1, (await runs.ListAsync(null, null, 0, 20, TestScope, default)).Items);
        var child = await gateway.CreateOrGetChildFlowAsync(TestScope.ScopeRef, TestScope.WorkspaceId, root.Value.Id, "analyze", input, "flowCall", null, default);
        Assert.IsFalse(string.IsNullOrWhiteSpace(child.RunId));
        Assert.HasCount(2, (await runs.ListAsync(null, null, 0, 20, TestScope, default)).Items);
    }

    [TestMethod]
    public async Task CompositionTranslatesOutsideScopeReferencesIntoValidationFailures()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "scoped-composition-child", CompositionChild());
        var compiler = new FlowCompositionService(fixture.Service);
        var forbidden = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => compiler.ResolveCallAsync(
            new("scoped-composition-child", ScopeRef: ResourceScopeRef.Workspace(Guid.NewGuid())),
            TestScope.AccessScopeRef, ResourceNamespace.Default, default));
        Assert.AreEqual("flow_composition_child_unavailable", forbidden.Code);
    }

    [TestMethod]
    public async Task CompositionPinsOrderedCallsAndLiteralParametersWithoutRemovingFixedSteps()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "composed-child", CompositionChild());
        var parent = await CreatePublishedGraphAsync(fixture, "composed-parent", CompositionTemplate());
        var template = (await fixture.Service.GetVersionAsync(TestScope.WorkspaceId, parent.Value.Id, "1.0.0", default))!.Value;
        var compiler = new FlowCompositionService(fixture.Service);
        var calls = new FlowCompositionCall[]
        {
            new() { Flow = new("composed-child"), Parameters = JsonSerializer.SerializeToElement(new { label = "${input.keepLiteral}" }) },
            new() { Flow = new("composed-child"), Parameters = JsonSerializer.SerializeToElement(new { label = "second" }) }
        };
        var accepted = await compiler.ResolveAsync(template, new Dictionary<string, IReadOnlyList<FlowCompositionCall>> { ["prepare"] = calls }, TestScope.AccessScopeRef, default);
        var pinned = accepted.Receipt!.Bindings["prepare"];
        Assert.HasCount(2, pinned);
        Assert.IsTrue(pinned.All(call => call.Flow is { VersionStrategy: FlowCallVersionStrategy.Exact, Version: "1.0.0", ResourceUid: not null, DefinitionHash: not null }));
        var generated = accepted.Definition.Graph!.Steps.OfType<FlowCallStepDefinition>().ToArray();
        Assert.HasCount(2, generated);
        Assert.AreEqual("${input.keepLiteral}", generated[0].CompositionParameters?.GetProperty("label").GetString());
        Assert.AreEqual("${steps.anonymize.output}", generated[0].InputMapping?.GetString());
        Assert.IsTrue(accepted.Definition.Graph.Steps.Any(step => step.Name == "anonymize"));
        Assert.AreEqual(generated[1].Name, accepted.Definition.Graph.Transitions.Single(transition => transition.FromStep == generated[0].Name).ToStep);
        Assert.AreNotEqual(accepted.Receipt.TemplateHash, accepted.Receipt.ExecutableHash);
        var child = (await fixture.Service.GetAsync(TestScope.WorkspaceId, new("composed-child"), default))!;
        await fixture.Service.UpdateAsync(TestScope.WorkspaceId, child.Value.Id, new(null, "2.0.0", true, child.Value.Definition, Graph: CompositionChild()), child.ETag, default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, child.Value.Id, "2.0.0", true, default);
        var resumed = await compiler.ResolveAsync(template, accepted.Receipt.Bindings, TestScope.AccessScopeRef, default);
        Assert.AreEqual(accepted.Receipt.ExecutableHash, resumed.Receipt!.ExecutableHash);
        var next = await compiler.ResolveAsync(template, new Dictionary<string, IReadOnlyList<FlowCompositionCall>> { ["prepare"] = calls }, TestScope.AccessScopeRef, default);
        Assert.AreEqual("2.0.0", next.Receipt!.Bindings["prepare"][0].Flow!.Version);
    }

    [TestMethod]
    public async Task EmptyCompositionPreservesFixedAnonymizationAndRequiredPointRefusesAdmission()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var parent = await CreatePublishedGraphAsync(fixture, "identity-composition", CompositionTemplate());
        var template = (await fixture.Service.GetVersionAsync(TestScope.WorkspaceId, parent.Value.Id, "1.0.0", default))!.Value;
        var compiler = new FlowCompositionService(fixture.Service);
        var empty = new Dictionary<string, IReadOnlyList<FlowCompositionCall>>();
        var compiled = await compiler.ResolveAsync(template, empty, TestScope.AccessScopeRef, default);
        Assert.HasCount(0, compiled.Definition.Graph!.Steps.OfType<FlowCallStepDefinition>().ToArray());
        Assert.AreEqual("${steps.anonymize.output}", compiled.Definition.Graph.Steps.OfType<TransformFlowStepDefinition>().Single(step => step.Name == "extensions").Mapping?.GetString());
        var required = template with { Graph = template.Graph! with { Steps = template.Graph.Steps.Select(step => step is FlowCompositionPointStepDefinition point ? point with { AllowEmpty = false } : step).ToArray() } };
        var missing = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => compiler.ResolveAsync(required, empty, TestScope.AccessScopeRef, default));
        Assert.AreEqual("flow_composition_binding_required", missing.Code);
        var unknown = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => compiler.ResolveAsync(template, new Dictionary<string, IReadOnlyList<FlowCompositionCall>> { ["unknown"] = [] }, TestScope.AccessScopeRef, default));
        Assert.AreEqual("flow_composition_point_unknown", unknown.Code);
    }

    [TestMethod]
    public async Task CompositionRejectsCyclesInvalidParametersAndIncompatibleChildContracts()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await CreatePublishedGraphAsync(fixture, "composed-child", CompositionChild());
        var parent = await CreatePublishedGraphAsync(fixture, "composed-parent", CompositionTemplate());
        var template = (await fixture.Service.GetVersionAsync(TestScope.WorkspaceId, parent.Value.Id, "1.0.0", default))!.Value;
        var compiler = new FlowCompositionService(fixture.Service);
        var invalid = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => compiler.ResolveAsync(template,
            new Dictionary<string, IReadOnlyList<FlowCompositionCall>> { ["prepare"] = [new() { Flow = new("composed-child"), Parameters = JsonSerializer.SerializeToElement(new { label = 42 }) }] }, TestScope.AccessScopeRef, default));
        Assert.AreEqual("flow_composition_value_invalid", invalid.Code);
        var cycle = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => compiler.ResolveAsync(template,
            new Dictionary<string, IReadOnlyList<FlowCompositionCall>> { ["prepare"] = [new() { Flow = new("composed-parent") }] }, TestScope.AccessScopeRef, default));
        Assert.AreEqual("flow_composition_cycle", cycle.Code);
        var child = CompositionChild() with { InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "string" }, parameters = new { type = "object" } } }) };
        child = child with { Steps = child.Steps.Select(step => step is InputFlowStepDefinition inputStep ? inputStep with { Schema = child.InputSchema } : step).ToArray() };
        await CreatePublishedGraphAsync(fixture, "wrong-contract", child);
        var incompatible = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => compiler.ResolveAsync(template,
            new Dictionary<string, IReadOnlyList<FlowCompositionCall>> { ["prepare"] = [new() { Flow = new("wrong-contract") }] }, TestScope.AccessScopeRef, default));
        Assert.AreEqual("flow_composition_contract_incompatible", incompatible.Code);
    }

    [TestMethod]
    public void CompositionSchemaRejectsUnsupportedKeywordsAndChecksOptionalPropertyTypes()
    {
        Assert.IsFalse(FlowCompositionSchema.IsAssignable(JsonSerializer.SerializeToElement(new { type = "object" }),
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { count = new { type = "integer" } } })));
        Assert.IsFalse(FlowCompositionSchema.IsSupported(JsonSerializer.SerializeToElement(new { type = new[] { "string", "null" } })));
        Assert.IsFalse(FlowCompositionSchema.IsSupported(JsonSerializer.SerializeToElement(new { type = "string", not = new { type = "string" } })));
        Assert.ThrowsExactly<FlowValidationException>(() => FlowCompositionSchema.ValidateValue(JsonSerializer.SerializeToElement(new { type = "string", pattern = "^[a-z]+$" }), JsonSerializer.SerializeToElement("42")));
    }

    [TestMethod]
    public void UnconstrainedOutputTargetsAndNativeParameterValuesRetainRuntimeValidation()
    {
        var any = JsonSerializer.SerializeToElement(new { });
        var objectSchema = JsonSerializer.SerializeToElement(new { type = "object" });
        Assert.IsTrue(FlowCompositionSchema.IsAssignable(objectSchema, any));
        Assert.IsTrue(FlowCompositionSchema.IsAssignable(any, any));
        Assert.IsFalse(FlowCompositionSchema.IsAssignable(any, objectSchema));
        var openApiObject = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"value":{"type":["object","array","string","number","boolean","null"]},"number":{"type":"integer","format":"int64"}}}""");
        Assert.IsTrue(FlowCompositionSchema.IsAssignable(openApiObject, objectSchema));
        Assert.IsFalse(FlowCompositionSchema.IsAssignable(openApiObject, JsonSerializer.Deserialize<JsonElement>("""{"type":"object","required":["missing"]}""")));
        var parameters = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"equals":{}},"required":["equals"],"additionalProperties":false}""");
        var value = JsonSerializer.SerializeToElement(new { equals = new { active = true } });
        Assert.ThrowsExactly<FlowValidationException>(() => FlowCompositionSchema.ValidateValue(parameters, value));
        FlowCompositionSchema.ValidateValue(parameters, value, allowDynamicSchema: true);
        Assert.ThrowsExactly<FlowValidationException>(() => FlowCompositionSchema.ValidateValue(parameters, any, allowDynamicSchema: true));
    }

    private static FlowGraphDefinition CompositionTemplate()
    {
        var value = JsonSerializer.SerializeToElement(new { type = "object" });
        return new()
        {
            EntryStep = "input",
            InputSchema = value,
            OutputSchema = value,
            Steps = [new InputFlowStepDefinition { Name = "input", Schema = value },
                new TransformFlowStepDefinition { Name = "anonymize", Mapping = JsonSerializer.SerializeToElement(new { redacted = true }), Schema = value },
                new FlowCompositionPointStepDefinition { Name = "extensions", Point = "prepare", InputSchema = value, OutputSchema = value, InputMapping = JsonSerializer.SerializeToElement("${steps.anonymize.output}") },
                new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success, Schema = value, OutputMapping = JsonSerializer.SerializeToElement("${transition.output}") }],
            Transitions = [new("input-fixed", "input", "completed", "anonymize"), new("fixed-point", "anonymize", "completed", "extensions"), new("point-output", "extensions", "completed", "completed")]
        };
    }
    private static FlowGraphDefinition CompositionChild()
    {
        var value = JsonSerializer.SerializeToElement(new { type = "object" });
        var input = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value, parameters = new { type = "object", properties = new { label = new { type = "string" } }, required = new[] { "label" }, additionalProperties = false } }, required = new[] { "value", "parameters" }, additionalProperties = false });
        return new()
        {
            EntryStep = "input",
            InputSchema = input,
            OutputSchema = value,
            Steps = [new InputFlowStepDefinition { Name = "input", Schema = input }, new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success, Schema = value, OutputMapping = JsonSerializer.SerializeToElement("${input.value}") }],
            Transitions = [new("next", "input", "completed", "completed")]
        };
    }
}
