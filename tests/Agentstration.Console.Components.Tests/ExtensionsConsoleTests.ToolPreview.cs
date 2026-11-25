using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;
using Agentstration.Tools;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
namespace Agentstration.Web.Tests;

public sealed partial class ExtensionsConsoleTests
{
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task MaterializedToolPreviewUsesOneExistingExactRead(string locale)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new BundleExtensionsClient());
        var tools = new BundleToolsClient();
        context.Services.AddSingleton<IToolsClient>(tools);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        ConfigureExtensionNavigation(context);
        await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(default);
        var cut = context.Render<Agentstration.Web.Components.Pages.ExtensionDetails>(p => p.Add(c => c.RegistrationName, "crawl4ai"));
        await SelectExtensionSectionAsync(cut, "contributions");
        Assert.AreEqual(0, tools.ExactToolReads);
        Assert.IsEmpty(cut.FindComponents<ToolResourcePreview>());
        await cut.Find("[data-contribution-id='web.crawl'] [data-testid=extension-tool-preview]").ClickAsync(new());
        Assert.AreEqual(1, tools.ExactToolReads);
        Assert.AreEqual(ResourceScopeRef.Instance, tools.RequestedTool?.ScopeRef);
        Assert.AreEqual(ResourceNamespace.Default, tools.RequestedTool?.Namespace);
        Assert.AreEqual("paired-crawl4ai.web.crawl", tools.RequestedTool?.Name);
        Assert.HasCount(1, cut.FindComponents<ToolResourceSummary>());
        Assert.IsEmpty(cut.FindAll("[data-testid=tool-preview-edit]"));
    }
}
