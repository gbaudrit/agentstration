using Agentstration.Models;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Bunit.JSInterop;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ExtensionsConsoleTests
{
    [TestMethod]
    public async Task ExtensionSectionsUseAccessibleTabs()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(configured: false);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();

        rendered.WaitForAssertion(() =>
        {
            var tabs = rendered.FindAll("[role='tab']");
            Assert.HasCount(3, tabs);
            Assert.AreEqual("Synthèse", tabs[0].TextContent.Trim());
            Assert.AreEqual("Extensions", tabs[1].TextContent.Trim());
            Assert.AreEqual("Enrôlements", tabs[2].TextContent.Trim());
            Assert.AreEqual("true", tabs[0].GetAttribute("aria-selected"));
            _ = rendered.Find("#extensions-panel-summary");
            var metrics = rendered.FindAll("#extensions-panel-summary .metric-card-link");
            Assert.HasCount(4, metrics);
            Assert.AreEqual("/extensions?tab=catalog", metrics[0].GetAttribute("href"));
            Assert.AreEqual("/extensions?tab=enrollments", metrics[1].GetAttribute("href"));
            Assert.AreEqual("/extensions?tab=catalog", metrics[2].GetAttribute("href"));
            Assert.AreEqual("/extensions?tab=enrollments", metrics[3].GetAttribute("href"));
            Assert.IsEmpty(rendered.FindAll("#extensions-panel-summary .panel-actions"));
            Assert.AreEqual(0, client.ExtensionCalls, "The inventory already contains the extension projection; loading it separately duplicates live AEP inspections.");
        });

        await rendered.Find("[data-testid='extensions-tab-catalog']").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            _ = rendered.Find("#extensions-panel-catalog");
            Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='extensions-tab-endpoints']"));
            Assert.AreEqual("Enregistrer un point de terminaison", rendered.Find("header button.button-primary").TextContent.Trim());
            StringAssert.Contains(rendered.Find("table").TextContent, "Enrôlement");
        });
    }

    [TestMethod]
    public async Task EnrollmentModeDisabledByConfigurationIsVisibleButLocked()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(
            configured: false,
            new AepEnrollmentSettingsSnapshot(false, true, false, true, null)));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);

        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await rendered.Find("[data-testid='extensions-tab-enrollments']").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            var inputs = rendered.FindAll("section input[type='checkbox']");
            Assert.HasCount(2, inputs);
            Assert.IsTrue(inputs[0].HasAttribute("disabled"));
            Assert.IsFalse(inputs[1].HasAttribute("disabled"));
            StringAssert.Contains(rendered.Markup, "Disabled by application configuration");
        });
    }

    [TestMethod]
    public async Task ActivePairingRefreshesEnrollmentStateAutomatically()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<bool>("agentstrationEnrollment.postAndOpen", _ => true).SetResult(true);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(configured: false, completesPairing: true);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await rendered.Find("[data-testid='extensions-tab-enrollments']").ClickAsync(new());
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "En attente");
            var card = rendered.Find("[data-testid='enrollment-card']");
            _ = card.QuerySelector(".enrollment-status") ?? throw new AssertFailedException("The enrollment status stack is missing.");
            _ = card.QuerySelector(".enrollment-action-layout") ?? throw new AssertFailedException("The enrollment action layout is missing.");
            Assert.AreEqual("Enrôler l’extension", card.QuerySelector("button.button-primary")?.TextContent.Trim());
            Assert.AreEqual("/extensions/llama-cpp-local?namespace=default", card.QuerySelector("a.text-button")?.GetAttribute("href"));
            Assert.IsNotNull(card.QuerySelector("a[href='http://localhost:5260/']"));
            Assert.IsNotNull(card.QuerySelector("a[href='http://localhost:5260/aep/enrollment/pair']"));
        });

        await rendered.Find("[data-testid='enrollment-card'] button.button-primary").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Disponible");
            Assert.IsFalse(rendered.Markup.Contains("123456789", StringComparison.Ordinal));
            Assert.IsGreaterThanOrEqualTo(3, client.EnrollmentCalls);
        }, TimeSpan.FromSeconds(5));
        var handoff = context.JSInterop.Invocations.Single(value =>
            string.Equals(value.Identifier, "agentstrationEnrollment.postAndOpen", StringComparison.Ordinal));
        Assert.AreEqual("123456789", handoff.Arguments[0]?.ToString());
        Assert.AreEqual("http://localhost:5260/aep/enrollment/pair", handoff.Arguments[1]?.ToString());
        Assert.IsFalse(context.JSInterop.Invocations.Any(value =>
            string.Equals(value.Identifier, "agentstrationEnrollment.copyAndOpen", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ConfiguredModelProviderContributionKeepsAnAccessibleAction()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(configured: true));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());

        var rendered = context.Render<Agentstration.Web.Components.Pages.ExtensionDetails>(parameters => parameters
            .Add(value => value.RegistrationName, "llama-cpp-local"));
        var tabs = rendered.WaitForElements("[role='tab']");
        await tabs[3].ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            var action = rendered.Find("table a.button-secondary");
            Assert.AreEqual("Open llama-cpp-local provider", action.TextContent.Trim());
            Assert.AreEqual("/modelproviders/llama-cpp-local?namespace=default", action.GetAttribute("href"));
        });
    }

    [TestMethod]
    public async Task DiscoveredModelProviderContributionOffersConfiguration()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(configured: false));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());

        var rendered = context.Render<Agentstration.Web.Components.Pages.ExtensionDetails>(parameters => parameters
            .Add(value => value.RegistrationName, "llama-cpp-local"));
        var tabs = rendered.WaitForElements("[role='tab']");
        await tabs[3].ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            var action = rendered.Find("table a.button-primary");
            Assert.AreEqual("Configure llama.cpp provider", action.TextContent.Trim());
            StringAssert.StartsWith(action.GetAttribute("href"), "/modelproviders/new?");
        });
    }

    [TestMethod]
    public async Task ExtensionInventoryShowsEnrollmentAndAvailabilitySeparately()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(configured: false));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);

        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await rendered.Find("[data-testid='extensions-tab-catalog']").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            var row = rendered.Find("tbody tr");
            StringAssert.Contains(row.TextContent, "En attente");
            StringAssert.Contains(row.TextContent, "Disponible");
            Assert.AreEqual("/extensions/llama-cpp-local?namespace=default", row.QuerySelector("a")?.GetAttribute("href"));
        });
    }

    [TestMethod]
    public async Task ExtensionDetailsExposeEnrollmentInformationAndLinks()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(configured: false);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);

        var rendered = context.Render<Agentstration.Web.Components.Pages.ExtensionDetails>(parameters => parameters
            .Add(value => value.RegistrationName, "llama-cpp-local"));
        var tabs = rendered.WaitForElements("[role='tab']");
        await tabs[2].ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Détails de l’enrôlement");
            Assert.AreEqual("http://localhost:5260/", rendered.Find("a[href='http://localhost:5260/']").TextContent.Trim());
            Assert.AreEqual("http://localhost:5260/aep/enrollment/pair", rendered.Find("a[href='http://localhost:5260/aep/enrollment/pair']").TextContent.Trim());
            StringAssert.Contains(rendered.Markup, "En attente");
            Assert.IsGreaterThanOrEqualTo(1, client.EnrollmentCalls);
        });
    }

    [TestMethod]
    public async Task ExtensionDetailsConfirmUnenrollmentAndRefreshItsState()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(configured: false, availableEnrollment: true);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);

        var rendered = context.Render<Agentstration.Web.Components.Pages.ExtensionDetails>(parameters => parameters
            .Add(value => value.RegistrationName, "llama-cpp-local"));
        var tabs = rendered.WaitForElements("[role='tab']");
        await tabs[2].ClickAsync(new());
        var unenroll = rendered.WaitForElement("section.resource-section .panel-actions button.button-danger");
        Assert.AreEqual("Désenrôler l’extension", unenroll.TextContent.Trim());

        await unenroll.ClickAsync(new());
        var dialog = rendered.Find("[role='alertdialog']");
        StringAssert.Contains(dialog.TextContent, "L’identifiant actuel sera invalidé");
        await dialog.QuerySelector("button.button-danger")!.ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, client.UnenrollmentCalls);
            StringAssert.Contains(rendered.Markup, "En attente");
        });
    }

    [TestMethod]
    public async Task PairingCodeEnrollmentIsNotStartedFromTheInventoryWithoutAScopeChoice()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(configured: false);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await rendered.Find("[data-testid='extensions-tab-catalog']").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            var enrollmentCell = rendered.Find("tbody .enrollment-cell");
            StringAssert.Contains(enrollmentCell.TextContent, "En attente");
            Assert.IsEmpty(enrollmentCell.QuerySelectorAll("button"));
            Assert.AreEqual(0, client.RotateCalls);
        });
    }

    [TestMethod]
    public async Task AvailableEnrollmentOffersUnenrollmentAfterCredentialRevocation()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(configured: false, availableEnrollment: true);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await rendered.Find("[data-testid='extensions-tab-enrollments']").ClickAsync(new());

        var actions = rendered.WaitForElements("[data-testid='enrollment-card'] .enrollment-management-actions button");
        Assert.HasCount(3, actions);
        Assert.AreEqual("Renouveler l’identifiant", actions[0].TextContent.Trim());
        Assert.AreEqual("Révoquer l’identifiant", actions[1].TextContent.Trim());
        Assert.AreEqual("Désenrôler", actions[2].TextContent.Trim());

        await actions[2].ClickAsync(new());
        var dialog = rendered.Find("[role='alertdialog']");
        StringAssert.Contains(dialog.TextContent, "L’identifiant actuel sera invalidé");
        await dialog.QuerySelector("button.button-danger")!.ClickAsync(new());

        rendered.WaitForAssertion(() => Assert.AreEqual(1, client.UnenrollmentCalls));
    }

    [TestMethod]
    public async Task UnpairedEnrollmentLooksPendingAndShowsTheScopeSelectorAgain()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(configured: false, availableEnrollment: true);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await rendered.Find("[data-testid='extensions-tab-enrollments']").ClickAsync(new());
        var unenroll = rendered.WaitForElements("[data-testid='enrollment-card'] .enrollment-management-actions button")[2];
        await unenroll.ClickAsync(new());
        await rendered.Find("[role='alertdialog'] button.button-danger").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            var card = rendered.Find("[data-testid='enrollment-card']");
            StringAssert.Contains(card.TextContent, "En attente");
            Assert.IsNotNull(card.QuerySelector(".enrollment-scope select"));
            Assert.IsNull(card.QuerySelector(".resource-scope-badge"));
        });
    }

    [TestMethod]
    public async Task EnrollmentSummaryCountsOnlyItemsInTheQueue()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(configured: false, availableEnrollment: true));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await rendered.Find("[data-testid='extensions-tab-enrollments']").ClickAsync(new());
        var unenroll = rendered.WaitForElements("[data-testid='enrollment-card'] .enrollment-management-actions button")[2];
        await unenroll.ClickAsync(new());
        await rendered.Find("[role='alertdialog'] button.button-danger").ClickAsync(new());
        await rendered.Find("[data-testid='extensions-tab-summary']").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            var enrollmentMetric = rendered.FindAll("#extensions-panel-summary .metric-card-link")[1];
            StringAssert.Contains(enrollmentMetric.TextContent, "Enrôlements");
            StringAssert.Contains(enrollmentMetric.TextContent, "1 en cours");
            Assert.AreEqual("1", enrollmentMetric.QuerySelector("strong")?.TextContent.Trim());
        });
    }

    [TestMethod]
    public async Task SourceProviderContributionOffersExplicitConfigurationFromInventory()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(configured: false, source: true));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton<ISourceProvidersClient>(new FakeSourceProvidersClient());
        context.Services.AddSingleton(new NotificationState());
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await rendered.Find("[data-testid='extensions-tab-catalog']").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            var action = rendered.Find(".inventory-contribution-actions a.text-button");
            Assert.AreEqual("Configure Source provider git", action.TextContent.Trim());
            StringAssert.StartsWith(action.GetAttribute("href"), "/sourceproviders/new?");
        });
    }

    private sealed class FakeExtensionsClient(bool configured, AepEnrollmentSettingsSnapshot? settings = null, bool completesPairing = false, bool source = false, bool availableEnrollment = false) : IExtensionsClient
    {
        private readonly Guid enrollmentId = Guid.NewGuid();
        private bool codeRotated;
        private bool unenrolled;
        private int postRotateReads;
        public int EnrollmentCalls { get; private set; }
        public int RotateCalls { get; private set; }
        public int ExtensionCalls { get; private set; }
        public int UnenrollmentCalls { get; private set; }

        public Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken)
        {
            ExtensionCalls++;
            return Task.FromResult<IReadOnlyList<ExtensionResponse>>([Extension()]);
        }

        public Task<IReadOnlyList<ExtensionInventoryItemResponse>> GetExtensionInventoryAsync(CancellationToken cancellationToken)
        {
            var extension = Extension();
            return Task.FromResult<IReadOnlyList<ExtensionInventoryItemResponse>>([new ExtensionInventoryItemResponse(
                "registration:default/llama-cpp-local",
                extension.RegistrationName,
                extension.RegistrationNamespace,
                source ? ResourceScopeRef.Instance : null,
                enrollmentId,
                extension.Extension!.Name,
                extension.Extension.Id,
                extension.Extension.Version,
                extension.Endpoint,
                "pairingCode",
                true,
                extension.Status,
                unenrolled ? AepEnrollmentState.Unpaired : availableEnrollment ? AepEnrollmentState.Available : AepEnrollmentState.Pending,
                DateTimeOffset.UtcNow,
                extension,
                [new ExtensionInventoryConnectionResponse(
                    extension.RegistrationName,
                    extension.RegistrationNamespace,
                    source ? ResourceScopeRef.Instance : null,
                    extension.Extension.Name,
                    extension.Endpoint,
                    extension.DiscoverySource,
                    true,
                    AepEnrollmentMode.PairingCode,
                    extension.Status)])]);
        }

        private ExtensionResponse Extension() => new(
            "llama-cpp-local",
            "default",
            new Uri("http://localhost:5270/"),
            "available",
            new ExtensionIdentityResponse("Agentstration.Extensions.LlamaCpp", "llama.cpp", "1.0.0", "Local provider"),
            [new ExtensionContributionResponse(source ? "source-provider" : "model-provider", source ? "git" : "llama.cpp")],
            [],
            [],
            configured ? [new ExtensionProviderBindingResponse("llama-cpp-local", "default", "llama.cpp")] : [],
            null,
            "configuration");

        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionRegistrationResource>>([]);

        public Task<AepEnrollmentSettingsSnapshot> GetEnrollmentSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings ?? new AepEnrollmentSettingsSnapshot(true, true, true, true, null));

        public Task<IReadOnlyList<AepEnrollmentRequestResource>> GetEnrollmentsAsync(CancellationToken cancellationToken)
        {
            EnrollmentCalls++;
            var state = unenrolled
                ? AepEnrollmentState.Unpaired
                : codeRotated
                ? ++postRotateReads >= 2 && completesPairing ? AepEnrollmentState.Available : AepEnrollmentState.CodeIssued
                : availableEnrollment ? AepEnrollmentState.Available : AepEnrollmentState.Pending;
            return Task.FromResult<IReadOnlyList<AepEnrollmentRequestResource>>([Enrollment(state)]);
        }

        public Task<AepPairingCodeResult> RotateEnrollmentCodeAsync(Guid requestId, CancellationToken cancellationToken)
        {
            RotateCalls++;
            codeRotated = true;
            postRotateReads = 0;
            return Task.FromResult(new AepPairingCodeResult(enrollmentId, "123456789", DateTimeOffset.UtcNow.AddMinutes(1)));
        }

        public Task UnenrollExtensionAsync(Guid requestId, CancellationToken cancellationToken)
        {
            UnenrollmentCalls++;
            unenrolled = true;
            return Task.CompletedTask;
        }

        private AepEnrollmentRequestResource Enrollment(AepEnrollmentState state) => new()
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.AepEnrollmentRequest,
            Metadata = new ResourceMetadata { Name = enrollmentId.ToString("N") },
            Generation = 1,
            Definition = new AepEnrollmentRequestProperties
            {
                InstanceId = enrollmentId,
                TargetScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()),
                TargetTenantId = Guid.NewGuid(),
                ExtensionId = "Agentstration.Extensions.Test",
                ExtensionName = "Test extension",
                ExtensionVersion = "1.0.0",
                Endpoint = new Uri("http://localhost:5260/"),
                PairingUri = new Uri("http://localhost:5260/aep/enrollment/pair"),
                State = state,
                AnnouncedAt = DateTimeOffset.UtcNow,
                CodeExpiresAt = state == AepEnrollmentState.CodeIssued ? DateTimeOffset.UtcNow.AddMinutes(1) : null
            }
        };

        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceSnapshot<ExtensionRegistrationResource>(new ExtensionRegistrationResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.ExtensionRegistration,
                Metadata = new ResourceMetadata { Name = name, Namespace = @namespace },
                Generation = 1,
                Definition = new ExtensionRegistrationProperties
                {
                    DisplayName = "llama.cpp",
                    Endpoint = new Uri("http://localhost:5270/"),
                    Enabled = true,
                    Source = ExtensionRegistrationSource.Configuration
                }
            }, "\"etag\""));
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class WritableContextProvider : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(),
            "Administrator",
            Guid.NewGuid(),
            "dev",
            "Development",
            Guid.NewGuid(),
            "default",
            "Default workspace",
            new HashSet<string>([AuthorizationPermissions.ResourcesWrite], StringComparer.Ordinal),
            []));
    }

    private sealed class FakeModelProfilesClient : IModelProfilesClient
    {
        public Task<IReadOnlyList<ModelProfileSummaryResponse>> GetModelProfilesAsync(string? search, string? provider, string? status, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileResource>> GetModelProfileAsync(string profileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileResource>> CreateModelProfileAsync(CreateModelProfileRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileResource>> UpdateModelProfileAsync(string profileName, PutModelProfileRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteModelProfileAsync(string profileName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProfileUsagesResponse> GetModelProfileUsagesAsync(string profileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProfileResolutionResponse> GetModelProfileResolutionAsync(string profileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileOptionMigrationPreviewResponse>> PreviewOptionMigrationAsync(ResourceNamespace @namespace, string profileName, string targetVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileResource>> ApplyOptionMigrationAsync(ResourceNamespace @namespace, string profileName, string targetVersion, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeSourceProvidersClient : ISourceProvidersClient
    {
        public Task<IReadOnlyList<SourceProviderSummaryResponse>> GetSourceProvidersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceProviderSummaryResponse>>([]);
        public Task<ResourceSnapshot<SourceProviderResource>> GetSourceProviderAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SourceProviderResource>> CreateSourceProviderAsync(CreateSourceProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SourceProviderResource>> UpdateSourceProviderAsync(ResourceNamespace @namespace, string name, PutSourceProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSourceProviderAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SourceProviderStatusResponse> GetStatusAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SourceProviderUsagesResponse> GetUsagesAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
