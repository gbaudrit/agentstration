using Agentstration.Knowledge.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;

namespace Agentstration.Web.Tests;

public sealed partial class KnowledgeSourceEditorTests
{
    [TestMethod]
    public async Task RefreshedEmbeddedSnapshotRetainsInvalidLifecycleAndOriginalEtag()
    {
        var source = ProjectedSource();
        var client = new KnowledgeClientStub(source);
        using var context = CreateContext(client);
        var original = new ResourceSnapshot<KnowledgeSourceResource>(source, "\"original\"");
        var page = context.Render<KnowledgeSourceEditor>(p => p.Add(x => x.Name, source.Name).Add(x => x.Embedded, true).Add(x => x.Snapshot, original));
        var editContext = page.FindComponent<EditForm>().Instance.EditContext;
        page.Find("[data-testid=lifecycle-enable]").Change(true);
        page.Find("[data-testid=lifecycle-ttl]").Change("invalid duration");
        page.Find("[data-testid=knowledge-source-display-name]").Change("Local draft");
        var latest = new ResourceSnapshot<KnowledgeSourceResource>(source with { Definition = source.Definition with { DisplayName = "New server value" } }, "\"latest\"");
        page.Render(p => p.Add(x => x.Name, source.Name).Add(x => x.Embedded, true).Add(x => x.Snapshot, latest));
        Assert.AreSame(editContext, page.FindComponent<EditForm>().Instance.EditContext);
        Assert.AreEqual("invalid duration", page.Find("[data-testid=lifecycle-ttl]").GetAttribute("value"));
        Assert.AreEqual("Local draft", page.Find("[data-testid=knowledge-source-display-name]").GetAttribute("value"));
        Assert.IsTrue(page.Instance.HasDraft);
        await page.Find("form").SubmitAsync();
        Assert.IsNull(client.UpdatedRequest);
        page.Find("[data-testid=lifecycle-ttl]").Change("01:00:00");
        await page.Find("form").SubmitAsync();
        page.WaitForAssertion(() => Assert.AreEqual("\"original\"", client.UpdatedEtag));
        Assert.AreEqual("Local draft", client.UpdatedRequest!.Properties.DisplayName);
    }

    [TestMethod]
    public async Task ExplicitDiscardAcceptsBufferedSnapshotAndReplacesFormContext()
    {
        var source = ProjectedSource();
        using var context = CreateContext(new KnowledgeClientStub(source));
        var original = new ResourceSnapshot<KnowledgeSourceResource>(source, "\"original\"");
        var page = context.Render<KnowledgeSourceEditor>(p => p.Add(x => x.Name, source.Name).Add(x => x.Embedded, true).Add(x => x.Snapshot, original));
        var editContext = page.FindComponent<EditForm>().Instance.EditContext;
        page.Find("[data-testid=knowledge-source-display-name]").Change("Local draft");
        var latest = new ResourceSnapshot<KnowledgeSourceResource>(source with { Definition = source.Definition with { DisplayName = "Accepted server value" } }, "\"latest\"");
        page.Render(p => p.Add(x => x.Name, source.Name).Add(x => x.Embedded, true).Add(x => x.Snapshot, latest));
        await page.InvokeAsync(() => page.Instance.DiscardAsync());
        Assert.AreNotSame(editContext, page.FindComponent<EditForm>().Instance.EditContext);
        Assert.AreEqual("Accepted server value", page.Find("[data-testid=knowledge-source-display-name]").GetAttribute("value"));
        Assert.IsFalse(page.Instance.HasDraft);
    }
}
