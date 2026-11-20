using Agentstration.Packs.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class PackProjectSourceEditorTests
{
    private const string Source = """
        {"apiVersion":"agentstration.io/v1","kind":"Agent","metadata":{"name":"welcome"},"definition":{"displayName":"Saved welcome","handler":"prompt-agent","instructions":"Help.","modelProfile":{"binding":"model"},"runtimeProfile":{"binding":"runtime"},"tools":[],"settings":{}}}
        """;
    private static readonly PackProjectSourceDocument Document = new("agents/welcome.yaml", "Agent", "welcome", Source);
    [TestMethod]
    public async Task InvalidRawAndFormDraftsSurviveModeChangesAndServerRefreshUntilExplicitDiscard()
    {
        using var context = Context(); var dirty = false; var page = context.Render<PackProjectSourceEditor>(p => p.Add(value => value.Document, Document).Add(value => value.InitialForm, true).Add(value => value.DraftChanged, value => dirty = value));
        var agent = page.FindComponent<PackAgentDefinitionForm>(); agent.FindAll("textarea")[^1].Input("{ invalid form settings"); Assert.IsTrue(dirty);
        await page.InvokeAsync(() => page.Instance.ShowModeAsync(false)); page.Find("[data-testid=pack-project-source-yaml]").Input("{ invalid raw source");
        await page.InvokeAsync(() => page.Instance.ShowModeAsync(true)); Assert.AreEqual("{ invalid form settings", agent.FindAll("textarea")[^1].GetAttribute("value"));
        page.Render(p => p.Add(value => value.Document, Document with { Source = Source.Replace("Saved welcome", "Server welcome", StringComparison.Ordinal) }));
        Assert.IsTrue(page.Instance.IsDirty); await page.InvokeAsync(() => page.Instance.ShowModeAsync(false)); Assert.AreEqual("{ invalid raw source", page.Find("[data-testid=pack-project-source-yaml]").GetAttribute("value"));
        await page.InvokeAsync(page.Instance.DiscardAsync); Assert.IsFalse(page.Instance.IsDirty); Assert.IsFalse(dirty); StringAssert.Contains(page.Find("[data-testid=pack-project-source-yaml]").GetAttribute("value")!, "Server welcome");
    }
    [TestMethod]
    public async Task ReplacingAnotherViewDraftRequiresConfirmationAndRejectedSaveRetainsBothDrafts()
    {
        using var context = Context(); UpdatePackProjectSourceCommand? command = null; var page = context.Render<PackProjectSourceEditor>(p => p.Add(value => value.Document, Document).Add(value => value.InitialForm, true).Add(value => value.SaveRequested, value => command = value));
        var agent = page.FindComponent<PackAgentDefinitionForm>(); agent.FindAll("input")[0].Change("Form draft");
        await page.InvokeAsync(() => page.Instance.ShowModeAsync(false)); page.Find("[data-testid=pack-project-source-yaml]").Input("Raw draft"); page.Find("[data-testid=pack-project-source-save]").Click();
        Assert.IsNull(command); Assert.IsNotNull(page.Find("[data-testid=pack-project-source-confirmation]")); page.Find("[data-testid=pack-project-source-confirmation] .button-secondary").Click(); Assert.IsNull(command);
        page.Find("[data-testid=pack-project-source-save]").Click(); page.Find("[data-testid=pack-project-source-confirmation-confirm]").Click(); Assert.AreEqual("Raw draft", command!.Source);
        Assert.IsTrue(page.Instance.IsDirty); Assert.AreEqual("Raw draft", page.Find("[data-testid=pack-project-source-yaml]").GetAttribute("value")); Assert.AreEqual("Form draft", agent.FindAll("input")[0].GetAttribute("value"));
        await page.InvokeAsync(() => page.Instance.AcknowledgeAsync(Document with { Source = Source.Replace("Saved welcome", "Acknowledged", StringComparison.Ordinal) })); Assert.IsFalse(page.Instance.IsDirty);
    }
    [TestMethod]
    public void ReadOnlySourceCanBeInspectedButCannotMutateOrApplyDrafts()
    {
        using var context = Context(); var page = context.Render<PackProjectSourceEditor>(p => p.Add(value => value.Document, Document).Add(value => value.InitialForm, true).Add(value => value.ReadOnly, true));
        Assert.IsEmpty(page.FindAll("[data-testid=pack-project-source-save]")); Assert.IsEmpty(page.FindAll("input:not([disabled]),textarea:not([readonly]):not([disabled])")); Assert.IsFalse(page.Instance.IsDirty);
    }
    private static BunitContext Context() { var context = new BunitContext().WithCollectionPresentation(); context.JSInterop.Mode = JSRuntimeMode.Loose; context.Services.AddLocalization(options => options.ResourcesPath = "Resources"); return context; }
}
