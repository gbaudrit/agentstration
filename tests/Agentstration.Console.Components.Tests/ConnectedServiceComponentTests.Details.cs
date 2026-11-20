using System.Net;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ConnectedServiceComponentTests
{
    private static IRenderedComponent<ConnectedServiceDetails> FoundationDetails(BunitContext context, DetailsClient client, ResourceScopeRef owner, string? contextScope = null, string? resourceNamespace = null)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(client);
        context.Services.AddSingleton<IParametersClient>(new ParametersClientStub([]));
        context.Services.AddSingleton<ISecretsClient>(new SecretsClientStub([]));
        context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationScope(owner));
        var query = $"scopeRef={Uri.EscapeDataString(owner.Value)}&context={Uri.EscapeDataString(contextScope ?? owner.Value)}";
        if (resourceNamespace is not null) query += "&namespace=" + Uri.EscapeDataString(resourceNamespace);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/connected-services/redis?" + query);
        ConfigureNavigation(context);
        return context.Render<ConnectedServiceDetails>(p => p.Add(v => v.Name, "redis"));
    }
    private static ConnectedServiceTypeResource DetailsTypeWithoutBindings()
    {
        var type = TypeCatalog();
        return type with { Definition = type.Definition with { Versions = [type.Definition.Versions[0] with { Inputs = [] }] } };
    }
    private static Task DetailsSection(BunitContext context, IRenderedComponent<ConnectedServiceDetails> cut, string path) => cut.InvokeAsync(() => SelectSection(context, path));

    [TestMethod]
    public async Task ServiceDetailsShareTheCommonFormStateAndRetainTheInvalidDraftUntilDiscard()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        var type = DetailsTypeWithoutBindings(); var owner = ResourceScopeRef.Instance;
        var original = Resource(owner, type);
        var client = new DetailsClient(original, type, owner);
        var cut = FoundationDetails(context, client, owner);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await DetailsSection(context, cut, "definition/identity");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(adapter.SectionState, form.SectionState);
        Assert.AreSame(adapter.SectionPresentations, form.Sections);
        cut.Find("[data-testid=connected-service-display-name]").Change("");
        await cut.Find("[data-testid=connected-service-form]").SubmitAsync();
        Assert.IsTrue(form.EditContext.GetValidationMessages().Any());
        client.Current = original with { Generation = 2, Definition = original.Definition with { DisplayName = "New saved service" } };
        client.ReadEtag = "new-etag";
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("", cut.Find("[data-testid=connected-service-display-name]").GetAttribute("value"));
        Assert.IsTrue(form.EditContext.GetValidationMessages().Any());
        Assert.AreEqual("\"etag\"", cut.Find("[data-testid=connected-service-form]").GetAttribute("data-resource-etag"));
        StringAssert.Contains(cut.Markup, "A refreshed version is available");
        await DetailsSection(context, cut, "yaml");
        await DetailsSection(context, cut, "definition/identity");
        Assert.AreEqual("", cut.Find("[data-testid=connected-service-display-name]").GetAttribute("value"));
        cut.Find(".unsaved-changes-bar button[type=button]").Click();
        Assert.AreEqual("New saved service", cut.Find("[data-testid=connected-service-display-name]").GetAttribute("value"));
        Assert.AreEqual("new-etag", cut.Find("[data-testid=connected-service-form]").GetAttribute("data-resource-etag"));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.Unauthorized, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    public async Task ServiceDetailsRefreshRetainsRecoverableDraftAndClearsLostAccess(HttpStatusCode code, bool retain)
    {
        using var context = new BunitContext().WithCollectionPresentation();
        var owner = ResourceScopeRef.Instance; var type = DetailsTypeWithoutBindings();
        var client = new DetailsClient(Resource(owner, type), type, owner);
        var cut = FoundationDetails(context, client, owner);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await DetailsSection(context, cut, "definition/identity");
        cut.Find("[data-testid=connected-service-display-name]").Change("Private service draft");
        client.ReadError = new("read failed", "read-error", code);
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreEqual(retain, cut.FindAll("[data-testid=connected-service-form]").Count > 0);
        if (retain) Assert.AreEqual("Private service draft", cut.Find("[data-testid=connected-service-display-name]").GetAttribute("value"));
        else Assert.DoesNotContain("Private service draft", cut.Markup);
    }

    [TestMethod]
    public async Task ServiceDetailsKeepEditsMadeWhileARefreshIsPending()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        var owner = ResourceScopeRef.Instance; var type = DetailsTypeWithoutBindings();
        var original = Resource(owner, type); var client = new DetailsClient(original, type, owner);
        var cut = FoundationDetails(context, client, owner);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await DetailsSection(context, cut, "definition/identity");
        var response = new TaskCompletionSource<ResourceSnapshot<ConnectedServiceResource>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PendingRead = response.Task;
        var refresh = cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        cut.WaitForAssertion(() => Assert.AreEqual("true", cut.Find(".resource-details-body").GetAttribute("aria-busy")));
        Assert.IsFalse(cut.Find("[data-testid=connected-service-display-name]").HasAttribute("disabled"));
        cut.Find("[data-testid=connected-service-display-name]").Change("Typed during refresh");
        response.SetResult(new(original with { Generation = 2 }, "new-version"));
        await refresh;
        Assert.AreEqual("Typed during refresh", cut.Find("[data-testid=connected-service-display-name]").GetAttribute("value"));
        Assert.AreEqual("\"etag\"", cut.Find("[data-testid=connected-service-form]").GetAttribute("data-resource-etag"));
    }

    [TestMethod]
    public async Task ServiceDetailsDropAReplacedIdentityBeforeItsDependenciesLoad()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        var owner = ResourceScopeRef.Instance; var type = DetailsTypeWithoutBindings();
        var client = new DetailsClient(Resource(owner, type), type, owner);
        var cut = FoundationDetails(context, client, owner);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await DetailsSection(context, cut, "definition/identity");
        cut.Find("[data-testid=connected-service-display-name]").Change("Private replaced draft");
        client.Current = client.Current with { Uid = Guid.NewGuid() };
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.IsEmpty(cut.FindAll("[data-testid=connected-service-form]"));
        Assert.DoesNotContain("Private replaced draft", cut.Markup);
    }

    [TestMethod]
    public async Task ServiceDetailsInheritedContextKeepsInspectionSectionsAndLocksMutation()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        var type = DetailsTypeWithoutBindings(); var owner = ResourceScopeRef.Instance;
        var client = new DetailsClient(Resource(owner, type), type, owner);
        var cut = FoundationDetails(context, client, owner, ResourceScopeRef.Workspace(Guid.NewGuid()).Value);
        Assert.IsEmpty(cut.FindAll("[data-resource-section=access]"));
        Assert.IsTrue(cut.Find(".resource-header input[role=switch]").HasAttribute("disabled"));
        await DetailsSection(context, cut, "definition/identity");
        Assert.IsTrue(cut.Find("[data-testid=connected-service-definition-fields]").HasAttribute("disabled"));
        Assert.HasCount(1, cut.FindAll("[data-testid=inherited-resource-notice]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=connected-service-save]"));
        await cut.Find("[data-testid=connected-service-form]").SubmitAsync();
        Assert.IsNull(client.LastEtag);
        Assert.HasCount(1, cut.FindAll("[data-resource-section=discovery]"));
    }

    [TestMethod]
    public async Task ServiceDetailsManagedDefinitionDoesNotRemoveIndependentOwnerAccess()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        var type = DetailsTypeWithoutBindings(); var owner = ResourceScopeRef.Instance;
        var saved = Resource(owner, type) with { Governance = ResourceGovernance.Core("fixture") };
        var cut = FoundationDetails(context, new DetailsClient(saved, type, owner), owner);
        Assert.HasCount(1, cut.FindAll("[data-resource-section=access]"));
        await DetailsSection(context, cut, "definition/identity");
        Assert.IsTrue(cut.Find("[data-testid=connected-service-definition-fields]").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll("[data-testid=connected-service-save]"));
        Assert.HasCount(1, cut.FindAll("[data-resource-section=discovery]"));
    }

    [TestMethod]
    public async Task ServiceDetailsInvalidRequiredBindingRoutesToTheUnvisitedConfiguration()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        var type = TypeCatalog(); var owner = ResourceScopeRef.Instance;
        var client = new DetailsClient(Resource(owner, type), type, owner);
        var cut = FoundationDetails(context, client, owner);
        await DetailsSection(context, cut, "definition/identity");
        cut.Find("[data-testid=connected-service-display-name]").Change("Needs binding review");
        await cut.Find("[data-testid=connected-service-form]").SubmitAsync();
        Assert.AreEqual("#definition/configuration", new Uri(context.Services.GetRequiredService<NavigationManager>().Uri).Fragment);
        Assert.IsNull(client.LastEtag);
    }
    [TestMethod]
    [DataRow("en-US", HttpStatusCode.PreconditionFailed, "This service changed on the server.")]
    [DataRow("en-US", HttpStatusCode.Conflict, "This service changed on the server.")]
    [DataRow("fr-FR", HttpStatusCode.PreconditionFailed, "Ce service a été modifié sur le serveur.")]
    [DataRow("fr-FR", HttpStatusCode.Conflict, "Ce service a été modifié sur le serveur.")]
    public async Task ServiceDetailsLocalizeConflictsWithoutLosingTheDraftOrItsEtag(string locale, HttpStatusCode status, string expected)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext().WithCollectionPresentation();
        var owner = ResourceScopeRef.Instance; var type = DetailsTypeWithoutBindings();
        var client = new DetailsClient(Resource(owner, type), type, owner)
        {
            WriteError = new("Private backend concurrency detail", "conflict", status, "Resource version conflict")
        };
        var cut = FoundationDetails(context, client, owner);
        await DetailsSection(context, cut, "definition/identity");
        cut.Find("[data-testid=connected-service-display-name]").Change("Keep this conflicting draft");
        await cut.Find("[data-testid=connected-service-form]").SubmitAsync();
        StringAssert.Contains(cut.Find(".form-alert-danger").TextContent, expected);
        Assert.DoesNotContain("Private backend concurrency detail", cut.Markup);
        Assert.AreEqual("Keep this conflicting draft", cut.Find("[data-testid=connected-service-display-name]").GetAttribute("value"));
        Assert.AreEqual("\"etag\"", cut.Find("[data-testid=connected-service-form]").GetAttribute("data-resource-etag"));
        Assert.AreEqual("Redis", client.Current.Definition.DisplayName);
    }
}
