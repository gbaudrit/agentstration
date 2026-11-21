using System.Net;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class DataSourceProfileScreenTests
{
    [TestMethod]
    public async Task CommonFormSharesSectionStateAndIncludesSavedReadOnlyLabelsInsideIdentity()
    {
        var client = new Client(); client.Saved = client.Saved with { Metadata = client.Saved.Metadata with { Tags = new Dictionary<string, string> { ["business"] = "fixture" } } };
        using var context = Context(client); var cut = context.Render<DataSourceProfileEditor>(p => p.Add(c => c.Name, "profile"));
        Assert.Contains("business", cut.Find(".resource-identity-summary .labels-summary").TextContent);
        Assert.IsEmpty(cut.FindAll("form")); await Select(cut, "definition/identity");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance; var template = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.AreSame(template.SectionState, form.SectionState); Assert.AreSame(template.SectionPresentations, form.Sections);
        Assert.Contains(nameof(DataSourceProfileEditorModel.Name), form.Sections["definition/identity"].Fields);
        Assert.IsNotNull(cut.Find("[data-testid=profile-identity-editor] [data-testid=labels-editor]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=label-add]"));
        await Select(cut, "definition/configuration"); Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.IsNotNull(cut.Find("[data-testid=profile-flow]")); Assert.IsNotNull(cut.Find("[data-testid=profile-version]"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RefreshKeepsInvalidDefinitionOrYamlAndPolicyUntilExplicitDiscard(bool yaml)
    {
        var client = new Client(); using var context = Context(client); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var cut = context.Render<DataSourceProfileEditor>(p => p.Add(c => c.Name, "profile"));
        await Select(cut, yaml ? "yaml" : "definition/contracts");
        var field = yaml ? "[data-testid=profile-yaml]" : "[data-testid=profile-toolBindingsJson]";
        var invalid = yaml ? "invalid: [ private fixture" : "{ private fixture"; cut.Find(field).Input(invalid);
        if (yaml) cut.Find(".unsaved-changes-bar .button-primary").Click(); else await cut.Find("form").SubmitAsync();
        Assert.AreEqual(0, client.Updates);
        var form = yaml ? null : cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        await Select(cut, "access"); var policy = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        await cut.InvokeAsync(() => policy.Parameters.Get(c => c.DraftChanged).InvokeAsync(true));
        client.Saved = client.Saved with { Generation = 2, Definition = client.Saved.Definition with { DisplayName = "New saved profile" } }; client.ReadEtag = "newer-etag";
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreSame(policy, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        Assert.AreEqual(invalid, cut.Find(field).GetAttribute("value"));
        Assert.Contains("Saved profile", cut.Find(".resource-identity-summary").TextContent);
        if (form is not null)
        {
            Assert.AreSame(form, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
            Assert.AreEqual("initial-etag", cut.Find("form").GetAttribute("data-resource-etag")); Assert.IsTrue(form.GetValidationMessages().Any());
        }
        cut.Find(".unsaved-changes-bar .button-secondary").Click();
        Assert.Contains("New saved profile", cut.Find(".resource-identity-summary").TextContent);
        await Select(cut, "definition/identity");
        Assert.AreEqual("newer-etag", cut.Find("form").GetAttribute("data-resource-etag"));
        Assert.AreSame(policy, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        Assert.AreEqual(0, client.Updates);
    }

    [TestMethod]
    public async Task JsonTypedDuringRefreshIsRetainedWithoutAFalseConflictForAnUnchangedSnapshot()
    {
        var client = new Client(); using var context = Context(client); var cut = context.Render<DataSourceProfileEditor>(p => p.Add(c => c.Name, "profile"));
        await Select(cut, "definition/contracts"); var form = cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        var pending = new TaskCompletionSource<ResourceSnapshot<Agentstration.DataSources.Contracts.DataSourceProfileResource>>(TaskCreationOptions.RunContinuationsAsynchronously); client.PendingRead = pending.Task;
        var refresh = cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        cut.Find("[data-testid=profile-schemaJson]").Input("{ private in-flight fixture");
        await cut.InvokeAsync(() => pending.SetResult(new(client.Saved, "initial-etag"))); await refresh;
        Assert.AreSame(form, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("{ private in-flight fixture", cut.Find("[data-testid=profile-schemaJson]").GetAttribute("value"));
        Assert.IsEmpty(cut.FindAll(".resource-details-feedback .form-alert-warning"));
        Assert.AreEqual("initial-etag", cut.Find("form").GetAttribute("data-resource-etag")); Assert.AreEqual(0, client.Updates);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.NotFound, true)]
    [DataRow(HttpStatusCode.ServiceUnavailable, false)]
    public async Task RefreshFailureRetainsOnlyContentThatIsStillAuthorized(HttpStatusCode status, bool yaml)
    {
        var client = new Client(); using var context = Context(client); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var cut = context.Render<DataSourceProfileEditor>(p => p.Add(c => c.Name, "profile"));
        await Select(cut, yaml ? "yaml" : "definition/contracts");
        cut.Find(yaml ? "[data-testid=profile-yaml]" : "[data-testid=profile-toolBindingsJson]").Input("{ private loss fixture");
        await Select(cut, "access"); var policy = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        await cut.InvokeAsync(() => policy.Parameters.Get(c => c.DraftChanged).InvokeAsync(true));
        client.ReadError = new AgentstrationApiException("fixture", "profile-fixture", status);
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        if (status == HttpStatusCode.ServiceUnavailable)
        {
            Assert.IsNotNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
            Assert.AreSame(policy, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
            Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
            Assert.AreEqual("{ private loss fixture", cut.Find("[data-testid=profile-toolBindingsJson]").GetAttribute("value"));
        }
        else
        {
            Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
            Assert.IsEmpty(cut.FindAll("form, .resource-yaml-editor")); Assert.DoesNotContain("private loss fixture", cut.Markup);
            Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        }
    }

    [TestMethod]
    public async Task RecreatedProfileClearsOldDefinitionAndPolicyRatherThanAdoptingTheReplacement()
    {
        var client = new Client(); using var context = Context(client); var cut = context.Render<DataSourceProfileEditor>(p => p.Add(c => c.Name, "profile"));
        await Select(cut, "definition/identity"); cut.Find("[data-testid=profile-display-name]").Input("private old identity fixture");
        client.Saved = client.Saved with { Uid = Guid.NewGuid() };
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource); Assert.IsEmpty(cut.FindAll("form"));
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task RefreshRetainsTheSeparatelyPreparedPublicationVersion()
    {
        var client = new Client(); using var context = Context(client); var cut = context.Render<DataSourceProfileEditor>(p => p.Add(c => c.Name, "profile"));
        await Select(cut, "revisions"); cut.Find("[data-testid=profile-publish-version]").Input("2.0.0");
        client.Saved = client.Saved with { Generation = 2, Definition = client.Saved.Definition with { Version = "1.1.0" } }; client.ReadEtag = "newer-etag";
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreEqual("2.0.0", cut.Find("[data-testid=profile-publish-version]").GetAttribute("value")); Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task SuccessfulDeletionInvalidatesTheSavedIdentityBeforeLeavingWithAPolicyDraft()
    {
        var client = new Client(); using var context = Context(client); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var cut = context.Render<DataSourceProfileEditor>(p => p.Add(c => c.Name, "profile"));
        await ResourceScreenDeletionAssertions.ConfirmAndReleasePolicyDraftAsync(cut, context.Services.GetRequiredService<NavigationManager>());
        Assert.AreEqual(1, client.Deletes); Assert.AreEqual("initial-etag", client.LastEtag);
    }
    [TestMethod]
    public async Task LosingReadAccessAfterPublicationClearsCachedDefinitionAndPolicy()
    {
        var client = new Client { LoseReadAfterPublication = true }; using var context = Context(client); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var cut = context.Render<DataSourceProfileEditor>(p => p.Add(c => c.Name, "profile"));
        await Select(cut, "access"); var policy = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        await cut.InvokeAsync(() => policy.Parameters.Get(c => c.DraftChanged).InvokeAsync(true));
        await Select(cut, "revisions"); await cut.Find(".resource-details-content button.button-primary").ClickAsync(new());
        Assert.AreEqual(1, client.Publications); Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
        Assert.IsEmpty(cut.FindComponents<Stub<ResourceAccessPanel>>()); Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }
    private static Task Select(IRenderedComponent<DataSourceProfileEditor> cut, string path) => cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection(path));
}
