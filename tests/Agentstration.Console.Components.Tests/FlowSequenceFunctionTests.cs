using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Functions.Abstractions;
using Agentstration.Functions.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class KnowledgeSourceEditorTests
{
    [TestMethod]
    [DataRow("en-US", "Add a function", "Select a JSON value")]
    [DataRow("fr-FR", "Ajouter une fonction", "Extrait une valeur JSON")]
    public async Task FunctionSequenceEditsLiteralParametersAlongsideFlowsAndHonorsReadOnly(string locale, string addLabel, string description)
    {
        using var culture = new TestCultureScope(locale);
        using var context = CreateContext(new KnowledgeClientStub());
        var owner = new ConsoleContextState(new TransformationContextProvider());
        await owner.EnsureLoadedAsync(default);
        context.Services.AddSingleton(owner);
        context.Services.AddSingleton<IConsoleNavigationContext>(new KnowledgeNavigationContext("/instance"));
        context.Services.AddSingleton<ITransformationFunctionsClient>(new FunctionClientStub());
        IReadOnlyList<FlowCompositionCall> calls = [];
        var rendered = context.Render<FlowSequenceEditor>(parameters => parameters.Add(value => value.ValueChanged, values => calls = values));
        var add = rendered.Find("[data-testid=function-sequence-add]");
        Assert.Contains(locale == "fr-FR" ? "Les fonctions et les Flows" : "Functions and Flows", rendered.Markup);
        Assert.AreEqual(addLabel, add.TextContent);
        Assert.IsFalse(add.HasAttribute("disabled"));
        add.Click();
        rendered.Render(parameters => parameters.Add(value => value.Value, calls));
        Assert.AreEqual("select", calls.Single().Function!.Name);
        Assert.IsNull(calls.Single().Flow);
        Assert.Contains(description, rendered.Find("[data-testid=function-description]").TextContent);
        Assert.IsEmpty(rendered.FindAll("[data-schema-path='$.value']"));
        Assert.IsEmpty(rendered.FindAll("[data-testid=function-sequence-text-input-help]"));
        rendered.Find("[data-schema-path='$.path'] input, [data-schema-path='$.path'] textarea").Input("${keep.literal}");
        Assert.AreEqual("${keep.literal}", calls.Single().Parameters.GetProperty("path").GetString());
        rendered.Render(parameters => parameters.Add(value => value.Value, calls).Add(value => value.ReadOnly, true));
        Assert.IsTrue(rendered.Find("[data-testid=function-sequence-add]").HasAttribute("disabled"));
        Assert.IsTrue(rendered.Find("[data-schema-path='$.path'] input, [data-schema-path='$.path'] textarea").HasAttribute("disabled"));
        Assert.HasCount(1, rendered.FindAll("[data-testid=flow-sequence-add]"));
    }

    [TestMethod]
    [DataRow("en-US", "expects text", "JSON selection")]
    [DataRow("fr-FR", "attend un texte", "sélection JSON")]
    public async Task TextFunctionsExplainHowToExtractThePreviousResultsContent(string locale, string typeHint, string selectionHint)
    {
        using var culture = new TestCultureScope(locale);
        using var context = CreateContext(new KnowledgeClientStub());
        var owner = new ConsoleContextState(new TransformationContextProvider());
        await owner.EnsureLoadedAsync(default);
        context.Services.AddSingleton(owner);
        context.Services.AddSingleton<IConsoleNavigationContext>(new KnowledgeNavigationContext("/instance"));
        context.Services.AddSingleton<ITransformationFunctionsClient>(new FunctionClientStub(textInput: true));
        IReadOnlyList<FlowCompositionCall> calls = [];
        var rendered = context.Render<FlowSequenceEditor>(parameters => parameters.Add(value => value.ValueChanged, values => calls = values));
        rendered.Find("[data-testid=function-sequence-add]").Click();
        rendered.Render(parameters => parameters.Add(value => value.Value, calls));
        var help = rendered.Find("[data-testid=function-sequence-text-input-help]").TextContent;
        Assert.Contains(typeHint, help);
        Assert.Contains(selectionHint, help);
        Assert.Contains("/content", help);
        Assert.IsEmpty(rendered.FindAll("[data-schema-path='$.value']"));
        Assert.AreEqual(0, calls.Single().Parameters.EnumerateObject().Count());
    }

    [TestMethod]
    [DataRow("en-US", "receives the text content", "fixed when saved")]
    [DataRow("fr-FR", "reçoit le contenu texte", "figée à l’enregistrement")]
    public async Task FirstTextFunctionExplainsAcquisitionContentSelectionOnlyForTheResponseContract(string locale, string hint, string frozen)
    {
        using var culture = new TestCultureScope(locale);
        using var context = CreateContext(new KnowledgeClientStub());
        var owner = new ConsoleContextState(new TransformationContextProvider());
        await owner.EnsureLoadedAsync(default);
        context.Services.AddSingleton(owner);
        context.Services.AddSingleton<IConsoleNavigationContext>(new KnowledgeNavigationContext("/instance"));
        context.Services.AddSingleton<ITransformationFunctionsClient>(new FunctionClientStub(textInput: true));
        var response = JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"mediaType":{"type":"string"},"content":{"type":"string"}},"required":["mediaType","content"],"additionalProperties":false}
            """);
        var call = new FlowCompositionCall { Function = new("select", "1.0.0", ResourceScopeRef.Instance) };
        var rendered = context.Render<FlowSequenceEditor>(parameters => parameters
            .Add(value => value.Value, [call]).Add(value => value.AcquisitionOutputSchema, response));
        var help = rendered.Find("[data-testid=function-sequence-text-input-help]").TextContent;
        Assert.Contains(hint, help);
        Assert.Contains(frozen, help);
        Assert.DoesNotContain("/content", help);
        rendered.Render(parameters => parameters.Add(value => value.Value, [call, call]));
        Assert.DoesNotContain(hint, rendered.FindAll("[data-testid=function-sequence-text-input-help]")[1].TextContent);
        rendered.Render(parameters => parameters.Add(value => value.Value,
            [call with { FunctionInputSource = FlowCompositionFunctionInputSource.PreviousResult }]));
        Assert.DoesNotContain(hint, rendered.Find("[data-testid=function-sequence-text-input-help]").TextContent);
        rendered.Render(parameters => parameters.Add(value => value.Value, [call]).Add(value => value.AcquisitionOutputSchema,
            JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"content":{"type":"string"}}}""")));
        Assert.DoesNotContain(hint, rendered.Find("[data-testid=function-sequence-text-input-help]").TextContent);
    }

    private sealed class FunctionClientStub(bool textInput = false) : ITransformationFunctionsClient
    {
        public Task<IReadOnlyList<FunctionChoice>> ChoicesAsync(ResourceScopeRef scope, CancellationToken ct)
        {
            var descriptor = new NativeFunctionDescriptor("json.select", "1.0.0",
                JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"value":{},"path":{"type":"string","maxLength":1024}},"required":["value","path"],"additionalProperties":false}"""),
                JsonSerializer.Deserialize<JsonElement>("{}"));
            if (textInput) descriptor = descriptor with { Id = "example.text", InputSchema = FunctionInvocationSchemas.XmlContent };
            return Task.FromResult<IReadOnlyList<FunctionChoice>>([new("Select · 1.0.0", new(new("select", "1.0.0", scope), new("native", "1.0.0", scope),
                descriptor.Id, descriptor.Version, descriptor.InputSchema, descriptor.OutputSchema)) { IsBuiltIn = true }]);
        }
        public Task<IReadOnlyList<TransformationFunctionResource>> ListAsync(ResourceScopeRef? scope, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FunctionKindResource>> ListKindsAsync(ResourceScopeRef? scope, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<NativeFunctionDescriptor>> ImplementationsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ResourceSnapshot<TransformationFunctionResource>> GetAsync(FunctionReference reference, CancellationToken ct) => throw new NotSupportedException();
        public Task<ResourceSnapshot<FunctionKindResource>> GetKindAsync(FunctionReference reference, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(TransformationFunctionResource resource, string? etag, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveKindAsync(FunctionKindResource resource, string? etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
