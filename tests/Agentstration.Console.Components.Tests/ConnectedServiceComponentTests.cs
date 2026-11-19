using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Components.Values;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

using ConnectedServiceDetailsPage = Agentstration.Web.Components.Pages.ConnectedServiceDetails;
using ConnectedServiceEditorPage = Agentstration.Web.Components.Pages.ConnectedServiceEditor;
using ConnectedServicesPage = Agentstration.Web.Components.Pages.ConnectedServices;

[TestClass]
public sealed class ConnectedServiceComponentTests
{
    [TestMethod]
    public void CatalogRendersTypeAvailabilityAndExactScope()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new StubClient(Resource(scope)));

        var rendered = context.Render<ConnectedServicesPage>();

        rendered.WaitForAssertion(() =>
        {
            var row = rendered.Find("[data-testid='connected-service-row']");
            StringAssert.Contains(row.TextContent, "Redis");
            StringAssert.Contains(row.TextContent, "Ready");
            var nameLink = rendered.Find("[data-testid='connected-service-name-link']");
            Assert.AreEqual("resource-name-link", nameLink.ClassName);
            StringAssert.Contains(nameLink.GetAttribute("href")!, $"scopeRef={Uri.EscapeDataString(scope.Value)}");
            Assert.DoesNotContain("Inspect", row.TextContent, StringComparison.OrdinalIgnoreCase);
        });
    }

    [TestMethod]
    [DataRow("en-US", "Required fields")]
    [DataRow("fr-FR", "Champs obligatoires")]
    public void EditorUsesSharedBindingsAndOnlyOffersCompatibleUsableConfiguredResources(string locale, string requiredHint)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext().WithCollectionPresentation();
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new EditorClient(TypeCatalog(), workspace));
        context.Services.AddSingleton<IParametersClient>(new ParametersClientStub(
        [
            Parameter("workspace-endpoint", "Workspace endpoint", workspace, ParameterValueType.Text),
            Parameter("workspace-retries", "Workspace retries", workspace, ParameterValueType.WholeNumber),
            Parameter("granted-endpoint", "Granted endpoint", tenant, ParameterValueType.Text,
                new() { Grants = [new(workspace)] }),
            Parameter("wrong-type", "Wrong type", workspace, ParameterValueType.Logical),
            Parameter("not-granted", "Not granted", tenant, ParameterValueType.Text)
        ]));
        context.Services.AddSingleton<ISecretsClient>(new SecretsClientStub(
        [
            Secret("configured-secret", "Configured secret", workspace, true),
            Secret("granted-secret", "Granted secret", tenant, true, new() { Grants = [new(workspace)] }),
            Secret("empty-secret", "Empty secret", workspace, false),
            Secret("not-granted-secret", "Not granted secret", tenant, true)
        ]));

        var rendered = context.Render<ConnectedServiceEditorPage>();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("* " + requiredHint, rendered.Find(".required-fields-hint").TextContent.Trim());
            Assert.IsEmpty(rendered.FindAll(".form-alert-info"));
            Assert.IsEmpty(rendered.FindAll(".page-header .eyebrow"));
            Assert.HasCount(1, rendered.FindAll(".connected-service-configuration .schema-input-editor"));
            Assert.IsNotNull(rendered.Find(".page-header input[role=switch]"));
            Assert.IsEmpty(rendered.FindAll(".validation-errors"));
            var configuration = rendered.Find(".connected-service-configuration");
            Assert.IsTrue(configuration.InnerHtml.IndexOf("binding-requirements", StringComparison.Ordinal) < configuration.InnerHtml.IndexOf("schema-input-editor", StringComparison.Ordinal));
            Assert.IsNotNull(rendered.Find(".form-grid [data-testid='connected-service-type']"));
            var editors = rendered.FindComponents<ValueBindingRequirementEditor>();
            Assert.HasCount(3, editors);

            var endpoint = editors.Single(value => value.Instance.Requirement.Id == "endpoint").Instance;
            CollectionAssert.AreEqual(
                new[] { ValueBindingSourceKind.Literal, ValueBindingSourceKind.Parameter, ValueBindingSourceKind.Secret },
                endpoint.AllowedSources.ToArray());
            Assert.AreEqual(ValueBindingSourceKind.Literal, endpoint.Selection.Kind);
            CollectionAssert.AreEquivalent(
                new[] { "workspace-endpoint", "granted-endpoint", "configured-secret", "granted-secret" },
                endpoint.Options.Select(value => value.Address.Name).ToArray());

            var retries = editors.Single(value => value.Instance.Requirement.Id == "retries").Instance;
            CollectionAssert.AreEqual(new[] { ValueBindingSourceKind.Literal, ValueBindingSourceKind.Parameter }, retries.AllowedSources.ToArray());
            CollectionAssert.AreEqual(new[] { "workspace-retries" }, retries.Options.Select(value => value.Address.Name).ToArray());

            var credential = editors.Single(value => value.Instance.Requirement.Id == "credential").Instance;
            CollectionAssert.AreEqual(new[] { ValueBindingSourceKind.Secret }, credential.AllowedSources.ToArray());
            CollectionAssert.AreEquivalent(new[] { "configured-secret", "granted-secret" }, credential.Options.Select(value => value.Address.Name).ToArray());
            Assert.IsNotNull(credential.CreationContext);
            Assert.AreEqual(workspace, credential.CreationContext.ScopeRef);
        });
        rendered.Find("input[role=switch]").Change(false);
        Assert.IsFalse(((ConnectedServiceEditorModel)rendered.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance.EditContext!.Model).Enabled);
    }

    [TestMethod]
    [DataRow("en-US", "Manage the service configuration and review its availability.")]
    [DataRow("fr-FR", "Gérez la configuration du service et consultez sa disponibilité.")]
    public void DetailsUseResourceNavigationEmbedDefinitionAndDoNotExposeAnEditAction(string locale, string description)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext().WithCollectionPresentation();
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var type = TypeCatalog();
        var original = Resource(workspace, type);
        var resource = original with { Definition = original.Definition with {
            Options = JsonSerializer.SerializeToElement(new { candidatePaths = new[] { "/api/explore/v2.1/swagger.json" }, retries = 3 }),
            Bindings = [new() { InputId = "endpoint", Kind = ConnectedServiceBindingKind.Literal, Literal = JsonSerializer.SerializeToElement("https://example.test/" + new string('a', 200)) }]
        } };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new DetailsClient(resource, type, workspace));
        context.Services.AddSingleton<IParametersClient>(new ParametersClientStub([]));
        context.Services.AddSingleton<ISecretsClient>(new SecretsClientStub([]));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"/connected-services/{resource.Name}?scopeRef={Uri.EscapeDataString(workspace.Value)}");

        ConfigureNavigation(context);
        var rendered = context.Render<ConnectedServiceDetailsPage>(parameters => parameters
            .Add(component => component.Name, resource.Name));

        rendered.WaitForAssertion(() =>
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(description));
            StringAssert.Contains(rendered.Find(".resource-header").TextContent, resource.Definition.DisplayName);
            Assert.HasCount(5, rendered.FindAll(".resource-navigation-rail a[data-resource-section]"));
            Assert.AreEqual(workspace.Value, rendered.Find(".resource-header .resource-owner-scope").GetAttribute("data-scope-ref"));
            Assert.HasCount(3, rendered.FindAll(".metric-card"));
            Assert.HasCount(1, rendered.FindAll(".resource-overview .resource-identity-summary"));
            Assert.IsEmpty(rendered.FindAll(".resource-overview .schema-input-editor"));
            Assert.DoesNotContain("Exports and capabilities", rendered.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("sha256:", rendered.Find(".resource-overview").TextContent, StringComparison.Ordinal);
            Assert.IsEmpty(rendered.FindAll("a[href*='/edit']"));
            Assert.HasCount(3, rendered.FindAll(".connected-service-configuration-summary tbody tr"));
            StringAssert.Contains(rendered.Find(".connected-service-configuration-summary").TextContent, "/api/explore/v2.1/swagger.json");
            StringAssert.Contains(rendered.Find(".connected-service-configuration-summary").TextContent, new string('a', 200));
            Assert.HasCount(1, rendered.FindAll(".resource-header input[role=switch]"));
            Assert.IsEmpty(rendered.FindAll(".resource-header .button-danger"));
            Assert.DoesNotContain("Saved configuration and observed availability", rendered.Markup,
                StringComparison.Ordinal);
        });

        SelectSection(context, "definition/configuration");
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll("[data-testid='connected-service-form']"));
            Assert.IsEmpty(rendered.FindAll(".form-alert-danger"));
            Assert.HasCount(3, rendered.FindComponents<ValueBindingRequirementEditor>());
            Assert.HasCount(1, rendered.FindAll(".resource-header input[role=switch]"));
        });

        SelectSection(context, "yaml");
        rendered.WaitForAssertion(() => StringAssert.Contains(
            rendered.Find("[data-testid='connected-service-yaml-viewer'] textarea").GetAttribute("value") ?? rendered.Find("[data-testid='connected-service-yaml-viewer'] textarea").TextContent,
            "apiVersion"));
    }

    [TestMethod]
    [DataRow("en-US", "No Connected Services")]
    [DataRow("fr-FR", "Aucun service connecté")]
    public void EmptyCatalogOffersCreationWithoutFiltersOrInformationBanner(string locale, string title)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new StubClient(null));
        var rendered = context.Render<ConnectedServicesPage>();
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(title, rendered.Find(".empty-state h2").TextContent);
            Assert.IsNotNull(rendered.Find(".empty-state a[href='/connected-services/new']"));
            Assert.IsEmpty(rendered.FindAll(".connected-services-toolbar, .form-alert-info, .page-header .eyebrow"));
        });
    }

    [TestMethod]
    public void FilteredCatalogOffersResetWithoutAnotherRequest()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new StubClient(Resource(ResourceScopeRef.Instance)));
        var rendered = context.Render<ConnectedServicesPage>();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='connected-service-row']")));
        rendered.Find(".connected-services-toolbar input").Change("no-match");
        Assert.IsNotNull(rendered.Find(".empty-state button"));
        rendered.Find(".empty-state button").Click();
        Assert.HasCount(1, rendered.FindAll("[data-testid='connected-service-row']"));
    }

    [TestMethod]
    public async Task HeaderActivationPreservesDraftAndRefreshesEtagAcrossTabsAndFailures()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var originalType = TypeCatalog();
        var type = originalType with { Definition = originalType.Definition with { Versions = [originalType.Definition.Versions[0] with { Inputs = [] }] } };
        var resource = Resource(workspace, type);
        var client = new DetailsClient(resource, type, workspace);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(client);
        context.Services.AddSingleton<IParametersClient>(new ParametersClientStub([]));
        context.Services.AddSingleton<ISecretsClient>(new SecretsClientStub([]));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/connected-services/{resource.Name}?scopeRef={Uri.EscapeDataString(workspace.Value)}");
        ConfigureNavigation(context);
        var rendered = context.Render<ConnectedServiceDetailsPage>(p => p.Add(v => v.Name, resource.Name));
        SelectSection(context, "definition/configuration");
        rendered.Find("[data-testid=connected-service-display-name]").Change("Unsaved name");
        SelectSection(context, "yaml");
        rendered.Find(".resource-header input[role=switch]").Change(false);
        await rendered.InvokeAsync(() => rendered.FindComponents<ConfirmationDialog>().Single(v => v.Instance.IsOpen).Instance.OnCancel.InvokeAsync());
        Assert.AreEqual(0, client.Deactivations);
        Assert.IsTrue(rendered.Find(".resource-header input[role=switch]").HasAttribute("checked"));
        rendered.Find(".resource-header input[role=switch]").Change(false);
        await rendered.InvokeAsync(() => rendered.FindComponents<ConfirmationDialog>().Single(v => v.Instance.IsOpen).Instance.OnConfirm.InvokeAsync());
        Assert.AreEqual(1, client.Deactivations);
        Assert.IsFalse(rendered.Find(".resource-header input[role=switch]").HasAttribute("checked"));
        client.FailUpdate = true;
        rendered.Find(".resource-header input[role=switch]").Change(true);
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll(".form-alert-danger")));
        Assert.IsFalse(rendered.Find(".resource-header input[role=switch]").HasAttribute("checked"));
        client.FailUpdate = false;
        rendered.Find(".resource-header input[role=switch]").Change(true);
        rendered.WaitForAssertion(() => Assert.IsTrue(client.Current.Definition.Enabled));
        Assert.AreEqual("Redis", client.Current.Definition.DisplayName, "Activation must not save other draft fields.");
        SelectSection(context, "definition/configuration");
        Assert.AreEqual("Unsaved name", rendered.Find("[data-testid=connected-service-display-name]").GetAttribute("value"));
        rendered.Find("form").Submit();
        rendered.WaitForAssertion(() => Assert.AreEqual("Unsaved name", client.Current.Definition.DisplayName));
        Assert.AreEqual("etag-2", client.LastEtag);
        Assert.AreEqual(workspace, client.LastScope);
        Assert.IsTrue(client.Current.Definition.Enabled);
        var editor = rendered.FindComponent<ConnectedServiceEditorPage>().Instance;
        rendered.Find("[data-testid=connected-service-display-name]").Change("Discard this name");
        SelectSection(context, "overview");
        StringAssert.Contains(rendered.Find(".resource-identity-summary").TextContent, "Unsaved name");
        Assert.DoesNotContain("Discard this name", rendered.Find(".resource-identity-summary").TextContent);
        rendered.Find(".unsaved-changes-bar button[type=button]").Click();
        SelectSection(context, "definition/configuration");
        Assert.AreSame(editor, rendered.FindComponent<ConnectedServiceEditorPage>().Instance);
        Assert.AreEqual("Unsaved name", rendered.Find("[data-testid=connected-service-display-name]").GetAttribute("value"));
        Assert.HasCount(0, rendered.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public void InheritedServiceActivationIsDisabled()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        var type = TypeCatalog();
        var resource = Resource(ResourceScopeRef.Instance, type);
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationScope(workspace));
        context.Services.AddSingleton<IConnectedServicesClient>(new DetailsClient(resource, type, workspace));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/connected-services/{resource.Name}?scopeRef=%2Finstance");
        ConfigureNavigation(context);
        var rendered = context.Render<ConnectedServiceDetailsPage>(p => p.Add(v => v.Name, resource.Name));
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Find(".resource-header input[role=switch]").HasAttribute("disabled")));
        Assert.HasCount(0, rendered.FindAll("[data-resource-section=\"definition/configuration\"]"));
        Assert.HasCount(0, rendered.FindAll("[data-resource-section=access]"));
        StringAssert.Contains(rendered.Find(".resource-identity-summary").InnerHtml, "data-scope-ref=\"/instance\"");
    }

    [TestMethod]
    public void CreationWizardAllowsIncompleteNavigationRetainsItsFormAndCreatesOnlyAtFinish()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var original = TypeCatalog();
        var type = original with { Definition = original.Definition with { Versions = [original.Definition.Versions[0] with { Inputs = [] }] } };
        var client = new EditorClient(type, scope);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(client);
        context.Services.AddSingleton<IParametersClient>(new ParametersClientStub([]));
        context.Services.AddSingleton<ISecretsClient>(new SecretsClientStub([]));
        var rendered = context.Render<ConnectedServiceEditorPage>();
        var form = rendered.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        Assert.AreEqual("false", rendered.Find("[data-wizard-step=identity]").GetAttribute("data-step-complete"));
        rendered.Find("[data-testid=creation-wizard-next]").Click();
        Assert.AreEqual("step", rendered.Find("[data-wizard-step=configuration]").GetAttribute("aria-current"));
        rendered.Find("[data-wizard-step=review]").Click();
        rendered.Find("[data-testid=connected-service-save]").Click();
        Assert.AreEqual("step", rendered.Find("[data-wizard-step=identity]").GetAttribute("aria-current"));
        Assert.IsNull(client.Created);
        rendered.Find("[data-testid=connected-service-display-name]").Change("Wizard service");
        rendered.Find("[data-testid=connected-service-name]").Change("wizard-service");
        rendered.Find("[data-wizard-step=configuration]").Click();
        rendered.Find("[data-wizard-step=identity]").Click();
        Assert.AreSame(form, rendered.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.AreEqual("Wizard service", rendered.Find("[data-testid=connected-service-display-name]").GetAttribute("value"));
        rendered.Find("[data-wizard-step=review]").Click();
        rendered.Find("[data-testid=connected-service-save]").Click();
        rendered.WaitForAssertion(() => Assert.IsNotNull(client.Created));
        Assert.IsNotNull(client.Created);
        Assert.AreEqual(scope, client.Created.ScopeRef);
        Assert.AreEqual(type.Uid, client.Created.Properties.Type.TypeUid);
        Assert.AreEqual(type.ScopeRef, client.Created.Properties.Type.Type.ScopeRef);
        Assert.AreEqual("1.0.0", client.Created.Properties.Type.Version);
        Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.Contains("/connected-services/wizard-service?", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StandaloneEditRouteUsesTheCanonicalDetailsConfigurationAndExactCoordinates()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new StubClient(null));
        context.Services.AddSingleton<IParametersClient>(new ParametersClientStub([]));
        context.Services.AddSingleton<ISecretsClient>(new SecretsClientStub([]));
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/connected-services/service/edit?scopeRef=%2Finstance&namespace=shared");
        context.Render<ConnectedServiceEditorPage>(p => p.Add(c => c.Name, "service"));
        var uri = new Uri(nav.Uri);
        Assert.AreEqual("/connected-services/service", uri.AbsolutePath);
        Assert.AreEqual("#definition/configuration", uri.Fragment);
        StringAssert.Contains(uri.Query, "namespace=shared");
        StringAssert.Contains(uri.Query, "scopeRef=%2Finstance");
    }

    private static void ConfigureNavigation(BunitContext context)
    {
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<string>("currentLocation", _ => true).SetResult(navigation.Uri.Split('#')[0] + "#overview");
    }
    private static void SelectSection(BunitContext context, string path)
    {
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.Uri.Split('#')[0] + "#" + path);
    }

    private sealed class NavigationScope(ResourceScopeRef scope) : IConsoleNavigationContext
    {
        public string? ScopeRef => scope.Value;
        public string AddToUri(string uri) => uri;
    }

    private static ConnectedServiceTypeResource TypeCatalog() => new()
    {
        Uid = Guid.NewGuid(),
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
        Metadata = new() { Name = "http" },
        ScopeRef = ResourceScopeRef.Instance,
        Definition = new()
        {
            DisplayName = "HTTP",
            PreferredVersion = "1.0.0",
            Versions =
            [
                new("1.0.0", "sha256:http", JsonSerializer.SerializeToElement(new { type = "object" }),
                [
                    new("endpoint", true, ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Standard, "uri"),
                    new("retries", false, ConnectedServiceValueType.WholeNumber, ConnectedServiceValueProtection.Standard),
                    new("credential", true, ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured)
                ], [])
            ]
        }
    };

    private static ParameterResource Parameter(string name, string displayName, ResourceScopeRef scope,
        ParameterValueType type, DescendantUsePolicy? usePolicy = null) => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ParameterResourceKinds.Parameter,
            Metadata = new() { Name = name },
            ScopeRef = scope,
            Definition = new()
            {
                DisplayName = displayName,
                ValueType = type,
                Value = type == ParameterValueType.WholeNumber
                ? JsonSerializer.SerializeToElement(3)
                : type == ParameterValueType.Logical
                    ? JsonSerializer.SerializeToElement(true)
                    : JsonSerializer.SerializeToElement("value"),
                UsePolicy = usePolicy ?? new()
            }
        };

    private static SecretResponse Secret(string name, string displayName, ResourceScopeRef scope, bool configured,
        DescendantUsePolicy? usePolicy = null) =>
        new(new SecretResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Secret,
            Metadata = new() { Name = name },
            ScopeRef = scope,
            Definition = new()
            {
                DisplayName = displayName,
                Vault = new ResourceReference("vault", scope),
                Key = name,
                UsePolicy = usePolicy ?? new()
            }
        }, configured ? "Configured" : "Missing", configured);

    private static ConnectedServiceResource Resource(ResourceScopeRef scope,
        ConnectedServiceTypeResource? type = null) => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedService,
            Metadata = new() { Name = "redis" },
            ScopeRef = scope,
            Definition = new()
            {
                DisplayName = "Redis",
                Type = type is null
                ? new() { Type = new ResourceReference("redis", ResourceScopeRef.Instance, new("azure")), TypeUid = Guid.NewGuid(), Version = "1.0.0", SchemaDigest = "sha256:contract" }
                : new() { Type = new ResourceReference(type.Name, type.ScopeRef, type.Namespace), TypeUid = type.Uid, Version = "1.0.0", SchemaDigest = "sha256:http" },
                Options = JsonSerializer.SerializeToElement(new { })
            },
            ObservedState = new() { Availability = ConnectedServiceAvailability.Ready, ObservedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }
        };

    private sealed class StubClient(ConnectedServiceResource? resource) : IConnectedServicesClient
    {
        public Task<IReadOnlyList<ConnectedServiceTypeResource>> GetTypesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConnectedServiceResource>> GetConnectedServicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ConnectedServiceResource>>(resource is null ? [] : [resource]);
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> GetConnectedServiceAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ConnectedServiceUsagesResponse> GetUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> CreateConnectedServiceAsync(CreateConnectedServiceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> UpdateConnectedServiceAsync(string name, ResourceScopeRef scopeRef, PutConnectedServiceRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> ReconcileAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> DeactivateAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EditorClient(ConnectedServiceTypeResource type, ResourceScopeRef workspace) : IConnectedServicesClient
    {
        public Task<IReadOnlyList<ConnectedServiceTypeResource>> GetTypesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ConnectedServiceTypeResource>>([type]);
        public Task<IReadOnlyList<ConnectedServiceResource>> GetConnectedServicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(workspace, workspace.Kind, "Workspace", true)]);
        public Task<ResourceSnapshot<ConnectedServiceResource>> GetConnectedServiceAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ConnectedServiceUsagesResponse> GetUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public CreateConnectedServiceRequest? Created { get; private set; }
        public Task<ResourceSnapshot<ConnectedServiceResource>> CreateConnectedServiceAsync(CreateConnectedServiceRequest request, CancellationToken cancellationToken)
        {
            Created = request;
            return Task.FromResult(new ResourceSnapshot<ConnectedServiceResource>(Resource(workspace, type) with { Metadata = new() { Name = request.Name }, Definition = request.Properties }, "\"created-etag\""));
        }
        public Task<ResourceSnapshot<ConnectedServiceResource>> UpdateConnectedServiceAsync(string name, ResourceScopeRef scopeRef, PutConnectedServiceRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> ReconcileAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> DeactivateAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class DetailsClient(ConnectedServiceResource resource, ConnectedServiceTypeResource type,
        ResourceScopeRef workspace) : IConnectedServicesClient
    {
        public ConnectedServiceResource Current { get; private set; } = resource;
        public bool FailUpdate { get; set; }
        public int Deactivations { get; private set; }
        private int revision;
        public string? LastEtag { get; private set; }
        public ResourceScopeRef? LastScope { get; private set; }
        public Task<IReadOnlyList<ConnectedServiceTypeResource>> GetTypesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ConnectedServiceTypeResource>>([type]);
        public Task<IReadOnlyList<ConnectedServiceResource>> GetConnectedServicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(workspace, workspace.Kind, "Workspace", true)]);
        public Task<ResourceSnapshot<ConnectedServiceResource>> GetConnectedServiceAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => Task.FromResult(new ResourceSnapshot<ConnectedServiceResource>(Current, revision == 0 ? "\"etag\"" : $"etag-{revision}"));
        public Task<ConnectedServiceUsagesResponse> GetUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => Task.FromResult(new ConnectedServiceUsagesResponse([], 0));
        public Task<ResourceSnapshot<ConnectedServiceResource>> CreateConnectedServiceAsync(CreateConnectedServiceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> UpdateConnectedServiceAsync(string name, ResourceScopeRef scopeRef, PutConnectedServiceRequest request, string etag, CancellationToken cancellationToken)
        {
            LastEtag = etag;
            LastScope = scopeRef;
            if (FailUpdate) throw new AgentstrationApiException("Update failed", "fixture");
            Current = Current with { Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<ConnectedServiceResource>(Current, $"etag-{++revision}"));
        }
        public Task<ResourceSnapshot<ConnectedServiceResource>> ReconcileAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> DeactivateAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken)
        {
            Deactivations++;
            Current = Current with { Definition = Current.Definition with { Enabled = false } };
            return Task.FromResult(new ResourceSnapshot<ConnectedServiceResource>(Current, $"etag-{++revision}"));
        }
    }

    private sealed class ParametersClientStub(IReadOnlyList<ParameterResource> resources) : IParametersClient
    {
        public Task<IReadOnlyList<ParameterResource>> GetParametersAsync(CancellationToken cancellationToken) => Task.FromResult(resources);
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> GetParameterAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> CreateParameterAsync(CreateParameterRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ParameterResource>> UpdateParameterAsync(string name, ResourceScopeRef scopeRef, PutParameterRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteParameterAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ParameterUsagesResponse> GetParameterUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SecretsClientStub(IReadOnlyList<SecretResponse> resources) : ISecretsClient
    {
        public Task<IReadOnlyList<SecretResponse>> GetSecretsAsync(CancellationToken cancellationToken) => Task.FromResult(resources);
        public Task<IReadOnlyList<VaultResponse>> GetVaultsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResponse>> GetVaultAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> CreateVaultAsync(CreateVaultRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<VaultResource>> UpdateVaultAsync(string name, PutVaultRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteVaultAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VaultInitializationResponse> InitializeVaultAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResponse>> GetSecretAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResource>> CreateSecretAsync(CreateSecretRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SecretResource>> UpdateSecretAsync(string name, PutSecretRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetSecretValueAsync(string name, string value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSecretValueAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSecretAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SecretUsagesResponse> GetSecretUsagesAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
