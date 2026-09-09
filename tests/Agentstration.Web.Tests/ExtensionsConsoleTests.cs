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
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(configured: false));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();

        rendered.WaitForAssertion(() =>
        {
            var tabs = rendered.FindAll("[role='tab']");
            Assert.HasCount(4, tabs);
            Assert.AreEqual("Synthèse", tabs[0].TextContent.Trim());
            Assert.AreEqual("Extensions", tabs[1].TextContent.Trim());
            Assert.AreEqual("Enrôlements", tabs[2].TextContent.Trim());
            Assert.AreEqual("Points de terminaison", tabs[3].TextContent.Trim());
            Assert.AreEqual("true", tabs[0].GetAttribute("aria-selected"));
            _ = rendered.Find("#extensions-panel-summary");
        });

        await rendered.Find("[data-testid='extensions-tab-endpoints']").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            _ = rendered.Find("#extensions-panel-endpoints");
            Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("#extensions-panel-catalog"));
            Assert.AreEqual("Enregistrer un point de terminaison", rendered.Find("header button.button-primary").TextContent.Trim());
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
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "En attente"));

        await rendered.Find("table button.button-primary").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Disponible");
            Assert.IsGreaterThanOrEqualTo(3, client.EnrollmentCalls);
        }, TimeSpan.FromSeconds(5));
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

        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await rendered.Find("[data-testid='extensions-tab-catalog']").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            var action = rendered.Find(".panel-actions a.button-secondary");
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

        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await rendered.Find("[data-testid='extensions-tab-catalog']").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            var action = rendered.Find(".panel-actions a.button-primary");
            Assert.AreEqual("Configure llama.cpp provider", action.TextContent.Trim());
            StringAssert.StartsWith(action.GetAttribute("href"), "/modelproviders/new?");
        });
    }

    [TestMethod]
    public async Task DiscoverButtonInvokesDiscoveryAndShowsResult()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(configured: false);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();

        rendered.WaitForAssertion(() => Assert.AreEqual("Discover extensions", rendered.Find("button.button-secondary").TextContent.Trim()));
        await rendered.Find("button.button-secondary").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, client.DiscoveryCalls);
            StringAssert.Contains(rendered.Find(".inline-info").TextContent, "1 source(s)");
        });
    }

    private sealed class FakeExtensionsClient(bool configured, AepEnrollmentSettingsSnapshot? settings = null, bool completesPairing = false) : IExtensionsClient
    {
        private readonly Guid enrollmentId = Guid.NewGuid();
        private bool codeRotated;
        private int postRotateReads;
        public int DiscoveryCalls { get; private set; }
        public int EnrollmentCalls { get; private set; }

        public Task<ExtensionDiscoveryResponse> DiscoverAsync(CancellationToken cancellationToken)
        {
            DiscoveryCalls++;
            return Task.FromResult(new ExtensionDiscoveryResponse(1, 1, 0, 0));
        }

        public Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionResponse>>([
                new(
                    "llama-cpp-local",
                    "default",
                    new Uri("http://localhost:5270/"),
                    "available",
                    new ExtensionIdentityResponse("Agentstration.Extensions.LlamaCpp", "llama.cpp", "1.0.0", "Local provider"),
                    [new ExtensionContributionResponse("model-provider", "llama.cpp")],
                    [],
                    [],
                    configured ? [new ExtensionProviderBindingResponse("llama-cpp-local", "default", "llama.cpp")] : [],
                    null,
                    "configuration")
            ]);

        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionRegistrationResource>>([]);

        public Task<AepEnrollmentSettingsSnapshot> GetEnrollmentSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings ?? new AepEnrollmentSettingsSnapshot(true, true, true, true, null));

        public Task<IReadOnlyList<AepEnrollmentRequestResource>> GetEnrollmentsAsync(CancellationToken cancellationToken)
        {
            EnrollmentCalls++;
            var state = codeRotated
                ? ++postRotateReads >= 2 && completesPairing ? AepEnrollmentState.Available : AepEnrollmentState.CodeIssued
                : AepEnrollmentState.Pending;
            return Task.FromResult<IReadOnlyList<AepEnrollmentRequestResource>>([Enrollment(state)]);
        }

        public Task<AepPairingCodeResult> RotateEnrollmentCodeAsync(Guid requestId, CancellationToken cancellationToken)
        {
            codeRotated = true;
            postRotateReads = 0;
            return Task.FromResult(new AepPairingCodeResult(enrollmentId, "123456789", DateTimeOffset.UtcNow.AddMinutes(1)));
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
                TenantId = Guid.NewGuid(),
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

        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
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
}
