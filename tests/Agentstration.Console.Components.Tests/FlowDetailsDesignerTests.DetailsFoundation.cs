using System.Net;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class FlowDetailsDesignerTests
{
    [TestMethod]
    [DataRow("en-US", "This Flow delegates one complete Run")]
    [DataRow("fr-FR", "Ce Flow délègue un Run complet")]
    public async Task RuntimeFlowRemainsInspectableWithoutAWorkflowDesigner(string culture, string help)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new(culture);
            using var context = FlowContext();
            context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
            var client = new FlowClientStub();
            var saved = await client.GetFlowAsync("sample", default);
            client.SnapshotOverride = new(saved with { Definition = new Agentstration.Flows.RuntimeFlowDefinition("coding-cli", "1.0", "1.0", System.Text.Json.JsonSerializer.SerializeToElement(new { })), Graph = null }, "\"runtime\"");
            context.Services.AddSingleton<IFlowApiClient>(client); AddConsoleContext(context);
            var page = context.Render<FlowDetails>(p => p.Add(value => value.FlowId, "sample"));
            page.WaitForElement("[data-testid=flow-identity-summary]");
            Assert.Contains(help, page.Markup); Assert.DoesNotContain("Kind.Runtime", page.Markup);
            Assert.HasCount(0, page.FindAll(".flow-header-designer"));
            SelectFlowSection(context, "definition/configuration");
            page.WaitForElement("[data-testid=flow-definition-form]");
            Assert.AreEqual(0, client.DraftLoadCount);
            Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
            Assert.HasCount(0, page.FindAll("a[href*='/designer']"));
            SelectFlowSection(context, "yaml");
            Assert.Contains("coding-cli", page.Markup);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    private static (BunitContext Context, FlowClientStub Client, IRenderedComponent<FlowDetails> Page) DetailsFoundation()
    {
        var context = FlowContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FlowClientStub(); context.Services.AddSingleton<IFlowApiClient>(client); AddConsoleContext(context);
        var page = context.Render<FlowDetails>(p => p.Add(value => value.FlowId, "sample"));
        page.WaitForElement("[data-testid=flow-identity-summary]");
        SelectFlowSection(context, "definition/identity");
        page.WaitForElement("[data-testid=flow-definition-form]");
        return (context, client, page);
    }

    [TestMethod]
    public void FlowDefinitionUsesTheSameSectionStateAndDeclarationsAsTheSavedResourceFrame()
    {
        var (context, _, page) = DetailsFoundation(); using (context)
        {
            var frame = page.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
            var form = page.FindComponent<ResourceDefinitionForm>().Instance;
            Assert.AreSame(frame.SectionState, form.SectionState);
            Assert.AreSame(frame.SectionPresentations, form.Sections);
            CollectionAssert.AreEqual(new[] { "Name", "Namespace", "DisplayName", "Description", "Tags" }, form.Sections["definition/identity"].Fields.ToArray());
            Assert.HasCount(1, page.FindAll("[data-testid=flow-identity-editor] [data-testid=labels-editor]"));
            Assert.HasCount(1, page.FindAll("[data-testid=flow-identity-summary] .labels-summary"));
            Assert.AreEqual("yaml", page.FindAll(".resource-navigation-rail a[data-resource-section]").Last().GetAttribute("data-resource-section"));
        }
    }

    [TestMethod]
    public async Task FlowInvalidHiddenIdentityReturnsToItsActualOwningSectionWithoutSaving()
    {
        var (context, client, page) = DetailsFoundation(); using (context)
        {
            page.Find("[data-testid=flow-definition-display-name]").Input(string.Empty);
            SelectFlowSection(context, "overview");
            await page.Find("[data-testid=flow-definition-form]").SubmitAsync();
            page.WaitForAssertion(() => Assert.AreEqual("definition/identity", page.FindComponent<ResourceDefinitionForm>().Instance.SectionState!.CurrentPath));
            Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.EditContext.GetValidationMessages().Any());
            Assert.IsNull(client.LastDraftUpdate);
        }
    }

    [TestMethod]
    public async Task FlowInvalidLabelRowsSurviveRefreshAndSectionChangesWithTheirOriginalContext()
    {
        var (context, client, page) = DetailsFoundation(); using (context)
        {
            var formContext = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
            page.Find("[data-testid=label-add]").Click();
            page.FindAll("[data-testid=label-key]").Last().Input("owner");
            page.FindAll("[data-testid=label-value]").Last().Input("duplicate owner");
            Assert.IsTrue(formContext.GetValidationMessages().Any());
            SelectFlowSection(context, "yaml");
            await page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
            SelectFlowSection(context, "definition/identity");
            Assert.AreSame(formContext, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
            Assert.AreEqual("duplicate owner", page.FindAll("[data-testid=label-value]").Last().GetAttribute("value"));
            Assert.IsTrue(formContext.GetValidationMessages().Any());
            Assert.HasCount(1, page.FindAll(".unsaved-changes-bar"));
            Assert.IsNull(client.LastDraftUpdate);
        }
    }

    [TestMethod]
    public async Task FlowChangedRefreshBuffersBothSavedEnvelopeAndMetadataDraftUntilExplicitDiscard()
    {
        var (context, client, page) = DetailsFoundation(); using (context)
        {
            var frame = page.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
            var formContext = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
            page.Find("[data-testid=flow-definition-display-name]").Input("Retained local metadata");
            var saved = await client.GetFlowAsync("sample", CancellationToken.None);
            var serverDraft = await client.GetDraftAsync("sample", CancellationToken.None);
            client.SnapshotOverride = new(saved with { DisplayName = "New saved publication" }, "\"resource-etag-new\"");
            client.DraftOverride = new(serverDraft.Value with { DisplayName = "New server draft" }, "\"draft-etag-new\"");
            await page.InvokeAsync(() => frame.OnRefresh.InvokeAsync());
            Assert.AreSame(formContext, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
            Assert.AreEqual("Retained local metadata", page.Find("[data-testid=flow-definition-display-name]").GetAttribute("value"));
            Assert.HasCount(1, page.FindAll("[data-testid=flow-refresh-conflict]"));
            Assert.DoesNotContain("New saved publication", page.Find(".resource-header").TextContent);
            Assert.IsFalse(page.Find(".unsaved-changes-bar button[type=button]").HasAttribute("disabled"));
            await page.Find(".unsaved-changes-bar button[type=button]").ClickAsync();
            Assert.AreEqual("New server draft", page.Find("[data-testid=flow-definition-display-name]").GetAttribute("value"));
            Assert.Contains("New saved publication", page.Find(".resource-header").TextContent);
            Assert.HasCount(0, page.FindAll("[data-testid=flow-refresh-conflict]"));
        }
    }

    [TestMethod]
    public async Task FlowRefreshEvaluatesNewInputAtResponseTimeAndRetainsTheOriginalDraftEtag()
    {
        var (context, client, page) = DetailsFoundation(); using (context)
        {
            var saved = await client.GetFlowAsync("sample", CancellationToken.None);
            var pending = new TaskCompletionSource<FlowResourceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.SnapshotLoader = () => pending.Task;
            var refreshing = page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
            page.WaitForAssertion(() => Assert.IsTrue(page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status == ResourceLoadStatus.Refreshing));
            page.Find("[data-testid=flow-definition-description]").Input("Typed while refresh was pending");
            pending.SetResult(new(saved with { DisplayName = "A new server title" }, "\"new-envelope\""));
            await refreshing;
            Assert.AreEqual("Typed while refresh was pending", page.Find("[data-testid=flow-definition-description]").GetAttribute("value"));
            await page.Find("[data-testid=flow-definition-form]").SubmitAsync();
            Assert.AreEqual("\"etag-1\"", client.LastDraftETag);
            Assert.AreEqual("Typed while refresh was pending", client.LastDraftUpdate?.Description);
        }
    }

    [TestMethod]
    public async Task FlowMetadataSavePreservesUnchangedTagValuesVerbatim()
    {
        var (context, client, page) = DetailsFoundation(); using (context)
        {
            var previous = await client.GetDraftAsync("sample", CancellationToken.None);
            var originalTags = new Dictionary<string, string> { ["owner"] = "  platform = team  " };
            client.DraftOverride = new(previous.Value with { Tags = originalTags }, "\"new-tags\"");
            await page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
            page.Find("[data-testid=flow-definition-display-name]").Input("Only display identity changed");
            await page.Find("[data-testid=flow-definition-form]").SubmitAsync();
            Assert.AreSame(originalTags, client.LastDraftUpdate!.Tags);
            Assert.AreEqual("  platform = team  ", client.LastDraftUpdate.Tags!["owner"]);
        }
    }

    [TestMethod]
    [DataRow(200, true)]
    [DataRow(201, false)]
    public async Task FlowMetadataRetainsTheExistingTrimmedDisplayNameLengthLimit(int length, bool accepted)
    {
        var (context, client, page) = DetailsFoundation(); using (context)
        {
            var name = new string('x', length);
            page.Find("[data-testid=flow-definition-display-name]").Input("  " + name + "  ");
            SelectFlowSection(context, "overview");
            await page.Find("[data-testid=flow-definition-form]").SubmitAsync();
            if (accepted) Assert.AreEqual(name, client.LastDraftUpdate!.DisplayName);
            else
            {
                Assert.IsNull(client.LastDraftUpdate);
                Assert.AreEqual("definition/identity", page.FindComponent<ResourceDefinitionForm>().Instance.SectionState!.CurrentPath);
                Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.EditContext.GetValidationMessages().Any());
            }
        }
    }

    [TestMethod]
    public async Task FlowFailedRefreshRetainsInvalidContextAndRetryDoesNotSubmitAMutation()
    {
        var (context, client, page) = DetailsFoundation(); using (context)
        {
            var formContext = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
            page.Find("[data-testid=flow-definition-display-name]").Input(string.Empty);
            client.SnapshotFailure = new HttpRequestException("Offline fixture");
            await page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
            Assert.AreEqual(ResourceLoadStatus.Failed, page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
            Assert.AreSame(formContext, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
            client.SnapshotFailure = null;
            await page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRetry.InvokeAsync());
            Assert.AreSame(formContext, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
            Assert.AreEqual(string.Empty, page.Find("[data-testid=flow-definition-display-name]").GetAttribute("value"));
            Assert.IsNull(client.LastDraftUpdate);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FlowReadLossOrReplacementPurgesPrivateDefinitionDrafts(bool replaced)
    {
        var (context, client, page) = DetailsFoundation(); using (context)
        {
            page.Find("[data-testid=flow-definition-display-name]").Input("Private pending title");
            if (replaced)
            {
                var saved = await client.GetFlowAsync("sample", CancellationToken.None);
                client.SnapshotOverride = new(saved with { Uid = Guid.NewGuid() }, "\"replacement\"");
            }
            else client.SnapshotFailure = new AgentstrationApiException("Read refused", "fixture-read-refused", HttpStatusCode.Forbidden);
            await page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
            Assert.AreEqual(replaced ? ResourceLoadStatus.NotFound : ResourceLoadStatus.Forbidden, page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
            Assert.HasCount(0, page.FindAll("[data-testid=flow-definition-form]"));
            Assert.HasCount(0, page.FindAll("[data-testid=flow-identity-summary]"));
            Assert.DoesNotContain("Private pending title", page.Markup);
        }
    }
}
