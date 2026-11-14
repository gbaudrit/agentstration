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
        rendered.Find("[data-schema-path='$.path'] input, [data-schema-path='$.path'] textarea").Input("${keep.literal}");
        Assert.AreEqual("${keep.literal}", calls.Single().Parameters.GetProperty("path").GetString());
        rendered.Render(parameters => parameters.Add(value => value.Value, calls).Add(value => value.ReadOnly, true));
        Assert.IsTrue(rendered.Find("[data-testid=function-sequence-add]").HasAttribute("disabled"));
        Assert.IsTrue(rendered.Find("[data-schema-path='$.path'] input, [data-schema-path='$.path'] textarea").HasAttribute("disabled"));
        Assert.HasCount(1, rendered.FindAll("[data-testid=flow-sequence-add]"));
    }

    private sealed class FunctionClientStub : ITransformationFunctionsClient
    {
        public Task<IReadOnlyList<FunctionChoice>> ChoicesAsync(ResourceScopeRef scope, CancellationToken ct)
        {
            var descriptor = new NativeFunctionDescriptor("json.select", "1.0.0",
                JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"value":{},"path":{"type":"string","maxLength":1024}},"required":["value","path"],"additionalProperties":false}"""),
                JsonSerializer.Deserialize<JsonElement>("{}"));
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
