using Agentstration.Web.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ResourcePreviewPanelTests
{
    [TestMethod]
    [DataRow("en-US", "Preview", "Close preview")]
    [DataRow("fr-FR", "Aperçu", "Fermer l’aperçu")]
    public async Task PreviewHasOnlyItsStructureAndCloseControlWithoutAResourceClient(string locale, string title, string close)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var closed = false;
        var cut = context.Render<ResourcePreviewPanel>(p => p.Add(c => c.IsOpen, true).Add(c => c.OnClose, () => closed = true));
        Assert.AreEqual(title, cut.Find("dialog h2").TextContent);
        Assert.AreEqual(string.Empty, cut.Find("[data-testid=resource-preview-content]").InnerHtml);
        await cut.Find($"button[aria-label='{close}']").ClickAsync(new());
        Assert.IsTrue(closed);
        cut.Render(p => p.Add(c => c.IsOpen, false));
        Assert.IsEmpty(cut.FindAll("dialog"));
    }
}
