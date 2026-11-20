using System.Net;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;

namespace Agentstration.Web.Tests;

public sealed partial class ToolDefinitionConsoleTests
{
    [TestMethod]
    public async Task DefinitionFoundationUsesSavedIdentityAndTheSameStatePresentationsAndRealModelFields()
    {
        var saved = SavedDefinition(); saved = saved with { Metadata = saved.Metadata with { Tags = new Dictionary<string, string> { ["team"] = "support" } } };
        using var context = SavedContext(new DefinitionClient { Resource = saved });
        var page = context.Render<ToolDefinitionEditor>(p => p.Add(c => c.Name, saved.Name));
        var frame = page.FindComponent<ConsoleResourceDetailsTemplate>();
        Assert.AreEqual(saved, frame.Instance.Resource);
        Assert.Contains("team", page.Find(".resource-identity-summary").TextContent);
        await Select(page, "definition/identity");
        var form = page.FindComponent<ResourceDefinitionForm>();
        Assert.AreSame(frame.Instance.SectionState, form.Instance.SectionState);
        Assert.AreSame(frame.Instance.SectionPresentations, form.Instance.Sections);
        Assert.IsTrue(form.Instance.Sections.Values.SelectMany(p => p.Fields).All(field => form.Instance.EditContext.Model.GetType().GetProperty(field) is not null));
        Assert.IsTrue(page.FindComponent<ResourceIdentityEditor>().Instance.LabelsReadOnly, "The ToolDefinition write contract is properties-only.");
        Assert.AreEqual("yaml", page.FindComponent<ResourceNavigation>().Instance.Items.Last().Path);
        await Select(page, "yaml");
        Assert.IsTrue(page.FindComponent<ResourceYamlEditor>().Instance.ReadOnly);
        Assert.Contains("kind: ToolDefinition", page.FindComponent<ResourceYamlEditor>().Instance.Value);
        Assert.Contains(saved.Name, page.FindComponent<ResourceYamlEditor>().Instance.Value);
    }

    [TestMethod]
    public async Task DefinitionRefreshBuffersNewerIdentityAndRetainsInvalidInputAndEditContextUntilDiscard()
    {
        var client = new DefinitionClient { Resource = SavedDefinition() }; using var context = SavedContext(client);
        var page = context.Render<ToolDefinitionEditor>(p => p.Add(c => c.Name, client.Resource.Name));
        await Select(page, "definition/configuration");
        var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        page.FindComponents<FormField>().Single(field => field.Instance.Label == "Timeout (seconds)").Find("input").Change("incomplete");
        client.Resource = client.Resource with { Generation = 3, Definition = client.Resource.Definition with { DisplayName = "Newer server identity" } };
        await page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("incomplete", page.FindComponents<FormField>().Single(field => field.Instance.Label == "Timeout (seconds)").Find("input").GetAttribute("value"));
        Assert.Contains("Saved Tool", page.Find(".resource-header").TextContent);
        Assert.Contains("newer", page.Find(".form-alert-warning").TextContent.ToLowerInvariant());
        await page.Find(".unsaved-changes-bar .button-secondary").ClickAsync();
        Assert.Contains("Newer server identity", page.Find(".resource-header").TextContent);
        Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DefinitionLostReadOrReplacedUidPurgesPrivateFormAndHistory(bool forbidden)
    {
        var client = new DefinitionClient { Resource = SavedDefinition() }; using var context = SavedContext(client);
        var page = context.Render<ToolDefinitionEditor>(p => p.Add(c => c.Name, client.Resource.Name));
        await Select(page, "definition/identity");
        page.FindComponent<ResourceIdentityFields>().Find("input:not([disabled])").Input("Private draft");
        if (forbidden) client.ReadError = new AgentstrationApiException("Access denied", "fixture-denied", HttpStatusCode.Forbidden);
        else client.Resource = client.Resource with { Uid = Guid.NewGuid() };
        await page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.IsEmpty(page.FindComponents<ResourceDefinitionForm>());
        Assert.DoesNotContain("Private draft", page.Markup);
        Assert.DoesNotContain("Saved Tool", page.Markup);
        Assert.AreEqual(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.NotFound, page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
    }

    [TestMethod]
    public async Task DefinitionRejectedWriteKeepsItsExactDraftAndOriginalEtag()
    {
        var client = new DefinitionClient { Resource = SavedDefinition(), WriteError = new InvalidOperationException("Rejected exact draft") }; using var context = SavedContext(client);
        var page = context.Render<ToolDefinitionEditor>(p => p.Add(c => c.Name, client.Resource.Name));
        await Select(page, "definition/identity");
        page.FindComponent<ResourceIdentityFields>().Find("input:not([disabled])").Input("Retained draft");
        var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        await page.Find("#tool-definition-resource-form").SubmitAsync();
        Assert.AreEqual("definition-etag", client.LastEtag);
        Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("Retained draft", page.FindComponent<ResourceIdentityFields>().Find("input:not([disabled])").GetAttribute("value"));
        Assert.Contains("Rejected exact draft", page.Find("[role=alert]").TextContent);
        Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }
}
