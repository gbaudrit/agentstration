using System.Net;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ExtensionsConsoleTests
{
    private static BunitContext ExtensionCreationContext(RegistrationCreationClient client, RegistrationCreationContextProvider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        context.Services.AddSingleton(new ConsoleContextState(provider ?? new RegistrationCreationContextProvider()));
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new RegistrationCreationScopeClient());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/extensions?tab=catalog&context=" + Uri.EscapeDataString(RegistrationCreationScopeClient.Owner.Value));
        return context;
    }
    private static async Task<IRenderedComponent<Agentstration.Web.Components.Pages.Extensions>> OpenRegistrationCreation(BunitContext context)
    {
        var page = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        page.WaitForAssertion(() => Assert.AreEqual("true", page.Find("[data-testid=distribution-extensions]").GetAttribute("data-interactive")));
        await page.Find("[data-testid=extensions-catalog] .page-header .button-primary, [data-testid=extensions-catalog] .collection-page-feedback .button-primary").ClickAsync(new());
        return page;
    }
    [TestMethod]
    public async Task RegistrationWizardAllowsIncompleteNavigationAndReturnsToFirstInvalidStep()
    {
        var client = new RegistrationCreationClient();
        using var context = ExtensionCreationContext(client);
        var page = await OpenRegistrationCreation(context);
        var wizard = page.FindComponent<CreationWizard>().Instance;
        await page.InvokeAsync(() => wizard.ContinueAsync());
        Assert.AreEqual("connection", wizard.CurrentStepId);
        Assert.AreEqual("false", page.Find("[data-wizard-step=identity]").GetAttribute("data-step-complete"));
        await page.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await page.Find("[data-testid=extension-create-registration]").ClickAsync(new());
        Assert.AreEqual("identity", wizard.CurrentStepId);
        Assert.IsNotEmpty(page.FindComponent<ResourceDefinitionForm>().Instance.EditContext.GetValidationMessages());
        Assert.IsNull(client.Created);
        Assert.HasCount(2, page.FindAll("[data-testid=creation-wizard-next], [data-testid=creation-wizard-next-top]"));
    }
    [TestMethod]
    public async Task RegistrationWizardRetainsDraftAndEditContextAcrossTabsAndCatalogRefresh()
    {
        var client = new RegistrationCreationClient();
        using var context = ExtensionCreationContext(client);
        var page = await OpenRegistrationCreation(context);
        page.Find("[data-testid=extension-registration-display-name]").Input("Pending registration");
        page.Find("[data-testid=extension-registration-name]").Input("pending-registration");
        var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        await page.InvokeAsync(() => page.FindComponent<CreationWizard>().Instance.GoToStepAsync("authentication"));
        await page.Find("[data-testid=extension-registration-authentication]").ChangeAsync(AepTransportAuthenticationMode.StaticBearer.ToString());
        page.Find("[data-testid=extension-registration-credential-name]").Input("keep-reference");
        await page.Find("[data-testid=extensions-tab-summary]").ClickAsync(new());
        await page.Find("[data-testid=extensions-tab-catalog]").ClickAsync(new());
        await page.Find("[data-testid=extensions-catalog-refresh]").ClickAsync(new());
        Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("authentication", page.FindComponent<CreationWizard>().Instance.CurrentStepId);
        Assert.AreEqual("Pending registration", page.Find("[data-testid=extension-registration-display-name]").GetAttribute("value"));
        Assert.AreEqual("keep-reference", page.Find("[data-testid=extension-registration-credential-name]").GetAttribute("value"));
        Assert.IsNull(client.Created);
    }
    [TestMethod]
    public async Task RegistrationWizardRejectsInvalidScopedCredentialThenPreservesItsExactReference()
    {
        var client = new RegistrationCreationClient();
        using var context = ExtensionCreationContext(client);
        var page = await OpenRegistrationCreation(context);
        page.Find("[data-testid=extension-registration-display-name]").Input("Created registration");
        page.Find("[data-testid=extension-registration-name]").Input("created-registration");
        page.Find("[data-testid=extension-registration-namespace]").Change("connections");
        await page.InvokeAsync(() => page.FindComponent<CreationWizard>().Instance.GoToStepAsync("connection"));
        page.Find("[data-testid=extension-registration-endpoint]").Input("https://extension.example.test/");
        await page.Find("[data-testid=extension-registration-enabled]").ChangeAsync(false);
        await page.InvokeAsync(() => page.FindComponent<CreationWizard>().Instance.GoToStepAsync("authentication"));
        await page.Find("[data-testid=extension-registration-authentication]").ChangeAsync(AepTransportAuthenticationMode.StaticBearer.ToString());
        page.Find("[data-testid=extension-registration-credential-name]").Input("token");
        page.Find("[data-testid=extension-registration-credential-namespace]").Input("credentials");
        page.Find("[data-testid=extension-registration-credential-scope]").Input("invalid-scope");
        await page.InvokeAsync(() => page.FindComponent<CreationWizard>().Instance.GoToStepAsync("review"));
        await page.Find("[data-testid=extension-create-registration]").ClickAsync(new());
        Assert.AreEqual("authentication", page.FindComponent<CreationWizard>().Instance.CurrentStepId);
        Assert.IsNotEmpty(page.FindComponent<ResourceDefinitionForm>().Instance.EditContext.GetValidationMessages());
        Assert.IsNull(client.Created);
        page.Find("[data-testid=extension-registration-credential-scope]").Input("/instance");
        await page.InvokeAsync(() => page.FindComponent<CreationWizard>().Instance.GoToStepAsync("review"));
        await page.Find("[data-testid=extension-create-registration]").ClickAsync(new());
        Assert.IsNotNull(client.Created);
        Assert.AreEqual("connections", client.Created.Namespace);
        Assert.AreEqual(RegistrationCreationScopeClient.Owner, client.Created.ScopeRef);
        Assert.AreEqual("token", client.Created.Properties.Credential!.Name);
        Assert.AreEqual(ResourceNamespace.Parse("credentials"), client.Created.Properties.Credential.Namespace);
        Assert.AreEqual(ResourceScopeRef.Instance, client.Created.Properties.Credential.ScopeRef);
        Assert.IsFalse(client.Created.Properties.Enabled);
        Assert.IsEmpty(page.FindAll("[data-testid=extension-registration-wizard]"));
    }
    [TestMethod]
    public async Task RegistrationWizardFailedCreationKeepsAllInputForAnExplicitRetry()
    {
        var client = new RegistrationCreationClient { Failure = new AgentstrationApiException("Rejected create", "fixture-create", HttpStatusCode.UnprocessableEntity) };
        using var context = ExtensionCreationContext(client);
        var page = await OpenRegistrationCreation(context);
        page.Find("[data-testid=extension-registration-display-name]").Input("Retained rejected registration");
        page.Find("[data-testid=extension-registration-name]").Input("rejected-registration");
        await page.InvokeAsync(() => page.FindComponent<CreationWizard>().Instance.GoToStepAsync("connection"));
        page.Find("[data-testid=extension-registration-endpoint]").Input("https://extension.example.test/");
        await page.InvokeAsync(() => page.FindComponent<CreationWizard>().Instance.GoToStepAsync("review"));
        await page.Find("[data-testid=extension-create-registration]").ClickAsync(new());
        Assert.Contains("Rejected create", page.Find("[data-testid=extension-registration-creation] [role=alert]").TextContent);
        Assert.AreEqual("Retained rejected registration", page.Find("[data-testid=extension-registration-display-name]").GetAttribute("value"));
        Assert.AreEqual(1, client.CreateCalls);
        client.Failure = null;
        await page.Find("[data-testid=extension-create-registration]").ClickAsync(new());
        Assert.AreEqual(2, client.CreateCalls);
        Assert.IsEmpty(page.FindAll("[data-testid=extension-registration-wizard]"));
    }
    [TestMethod]
    public async Task RegistrationWizardCreationAuthorizationIsIndependentOfAggregatedInventoryDenial()
    {
        var client = new RegistrationCreationClient { InventoryFailure = new AgentstrationApiException("Private aggregate denial", "fixture-aggregate", HttpStatusCode.Forbidden) };
        using var context = ExtensionCreationContext(client);
        var page = await OpenRegistrationCreation(context);
        Assert.HasCount(1, page.FindAll("[data-testid=extensions-catalog] [role=alert]"));
        Assert.DoesNotContain("Private aggregate denial", page.Markup);
        Assert.IsEmpty(page.FindAll("[data-testid=extension-inventory-row]"));
        page.Find("[data-testid=extension-registration-display-name]").Input("Authorized tenant registration");
        page.Find("[data-testid=extension-registration-name]").Input("tenant-registration");
        await page.InvokeAsync(() => page.FindComponent<CreationWizard>().Instance.GoToStepAsync("connection"));
        page.Find("[data-testid=extension-registration-endpoint]").Input("https://extension.example.test/");
        await page.InvokeAsync(() => page.FindComponent<CreationWizard>().Instance.GoToStepAsync("review"));
        await page.Find("[data-testid=extension-create-registration]").ClickAsync(new());
        Assert.IsNotNull(client.Created);
        Assert.AreEqual(RegistrationCreationScopeClient.Owner, client.Created.ScopeRef);
        Assert.AreEqual("tenant-registration", client.Created.Name);
        Assert.IsEmpty(page.FindAll("[data-testid=extension-registration-wizard]"));
        Assert.HasCount(1, page.FindAll("[data-testid=extensions-catalog] [role=alert]"));
    }

    [TestMethod]
    public async Task RegistrationWizardRetainsItsOwnDraftWhenAggregatedInventoryRefreshIsForbidden()
    {
        var client = new RegistrationCreationClient();
        using var context = ExtensionCreationContext(client);
        var page = await OpenRegistrationCreation(context);
        page.Find("[data-testid=extension-registration-display-name]").Input("Independent local draft");
        var editContext = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        await page.InvokeAsync(() => page.FindComponent<CreationWizard>().Instance.GoToStepAsync("authentication"));
        client.InventoryFailure = new AgentstrationApiException("Private aggregate denial", "fixture-aggregate", HttpStatusCode.Forbidden);
        await page.Find("[data-testid=extensions-catalog-refresh]").ClickAsync(new());
        Assert.AreSame(editContext, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("authentication", page.FindComponent<CreationWizard>().Instance.CurrentStepId);
        Assert.AreEqual("Independent local draft", page.Find("[data-testid=extension-registration-display-name]").GetAttribute("value"));
        Assert.HasCount(1, page.FindAll("[data-testid=extensions-catalog] [role=alert]"));
        Assert.IsNull(client.Created);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RegistrationWizardRespectsLiveReadAndWriteLoss(bool loseRead)
    {
        var client = new RegistrationCreationClient();
        var provider = new RegistrationCreationContextProvider();
        using var context = ExtensionCreationContext(client, provider);
        var page = await OpenRegistrationCreation(context);
        page.Find("[data-testid=extension-registration-display-name]").Input("Private independent draft");
        var editContext = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        provider.Permissions = new HashSet<string>([loseRead ? AuthorizationPermissions.ResourcesWrite : AuthorizationPermissions.ResourcesRead], StringComparer.Ordinal);
        client.InventoryFailure = new AgentstrationApiException("Private aggregate denial", "fixture-aggregate", HttpStatusCode.Forbidden);
        await page.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(default));
        page.WaitForAssertion(() =>
        {
            Assert.IsEmpty(page.FindAll("[data-testid=extensions-catalog] .page-header .button-primary, [data-testid=extensions-catalog] .collection-page-feedback .button-primary"));
            if (loseRead)
            {
                Assert.IsEmpty(page.FindAll("[data-testid=extension-registration-wizard]"));
                Assert.DoesNotContain("Private independent draft", page.Markup);
            }
            else
            {
                Assert.AreSame(editContext, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
                Assert.AreEqual("Private independent draft", page.Find("[data-testid=extension-registration-display-name]").GetAttribute("value"));
                Assert.IsTrue(page.Find("[data-testid=extension-registration-create-form] > fieldset").HasAttribute("disabled"));
            }
        });
        Assert.IsNull(client.Created);
    }

    [TestMethod]
    public async Task RegistrationCreationDoesNotOfferAnUnusableTenantWizardInInstanceContext()
    {
        var client = new RegistrationCreationClient();
        using var context = ExtensionCreationContext(client, new RegistrationCreationContextProvider { Kind = ConsoleContextKind.Instance });
        var page = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        page.WaitForAssertion(() => Assert.AreEqual("true", page.Find("[data-testid=distribution-extensions]").GetAttribute("data-interactive")));
        Assert.IsEmpty(page.FindAll("[data-testid=extensions-catalog] .page-header .button-primary, [data-testid=extensions-catalog] .collection-page-feedback .button-primary, [data-testid=extension-registration-wizard]"));
        Assert.IsNull(client.Created);
    }

    private sealed class RegistrationCreationContextProvider : IConsoleContextProvider
    {
        public ConsoleContextKind Kind { get; set; } = ConsoleContextKind.Tenant;
        public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>([AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite], StringComparer.Ordinal);
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken token) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "Public fixture user", Kind == ConsoleContextKind.Instance ? "/instance" : RegistrationCreationScopeClient.Owner.Value,
            Kind, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "fixture", "Public fixture Tenant", Guid.Empty, "", "", Permissions, [], []));
    }

    private sealed class RegistrationCreationScopeClient : IResourceScopeInventoryClient
    {
        public static ResourceScopeRef Owner { get; } = ResourceScopeRef.Tenant(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken token) => Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(Owner, ResourceScopeKind.Tenant, "Public fixture Tenant", true)]);
    }
    private sealed class RegistrationCreationClient : IExtensionsClient
    {
        public CreateExtensionRegistrationRequest? Created { get; private set; }
        public Exception? Failure { get; set; }
        public int CreateCalls { get; private set; }
        public Exception? InventoryFailure { get; set; }
        public Task<IReadOnlyList<ExtensionInventoryItemResponse>> GetExtensionInventoryAsync(CancellationToken token) => InventoryFailure is { } error
            ? Task.FromException<IReadOnlyList<ExtensionInventoryItemResponse>>(error) : Task.FromResult<IReadOnlyList<ExtensionInventoryItemResponse>>([]);
        public Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ExtensionResponse>>([]);
        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ExtensionRegistrationResource>>([]);
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace ns, string name, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken token)
        {
            CreateCalls++; if (Failure is { } error) return Task.FromException<ResourceSnapshot<ExtensionRegistrationResource>>(error);
            Created = request;
            return Task.FromResult(new ResourceSnapshot<ExtensionRegistrationResource>(new ExtensionRegistrationResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = ExtensionKinds.ExtensionRegistration,
                Metadata = new() { Name = request.Name, Namespace = ResourceNamespace.Parse(request.Namespace) }, Uid = Guid.NewGuid(), ScopeRef = request.ScopeRef, Definition = request.Properties }, "\"created\""));
        }
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace ns, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken token) => throw new NotSupportedException();
        public Task DeleteRegistrationAsync(ResourceNamespace ns, string name, string etag, CancellationToken token) => throw new NotSupportedException();
    }
}
