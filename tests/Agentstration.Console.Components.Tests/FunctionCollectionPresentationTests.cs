using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.Schemas;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class KnowledgeSourceEditorTests
{
    [TestMethod]
    public async Task FunctionCatalogRetainsImmutableContractVersionsAndProtectedKindDetails()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(new KnowledgeClientStub()).WithCollectionPresentation();
        var owner = new ConsoleContextState(new BuiltinFunctionContextProvider());
        await owner.EnsureLoadedAsync(default);
        context.Services.AddSingleton(owner);
        context.Services.AddSingleton<IConsoleNavigationContext>(new KnowledgeNavigationContext("/instance"));
        var client = new BuiltinFunctionClient();
        context.Services.AddSingleton<ITransformationFunctionsClient>(client);

        var rendered = context.Render<TransformationFunctions>();
        var row = rendered.Find("[data-testid=transformation-function-row]");
        Assert.AreEqual("default/json-select-builtin", row.QuerySelector(".collection-qualified-name code")!.GetAttribute("title"));
        StringAssert.Contains(row.TextContent, "1.0.0");
        Assert.IsNotNull(row.QuerySelector(".resource-icon"));
        Assert.IsNotNull(row.QuerySelector(".resource-owner-icon"));
        Assert.IsNotNull(row.QuerySelector(".status-icon-only"));
        Assert.IsNotNull(row.QuerySelector(".managed-icon-only"));
        var destination = row.QuerySelector("a.resource-name-link")!.GetAttribute("href")!;
        StringAssert.Contains(destination, "scopeRef=%2Finstance");
        StringAssert.Contains(destination, "namespace=default");

        Assert.IsEmpty(rendered.FindAll(".collection-expanded"));
        rendered.Find(".collection-actions button[aria-expanded=false]").Click();
        Assert.HasCount(1, rendered.FindAll(".collection-expanded"));
        Assert.HasCount(1, rendered.FindComponents<SchemaDocumentEditor>());
        Assert.IsTrue(rendered.FindComponent<SchemaDocumentEditor>().Instance.ReadOnly);
        Assert.Contains("1.0.0", rendered.Find(".collection-expanded").TextContent);
        rendered.Find(".collection-actions button[aria-expanded=true]").Click();
        Assert.IsEmpty(rendered.FindAll(".collection-expanded"));
        Assert.AreEqual(0, client.SaveCount);
    }
}
