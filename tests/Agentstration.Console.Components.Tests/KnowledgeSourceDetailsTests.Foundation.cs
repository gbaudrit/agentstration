using System.Net;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;

namespace Agentstration.Web.Tests;
public sealed partial class KnowledgeSourceEditorTests
{
    [TestMethod]
    public async Task DetailsRefreshRetainsBothDraftsAndDefinitionSaveKeepsYamlOriginalEtag()
    {
        var source = ProjectedSource(); var client = new KnowledgeClientStub(source);
        using var context = CreateContext(client);
        var page = context.Render<KnowledgeSourceDetails>(p => p.Add(x => x.Name, source.Name));
        DataSourceScreenTestSupport.Select(page, "yaml");
        var pending = (page.Find("[data-testid=knowledge-source-yaml-source]").GetAttribute("value") ?? page.Find("[data-testid=knowledge-source-yaml-source]").TextContent).Replace("Agentstration documentation", "Independent YAML", StringComparison.Ordinal);
        page.Find("[data-testid=knowledge-source-yaml-source]").Input(pending);
        DataSourceScreenTestSupport.Select(page, "definition/identity");
        page.Find("[data-testid=knowledge-source-display-name]").Input("Local definition");
        DataSourceScreenTestSupport.Select(page, "definition/configuration");
        page.Find("[data-testid=lifecycle-enable]").Change(true); page.Find("[data-testid=lifecycle-ttl]").Change("invalid duration");
        var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        client.ReadOverride = new(source with { Definition = source.Definition with { DisplayName = "Updated server source" } }, "\"server\"");
        await page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("invalid duration", page.Find("[data-testid=lifecycle-ttl]").GetAttribute("value"));
        Assert.AreEqual(pending, page.Find("[data-testid=knowledge-source-yaml-source]").GetAttribute("value") ?? page.Find("[data-testid=knowledge-source-yaml-source]").TextContent);
        Assert.Contains("Updated server source", page.Find(".resource-identity-summary").TextContent);
        page.Find("[data-testid=lifecycle-ttl]").Change("01:00:00");
        await page.Find("form").SubmitAsync();
        page.WaitForAssertion(() => Assert.AreEqual("Local definition", client.UpdatedRequest?.Properties.DisplayName));
        Assert.AreEqual("\"etag-1\"", client.UpdatedEtag);
        Assert.IsFalse(page.FindComponent<KnowledgeSourceEditor>().Instance.HasDraft, "Definition editor must become clean after its save.");
        DataSourceScreenTestSupport.Select(page, "yaml");
        Assert.IsFalse(page.Find("[data-testid=knowledge-source-yaml-save]").HasAttribute("disabled"), "An independent YAML draft must be saveable.");
        Assert.AreEqual(pending, page.Find("[data-testid=knowledge-source-yaml-source]").GetAttribute("value") ?? page.Find("[data-testid=knowledge-source-yaml-source]").TextContent);
        page.Find("[data-testid=knowledge-source-yaml-save]").Click();
        Assert.IsEmpty(page.FindAll(".resource-yaml-editor [role=alert]"), string.Join(" | ", page.FindAll(".resource-yaml-editor [role=alert]").Select(x => x.TextContent)));
        page.WaitForAssertion(() => Assert.AreEqual("Independent YAML", client.UpdatedRequest?.Properties.DisplayName));
        Assert.AreEqual("\"etag-1\"", client.UpdatedEtag);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    public async Task DetailsRefreshClearsPrivateDraftsOnlyWhenAccessIsLost(HttpStatusCode status, bool retains)
    {
        var source = ProjectedSource(); var client = new KnowledgeClientStub(source);
        using var context = CreateContext(client);
        var page = context.Render<KnowledgeSourceDetails>(p => p.Add(x => x.Name, source.Name));
        DataSourceScreenTestSupport.Select(page, "definition/identity");
        page.Find("[data-testid=knowledge-source-display-name]").Input("Private Knowledge draft");
        client.ReadFailure = new AgentstrationApiException("fixture", "fixture", status);
        await page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreEqual(retains, page.Markup.Contains("Private Knowledge draft", StringComparison.Ordinal));
        Assert.AreEqual(retains, page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.IsNull(client.UpdatedRequest);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeniedDefinitionOrYamlSaveClearsTheOwningResource(bool yaml)
    {
        var source = ProjectedSource(); var client = new KnowledgeClientStub(source);
        using var context = CreateContext(client);
        var page = context.Render<KnowledgeSourceDetails>(p => p.Add(x => x.Name, source.Name));
        client.WriteFailure = new AgentstrationApiException("private-response", "fixture", HttpStatusCode.Forbidden);
        if (yaml)
        {
            DataSourceScreenTestSupport.Select(page, "yaml");
            var field = page.Find("[data-testid=knowledge-source-yaml-source]");
            field.Input((field.GetAttribute("value") ?? field.TextContent).Replace("Agentstration documentation", "Private denied YAML", StringComparison.Ordinal));
            page.Find("[data-testid=knowledge-source-yaml-save]").Click();
        }
        else
        {
            DataSourceScreenTestSupport.Select(page, "definition/identity");
            page.Find("[data-testid=knowledge-source-display-name]").Input("Private denied definition");
            await page.Find("form").SubmitAsync();
        }
        page.WaitForAssertion(() => Assert.IsNull(page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource));
        Assert.DoesNotContain("Private denied", page.Markup);
        Assert.DoesNotContain("private-response", page.Markup);
        Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task ObsoleteRefreshCannotRestoreThePreviousResourceAfterAnAddressChange()
    {
        var source = ProjectedSource(); var client = new KnowledgeClientStub(source);
        using var context = CreateContext(client);
        var page = context.Render<KnowledgeSourceDetails>(p => p.Add(x => x.Name, source.Name));
        DataSourceScreenTestSupport.Select(page, "definition/identity");
        page.Find("[data-testid=knowledge-source-display-name]").Input("Private old context draft");
        var pending = new TaskCompletionSource<ResourceSnapshot<KnowledgeSourceResource>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = source with { Uid = Guid.NewGuid(), Metadata = source.Metadata with { Name = "next-source" }, Definition = source.Definition with { DisplayName = "Next resource" } };
        client.ReadSource = name => name == source.Name ? pending.Task : Task.FromResult(new ResourceSnapshot<KnowledgeSourceResource>(next, "next-etag"));
        var refresh = page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        page.Render(p => p.Add(x => x.Name, next.Name));
        pending.SetResult(new(source, "obsolete-etag")); await refresh;
        Assert.AreEqual(next.Uid, page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource!.Uid);
        Assert.Contains("Next resource", page.Find(".resource-identity-summary").TextContent);
        Assert.DoesNotContain("Private old context draft", page.Markup);
    }

    [TestMethod]
    public void ManagedKnowledgeKeepsIndependentlyAuthorizedProjection()
    {
        var source = ProjectedSource() with { Governance = ResourceGovernance.Core("fixture") };
        using var context = CreateContext(new KnowledgeClientStub(source));
        var page = context.Render<KnowledgeSourceDetails>(p => p.Add(x => x.Name, source.Name));
        DataSourceScreenTestSupport.Select(page, "projections");
        Assert.IsFalse(page.Find("[data-testid=knowledge-start-projection]").HasAttribute("disabled"));
        Assert.IsTrue(page.Find("[data-testid=knowledge-source-enabled]").HasAttribute("disabled"));
        DataSourceScreenTestSupport.Select(page, "definition/identity");
        Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
    }
}
