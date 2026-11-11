using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
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
        Assert.IsTrue(pinned.All(call => call.Flow.VersionStrategy == FlowCallVersionStrategy.Exact && call.Flow.Version == "1.0.0" && call.Flow.ResourceUid is not null && call.Flow.DefinitionHash is not null));
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
        Assert.AreEqual("2.0.0", next.Receipt!.Bindings["prepare"][0].Flow.Version);
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
