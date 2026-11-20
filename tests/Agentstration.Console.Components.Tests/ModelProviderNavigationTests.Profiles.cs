using System.Net;
using System.Text.Json;
using Agentstration.Extensions.Contracts;
using Agentstration.Models;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ModelProviderNavigationTests
{
    private static IRenderedComponent<ModelProfileEditor> FoundationProfile(BunitContext context, ExistingModelProfilesClient client)
    {
        context.Services.AddSingleton<IModelProfilesClient>(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/modelprofiles/shared-reasoning?scopeRef=%2Finstance&context=%2Finstance");
        return context.Render<ModelProfileEditor>(parameters => parameters.Add(value => value.Name, "shared-reasoning"));
    }
    private static Task ProfileSection(BunitContext context, IRenderedComponent<ModelProfileEditor> cut, string path) =>
        cut.InvokeAsync(() => { var navigation = context.Services.GetRequiredService<NavigationManager>(); navigation.NavigateTo(navigation.Uri.Split('#')[0] + "#" + path); });

    [TestMethod]
    public void ProfileWizardNavigatesIncompleteStepsAndValidatesBeforeCreation()
    {
        using var context = CreateContext(out _);
        context.Services.AddSingleton<IModelProfilesClient>(new StubModelProfilesClient());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/modelprofiles/new");
        var cut = context.Render<ModelProfileEditor>();
        cut.Find("[data-wizard-step=review]").Click();
        Assert.AreEqual("step", cut.Find("[data-wizard-step=review]").GetAttribute("aria-current"));
        Assert.AreEqual("false", cut.Find("[data-wizard-step=review]").GetAttribute("data-step-complete"));
        cut.Find("[data-testid=model-profile-save]").Click();
        Assert.AreEqual("step", cut.Find("[data-wizard-step=identity]").GetAttribute("aria-current"));
        Assert.IsTrue(cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext.GetValidationMessages().Any());
    }

    [TestMethod]
    public async Task ProfileRetainsInvalidSchemaContextAndOriginalEtagUntilExplicitDiscard()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(out _);
        var client = new ExistingModelProfilesClient();
        var cut = FoundationProfile(context, client);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await ProfileSection(context, cut, "definition/generation");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(adapter.SectionState, form.SectionState);
        Assert.AreSame(adapter.SectionPresentations, form.Sections);
        cut.FindAll("select").Single(element => element.QuerySelector("option[value=JsonSchema]") is not null).Change("JsonSchema");
        cut.FindAll("textarea").Single(element => element.GetAttribute("rows") == "10").Change("{invalid-schema");
        await cut.Find("[data-testid=model-profile-form]").SubmitAsync();
        Assert.IsTrue(form.EditContext.GetValidationMessages().Any());
        client.Profile = client.Profile with { Generation = 2, Definition = client.Profile.Definition with { DisplayName = "New saved name" } };
        client.ReadEtag = "\"profile-newer\"";
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("\"profile-etag\"", cut.Find("[data-testid=model-profile-form]").GetAttribute("data-resource-etag"));
        StringAssert.Contains(cut.Markup, "A newer version is available");
        Assert.IsTrue(form.EditContext.GetValidationMessages().Any());
        await ProfileSection(context, cut, "yaml");
        await ProfileSection(context, cut, "definition/generation");
        Assert.AreEqual("{invalid-schema", cut.FindAll("textarea").Single(element => element.GetAttribute("rows") == "10").GetAttribute("value"));
        Assert.IsNull(client.UpdatedRequest);
        cut.Find(".unsaved-changes-bar button[type=button]").Click();
        await ProfileSection(context, cut, "definition/identity");
        Assert.AreEqual("New saved name", cut.Find("[data-testid=model-profile-display-name]").GetAttribute("value"));
        Assert.AreEqual("\"profile-newer\"", cut.Find("[data-testid=model-profile-form]").GetAttribute("data-resource-etag"));
    }

    [TestMethod]
    public async Task ProfileSaveUsesOriginalVersionAndPreservesRelativeProviderReference()
    {
        using var context = CreateContext(out _);
        var client = new ExistingModelProfilesClient();
        client.Profile = client.Profile with { Definition = client.Profile.Definition with { Provider = new ResourceReference("ollama/local", @namespace: ProviderNamespace) } };
        var cut = FoundationProfile(context, client);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await ProfileSection(context, cut, "definition/identity");
        cut.Find("[data-testid=model-profile-display-name]").Change("My draft");
        client.ReadEtag = "\"newer\"";
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        await cut.Find("[data-testid=model-profile-form]").SubmitAsync();
        Assert.AreEqual("\"profile-etag\"", client.UpdateEtag);
        Assert.AreEqual("My draft", client.UpdatedRequest?.Properties.DisplayName);
        Assert.AreEqual(new ResourceReference("ollama/local", @namespace: ProviderNamespace), client.UpdatedRequest?.Properties.Provider);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.Unauthorized, false)]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    public async Task ProfileRefreshPreservesRecoverableDraftAndClearsDeniedData(HttpStatusCode code, bool retain)
    {
        using var context = CreateContext(out _);
        var client = new ExistingModelProfilesClient();
        var cut = FoundationProfile(context, client);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await ProfileSection(context, cut, "definition/identity");
        cut.Find("[data-testid=model-profile-display-name]").Change("Private draft");
        client.ReadFailure = new("read failed", "profile-error", code);
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreEqual(retain, cut.FindAll("[data-testid=model-profile-form]").Count > 0);
        if (retain) Assert.AreEqual("Private draft", cut.Find("[data-testid=model-profile-display-name]").GetAttribute("value"));
        else Assert.DoesNotContain("Private draft", cut.Markup);
    }

    [TestMethod]
    public async Task ProfileReplacedUidClearsOldDraft()
    {
        using var context = CreateContext(out _);
        var client = new ExistingModelProfilesClient();
        var cut = FoundationProfile(context, client);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await ProfileSection(context, cut, "definition/identity");
        cut.Find("[data-testid=model-profile-display-name]").Change("Private old identity");
        client.Profile = client.Profile with { Uid = Guid.NewGuid() };
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.IsEmpty(cut.FindAll("[data-testid=model-profile-form]"));
        Assert.DoesNotContain("Private old identity", cut.Markup);
    }

    [TestMethod]
    public async Task ProfileRefreshAcceptsAKnownEmptyUsageCountAfterReferencesAreRemoved()
    {
        using var context = CreateContext(out _);
        var client = new ExistingModelProfilesClient();
        var cut = FoundationProfile(context, client);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        cut.Find("[data-testid=model-profile-delete]").Click();
        Assert.IsEmpty(cut.FindAll("[data-testid=model-profile-delete-dialog-confirm]"));
        client.UsagesOverride = new([], 0);
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        cut.Find("[data-testid=model-profile-delete]").Click();
        Assert.HasCount(1, cut.FindAll("[data-testid=model-profile-delete-dialog-confirm]"));
    }

    [TestMethod]
    public async Task ProfileGuidedOptionsKeepPinnedEnvelopeAndLocalInvalidInputDuringRefresh()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var schema = JsonDocument.Parse("""{"type":"object","required":["metadata"],"properties":{"metadata":{"type":"object"}}}""");
        var contract = new ExtensionOptionSetResponse("profile-options", "model-provider", "ollama", "model-profile", "2",
            [new("1", "sha256:one", schema.RootElement.Clone(), false), new("2", "sha256:two", schema.RootElement.Clone(), false)], []);
        ExtensionResponse extension = new(RegistrationName: "ollama-extension", RegistrationNamespace: ProviderNamespace.Value,
            Endpoint: new Uri("http://localhost:5260"), Status: "available", Extension: new("ollama.extension", "Ollama", "1.0.0", null),
            Contributions: [new("model-provider", "ollama")], OptionSets: [contract], Usages: [], Providers: [], Details: null, DiscoverySource: "manual");
        using var context = CreateContext(out _, [extension]);
        var client = new ExistingModelProfilesClient();
        client.Profile = client.Profile with { Definition = client.Profile.Definition with { ProviderOptions = new Dictionary<string, VersionedExtensionOptions> { ["ollama"] = new() { OptionSet = "profile-options", Version = "1", SchemaDigest = "sha256:one", Values = JsonSerializer.SerializeToElement(new { metadata = new { retained = true } }) } } } };
        var cut = FoundationProfile(context, client);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await ProfileSection(context, cut, "definition/provider-options");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        cut.Find("textarea[rows='5']").Change("not-json");
        await cut.Find("[data-testid=model-profile-form]").SubmitAsync();
        StringAssert.Contains(string.Join(" ", form.EditContext.GetValidationMessages()), "objet JSON valide");
        Assert.IsNotNull(cut.Find(".form-field[aria-invalid=true] textarea[rows='5']"));
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("not-json", cut.Find("textarea[rows='5']").GetAttribute("value"));
        cut.Find("textarea[rows='5']").Change("{\"retained\":true}");
        cut.FindAll("button").Single(button => button.TextContent.Contains("JSON brut", StringComparison.Ordinal)).Click();
        var raw = cut.Find("textarea[rows='8']").GetAttribute("value");
        Assert.IsNotNull(raw);
        StringAssert.Contains(raw, "sha256:one");
        await cut.Find("[data-testid=model-profile-form]").SubmitAsync();
        Assert.AreEqual("1", client.UpdatedRequest?.Properties.ProviderOptions["ollama"].Version);
        Assert.AreEqual("sha256:one", client.UpdatedRequest?.Properties.ProviderOptions["ollama"].SchemaDigest);
        Assert.IsTrue(client.UpdatedRequest!.Properties.ProviderOptions["ollama"].Values.GetProperty("metadata").GetProperty("retained").GetBoolean());
    }

    [TestMethod]
    public void ProfileWizardCompletionUsesActualModelRangesAndRequiredFields()
    {
        var model = new ModelProfileEditorModel { Name = "profile", Namespace = "default", DisplayName = "Profile", ScopeRef = ResourceScopeRef.Instance, ProviderName = "provider", ModelName = "model", TopP = 2 };
        Assert.IsTrue(model.InvalidFields().SetEquals([nameof(model.TopP)]));
        model.TopP = .5;
        model.JsonSchema = "{invalid";
        Assert.IsTrue(model.InvalidFields().SetEquals([nameof(model.JsonSchema)]));
    }
}
