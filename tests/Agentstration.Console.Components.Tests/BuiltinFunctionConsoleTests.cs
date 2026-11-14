using System.Text.Json;
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
    [DataRow("en-US", "Built-in", "read-only")]
    [DataRow("fr-FR", "Intégré", "lecture seule")]
    public async Task BuiltinFunctionsAreIdentifiedAndReadOnlyEvenForAnInstanceAdministrator(string locale, string badge, string help)
    {
        using var culture = new TestCultureScope(locale);
        using var context = CreateContext(new KnowledgeClientStub());
        var owner = new ConsoleContextState(new BuiltinFunctionContextProvider());
        await owner.EnsureLoadedAsync(default);
        context.Services.AddSingleton(owner);
        context.Services.AddSingleton<IConsoleNavigationContext>(new KnowledgeNavigationContext("/instance"));
        var client = new BuiltinFunctionClient();
        context.Services.AddSingleton<ITransformationFunctionsClient>(client);
        var catalogue = context.Render<TransformationFunctions>();
        Assert.Contains(badge, catalogue.Find("[data-testid=transformation-function-row]").TextContent);
        Assert.IsEmpty(catalogue.FindAll("[data-testid=function-native-kind-create]"));
        Assert.IsFalse(catalogue.FindAll("button").Any(button => button.TextContent == (locale == "fr-FR" ? "Désactiver" : "Disable")));
        var editor = context.Render<TransformationFunctionEditor>(parameters => parameters.Add(value => value.Name, "json-select-builtin"));
        Assert.Contains(help, editor.Find("[role=note]").TextContent);
        Assert.IsTrue(editor.Find("fieldset").HasAttribute("disabled"));
        await editor.Find("form").SubmitAsync();
        Assert.AreEqual(0, client.SaveCount);
    }

    private sealed class BuiltinFunctionContextProvider : IConsoleContextProvider
    {
        public async Task<ConsoleContextSnapshot> GetAsync(CancellationToken ct) =>
            (await new TransformationContextProvider().GetAsync(ct)) with { Permissions = new HashSet<string> { "resources/read", "resources/write" } };
    }

    private sealed class BuiltinFunctionClient : ITransformationFunctionsClient
    {
        public int SaveCount { get; private set; }
        private static FunctionKindResource Kind => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = FunctionResourceKinds.FunctionKind, ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "native" }, Governance = ResourceGovernance.Core("functions.native"),
            Definition = new("Native", [new("1.0.0", "native", NativeFunctionKindContract.DefinitionSchema)])
        };
        private static TransformationFunctionResource Function => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = FunctionResourceKinds.TransformationFunction, ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "json-select-builtin" }, Governance = ResourceGovernance.Core("functions.native"),
            Definition = new("json.select", [new("1.0.0", new("native", "1.0.0", ResourceScopeRef.Instance),
                JsonSerializer.Deserialize<JsonElement>("""{"implementation":{"id":"json.select","version":"1.0.0"}}"""))])
        };
        public Task<IReadOnlyList<TransformationFunctionResource>> ListAsync(ResourceScopeRef? scope, CancellationToken ct) => Task.FromResult<IReadOnlyList<TransformationFunctionResource>>([Function]);
        public Task<IReadOnlyList<FunctionKindResource>> ListKindsAsync(ResourceScopeRef? scope, CancellationToken ct) => Task.FromResult<IReadOnlyList<FunctionKindResource>>([Kind]);
        public Task<IReadOnlyList<NativeFunctionDescriptor>> ImplementationsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<NativeFunctionDescriptor>>([
            new("json.select", "1.0.0", JsonSerializer.Deserialize<JsonElement>("{}"), JsonSerializer.Deserialize<JsonElement>("{}"))]);
        public Task<ResourceSnapshot<TransformationFunctionResource>> GetAsync(FunctionReference reference, CancellationToken ct) => Task.FromResult(new ResourceSnapshot<TransformationFunctionResource>(Function, "etag"));
        public Task<ResourceSnapshot<FunctionKindResource>> GetKindAsync(FunctionReference reference, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FunctionChoice>> ChoicesAsync(ResourceScopeRef scope, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(TransformationFunctionResource resource, string? etag, CancellationToken ct) { SaveCount++; return Task.CompletedTask; }
        public Task SaveKindAsync(FunctionKindResource resource, string? etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
