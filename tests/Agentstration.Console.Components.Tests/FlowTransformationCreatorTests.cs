using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class KnowledgeSourceEditorTests
{
    [TestMethod]
    public void OverviewDescribesTheOrderedInputTransformationsInsteadOfAnIdentityTransformation()
    {
        var source = ProjectedSource();
        var original = source.Definition.DataSources[0];
        source = source with { Definition = source.Definition with { DataSources = [original with { Transformations = [
            new() { Flow = new("redact", FlowCallVersionStrategy.Exact, "2.0.0", ResourceNamespace.Default, FlowClientStub.WorkspaceScope) },
            new() { Flow = new("verify", FlowCallVersionStrategy.Exact, "1.0.0", ResourceNamespace.Default, FlowClientStub.WorkspaceScope) }] }] } };
        using var context = CreateContext(new KnowledgeClientStub(source));
        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters.Add(value => value.Name, source.Name));
        rendered.WaitForAssertion(() => Assert.Contains("default/redact:2.0.0 → default/verify:1.0.0", rendered.Find("tbody").TextContent));
    }

    [TestMethod]
    public void ReopeningASequenceSelectsItsSavedVersionRatherThanTheFirstCatalogEntry()
    {
        using var context = CreateContext(new KnowledgeClientStub());
        var input = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = ManifestValueSchema, parameters = new { type = "object" } } });
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub { TransformationVersions = ["1.0.0", "2.0.0"],
            SequenceGraph = new() { EntryStep = "input", InputSchema = input, OutputSchema = ManifestValueSchema } });
        var call = new FlowCompositionCall { Flow = new("artifact-transform-builtin", FlowCallVersionStrategy.Exact, "2.0.0", ResourceNamespace.Default, FlowClientStub.WorkspaceScope) };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var rendered = context.Render<FlowSequenceEditor>(parameters => parameters.Add(value => value.Value, [call]));
            var selected = rendered.Find("[data-testid=flow-sequence-target] option[selected]");
            Assert.EndsWith("|2.0.0", selected.GetAttribute("value"));
            Assert.Contains(":2.0.0", selected.TextContent);
            rendered.Find("[data-testid=flow-sequence-refresh]").Click();
            Assert.EndsWith("|2.0.0", rendered.Find("[data-testid=flow-sequence-target] option[selected]").GetAttribute("value"));
            rendered.Dispose();
        }
    }

    [TestMethod]
    [DataRow("en-US", "No published transformation Flow", "Create a transformation Flow")]
    [DataRow("fr-FR", "Aucun Flow de transformation publié", "Créer un flow de transformation")]
    public void EmptyTransformationCatalogExplainsCreationAndHonorsReadOnly(string locale, string empty, string create)
    {
        using var culture = new TestCultureScope(locale);
        using var context = CreateContext(new KnowledgeClientStub());
        var rendered = context.Render<FlowSequenceEditor>(parameters => parameters
            .Add(value => value.InputSchema, ManifestValueSchema).Add(value => value.OutputSchema, ManifestValueSchema));
        Assert.IsTrue(rendered.FindAll("[role=status]").Any(element => element.TextContent.Contains(empty, StringComparison.Ordinal)));
        Assert.IsTrue(rendered.Find("[data-testid='flow-sequence-add']").HasAttribute("disabled"));
        Assert.IsFalse(rendered.Find("[data-testid='flow-sequence-refresh']").HasAttribute("disabled"));
        var button = rendered.Find("[data-testid='flow-sequence-create']");
        Assert.AreEqual(create, button.TextContent);
        Assert.IsFalse(button.HasAttribute("disabled"));
        rendered.Render(parameters => parameters.Add(value => value.ReadOnly, true));
        Assert.IsTrue(rendered.Find("[data-testid='flow-sequence-create']").HasAttribute("disabled"));
    }

    [TestMethod]
    public void TransformationSelectionReadsTheCanonicalInputStepSchema()
    {
        using var context = CreateContext(new KnowledgeClientStub());
        var input = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = ManifestValueSchema, parameters = new { type = "object" } } });
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub { SequenceGraph = new()
        {
            EntryStep = "input", OutputSchema = ManifestValueSchema,
            Steps = [new InputFlowStepDefinition { Name = "input", Schema = input }]
        } });
        IReadOnlyList<FlowCompositionCall> calls = [];
        var rendered = context.Render<FlowSequenceEditor>(parameters => parameters.Add(value => value.ValueChanged, values => calls = values));
        Assert.IsFalse(rendered.Find("[data-testid='flow-sequence-add']").HasAttribute("disabled"));
        rendered.Find("[data-testid='flow-sequence-add']").Click();
        rendered.Render(parameters => parameters.Add(value => value.Value, calls));
        StringAssert.Contains(rendered.Find("[data-testid='flow-sequence-target']").TextContent, "artifact-transform-builtin");
    }

    [TestMethod]
    [DataRow("en-US", false, "Draft created.")]
    [DataRow("fr-FR", false, "Brouillon créé.")]
    [DataRow("en-US", true, "The draft could not be created.")]
    [DataRow("fr-FR", true, "Le brouillon n’a pas pu être créé.")]
    public void TransformationCreationUsesTheCurrentOwnerAndPreservesSourceState(string locale, bool fail, string result)
    {
        using var culture = new TestCultureScope(locale);
        using var context = CreateContext(new KnowledgeClientStub());
        var client = new FlowClientStub { FailCreation = fail };
        context.Services.AddSingleton<IFlowApiClient>(client);
        context.Services.AddSingleton(new ConsoleContextState(new TransformationContextProvider()));
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var original = navigation.Uri;
        var changed = false;
        var rendered = context.Render<FlowSequenceEditor>(parameters => parameters
            .Add(value => value.InputSchema, ManifestValueSchema).Add(value => value.OutputSchema, ManifestValueSchema)
            .Add(value => value.ValueChanged, _ => changed = true));
        rendered.Find("[data-testid='flow-sequence-create']").Click();
        rendered.Find("[data-testid='flow-transformation-display-name']").Input("New transformation");
        rendered.WaitForAssertion(() => Assert.AreEqual("new-transformation", rendered.Find("[data-testid='flow-transformation-name']").GetAttribute("value")));
        rendered.Find("[data-testid='flow-transformation-create']").Click();
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.FindAll("[role=status], [role=alert]").Any(element => element.TextContent.Contains(result, StringComparison.Ordinal))));
        Assert.AreEqual(original, navigation.Uri);
        Assert.IsFalse(changed);
        if (fail) Assert.IsNull(client.CreatedTransformation);
        else
        {
            Assert.AreEqual(ResourceScopeRef.Instance, client.CreatedTransformation!.ScopeRef);
            Assert.IsTrue(JsonElement.DeepEquals(ManifestValueSchema, client.CreatedTransformation.Transformation!.InputSchema));
            Assert.IsTrue(JsonElement.DeepEquals(ManifestValueSchema, client.CreatedTransformation.Transformation.OutputSchema));
            StringAssert.Contains(rendered.Find("[data-testid='flow-transformation-designer']").GetAttribute("href"), "context=%2Finstance");
            Assert.AreEqual(1, context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "agentstrationDataSources.openDesigner"));
            rendered.Find("[data-testid='flow-sequence-create']").Click();
            Assert.AreEqual(string.Empty, rendered.Find("[data-testid='flow-transformation-display-name']").GetAttribute("value"));
        }
    }

    [TestMethod]
    public void AppendedTransformationUsesThePreviousOutputAndSurvivesCatalogRefresh()
    {
        using var context = CreateContext(new KnowledgeClientStub());
        var array = JsonSerializer.SerializeToElement(new { type = "array", items = new { type = "object" } });
        var input = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "object" }, parameters = new { type = "object" } } });
        var client = new FlowClientStub { SequenceGraph = new()
        {
            EntryStep = "input", InputSchema = input, OutputSchema = array
        } };
        context.Services.AddSingleton<IFlowApiClient>(client);
        context.Services.AddSingleton(new ConsoleContextState(new TransformationContextProvider()));
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var original = new FlowCompositionCall { Flow = new("artifact-transform-builtin", FlowCallVersionStrategy.Exact, "1.0.0", ResourceNamespace.Default, FlowClientStub.WorkspaceScope) };
        var rendered = context.Render<FlowSequenceEditor>(parameters => parameters
            .Add(value => value.Value, [original]).Add(value => value.InputSchema, ManifestValueSchema).Add(value => value.OutputSchema, array));
        rendered.Find("[data-testid='flow-sequence-create']").Click();
        rendered.Find("[data-testid='flow-transformation-display-name']").Input("Next transformation");
        rendered.Find("[data-testid='flow-transformation-create']").Click();
        rendered.WaitForAssertion(() => Assert.IsNotNull(client.CreatedTransformation));
        Assert.IsTrue(JsonElement.DeepEquals(array, client.CreatedTransformation!.Transformation!.InputSchema));
        Assert.IsTrue(JsonElement.DeepEquals(array, client.CreatedTransformation.Transformation.OutputSchema));
        Assert.HasCount(1, rendered.FindAll("[data-testid='flow-sequence-call']"));
        Assert.HasCount(1, rendered.FindAll("[data-testid='flow-transformation-designer']"));
    }

    private static JsonElement ManifestValueSchema => JsonSerializer.SerializeToElement(new { type = "object", properties = new { artifacts = new { type = "array" } }, required = new[] { "artifacts" }, additionalProperties = false });
    private sealed class TransformationContextProvider : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Test", "/instance", ConsoleContextKind.Instance, Guid.NewGuid(), "tenant", "Tenant", Guid.NewGuid(), "workspace", "Workspace", new HashSet<string>(), [], []));
    }
}
