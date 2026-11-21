using Agentstration.Api.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Flows;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Bunit.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed partial class ExtensionsConsoleTests
{
    [TestMethod]
    [DataRow("en-US", "snapshot")]
    [DataRow("fr-FR", "instantané")]
    public async Task ExtensionCatalogSnapshotHasNoGlobalQueryOrPagingAndRetainsCardsOnFailedRefresh(string locale, string help)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(false);
        var seed = (await client.GetExtensionInventoryAsync(default))[0];
        client.InventoryLoader = _ => Task.FromResult<IReadOnlyList<ExtensionInventoryItemResponse>>(Enumerable.Range(0, 26).Select(i => seed with { Key = $"fixture-{i}", RegistrationName = $"fixture-{i}", DisplayName = $"Fixture {i}" }).ToArray());
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/extensions?tab=catalog");
        var cut = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        cut.WaitForAssertion(() => Assert.HasCount(26, cut.FindAll("[data-testid=extension-inventory-row]")));
        Assert.IsEmpty(cut.FindAll("input[type=search], [data-filter-key], .collection-pager"));
        StringAssert.Contains(cut.Markup, help);
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        client.InventoryLoader = _ => throw new HttpRequestException("Private transport detail");
        await cut.Find("[data-testid=extensions-catalog-refresh]").ClickAsync(new());
        Assert.HasCount(26, cut.FindAll(".collection-card"));
        Assert.IsFalse(cut.Markup.Contains("Private transport detail", StringComparison.Ordinal));
        client.InventoryLoader = _ => Task.FromResult<IReadOnlyList<ExtensionInventoryItemResponse>>([seed]);
        await cut.Find("[data-testid=extensions-catalog] [role=alert] button").ClickAsync(new());
        Assert.HasCount(1, cut.FindAll(".collection-card"));
    }

    [TestMethod]
    public async Task ExtensionCatalogContextNotificationClearsMetadataRejectsOldReadAndDenialRemovesCommands()
    {
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(false);
        var seed = (await client.GetExtensionInventoryAsync(default))[0];
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/extensions?tab=catalog");
        var cut = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("[data-testid=extension-inventory-row]")));
        var old = new TaskCompletionSource<IReadOnlyList<ExtensionInventoryItemResponse>>();
        var replacement = new TaskCompletionSource<IReadOnlyList<ExtensionInventoryItemResponse>>();
        var calls = 0;
        client.InventoryLoader = _ => ++calls == 1 ? old.Task : replacement.Task;
        var refresh = cut.Find("[data-testid=extensions-catalog-refresh]").ClickAsync(new());
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(default));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("[data-testid=extension-inventory-row]")));
        replacement.SetResult([seed with { DisplayName = "Replacement" }]);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Replacement"));
        old.SetResult([seed with { DisplayName = "Obsolete" }]);
        await refresh;
        Assert.IsFalse(cut.Markup.Contains("Obsolete", StringComparison.Ordinal));
        client.InventoryLoader = _ => throw new AgentstrationApiException("Private denial", "fixture", System.Net.HttpStatusCode.Forbidden);
        await cut.Find("[data-testid=extensions-catalog-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("[data-testid=extension-inventory-row], [data-testid=extensions-catalog] header .button-primary"));
    }

    [TestMethod]
    public async Task DirectEnrollmentNavigationWaitsForPermissionsBeforeLoadingCommands()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(configured: false, availableEnrollment: true);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var snapshot = new TaskCompletionSource<ConsoleContextSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ConsoleContextState(new DeferredContextProvider(snapshot.Task));
        context.Services.AddSingleton(state);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/extensions?tab=enrollments");
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        Assert.AreEqual(0, client.EnrollmentCalls);
        snapshot.SetResult(await new WritableContextProvider().GetAsync(default));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("true", rendered.Find("[data-testid=extension-enrollment-open]").GetAttribute("aria-expanded"));
            Assert.HasCount(2, rendered.FindAll(".enrollment-management-actions .collection-primary-actions button"));
            Assert.HasCount(1, rendered.FindAll(".enrollment-management-actions .collection-actions-panel button"));
            Assert.AreEqual(1, client.EnrollmentCalls);
        });
    }

    [TestMethod]
    public async Task ToolCatalogRefreshKeepsAFilteredPageReadableAfterResourcesAreRemoved()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var tools = new BundleToolsClient
        {
            Catalog = Enumerable.Range(0, 26).Select(index => BundleToolsClient.Tool($"catalog.{index:D2}")).ToArray()
        };
        context.Services.AddSingleton<IToolsClient>(tools);
        var rendered = context.Render<Agentstration.Web.Components.Pages.Tools>();
        rendered.WaitForAssertion(() => Assert.HasCount(20, rendered.FindAll("tbody tr")));
        rendered.Find("input[type='search']").Input("catalog");
        await rendered.FindAll(".collection-page-number")[1].ClickAsync(new());
        rendered.WaitForAssertion(() => Assert.HasCount(6, rendered.FindAll("tbody tr")));
        tools.Catalog = tools.Catalog.Take(4).ToArray();
        await rendered.Find("[data-testid=resource-tools-refresh]").ClickAsync(new());
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(4, rendered.FindAll("tbody tr"));
            Assert.AreEqual("catalog", rendered.Find("input[type='search']").GetAttribute("value"));
            Assert.IsEmpty(rendered.FindAll(".collection-page-number"));
        });
    }

    [TestMethod]
    public async Task ExtensionListCombinesMetricsAndEnrollmentWithoutTabs()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
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
            Assert.IsEmpty(rendered.FindAll("[role=tab]"));
            var metrics = rendered.FindAll(".collection-page-metrics .metric-card");
            Assert.HasCount(4, metrics);
            StringAssert.Contains(metrics[0].TextContent, "Extensions chargées");
            Assert.HasCount(1, rendered.FindAll("[data-testid=extensions-catalog] table"));
            Assert.AreEqual(0, client.ExtensionCalls, "The inventory already contains the extension projection; loading it separately duplicates live AEP inspections.");
        });


        rendered.WaitForAssertion(() =>
        {
            _ = rendered.Find("[data-testid=extensions-catalog]");
            Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='extensions-tab-endpoints']"));
            Assert.AreEqual("Enregistrer un point de terminaison", rendered.Find("header button.button-primary").TextContent.Trim());
            StringAssert.Contains(rendered.Find("table").TextContent, "Enrôlement");
        });
    }

    [TestMethod]
    public async Task EnrollmentModeDisabledByConfigurationIsVisibleButLocked()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
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
        await rendered.Find("[data-testid=extension-enrollment-settings]").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            var inputs = rendered.FindAll("dialog input[role=switch]");
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
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
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
        await OpenEnrollmentAsync(rendered);
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "En attente");
            var card = rendered.Find("[data-testid='enrollment-card']");
            _ = card.QuerySelector(".enrollment-details-heading .status-badge") ?? throw new AssertFailedException("The enrollment status stack is missing.");
            _ = card.QuerySelector(".enrollment-action-layout") ?? throw new AssertFailedException("The enrollment action layout is missing.");
            Assert.AreEqual("Enrôler l’extension", card.QuerySelector("button.button-primary")?.TextContent.Trim());
            Assert.IsNull(card.QuerySelector(".enrollment-actions details"), "Enrollment commands must be directly available.");
            StringAssert.Contains(rendered.Find(".collection-identity-versions")!.TextContent, "1.0.0");
            Assert.AreEqual("/extensions/llama-cpp-local?namespace=default&scopeRef=%2Finstance&context=%2Finstance", rendered.Find("a.resource-name-link")?.GetAttribute("href"));
            Assert.IsNotNull(rendered.Find("a[href='http://localhost:5270/']"));
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
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(configured: true));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());

        ConfigureExtensionNavigation(context);
        var rendered = context.Render<Agentstration.Web.Components.Pages.ExtensionDetails>(parameters => parameters
            .Add(value => value.RegistrationName, "llama-cpp-local"));
        await SelectExtensionSectionAsync(rendered, "contributions");

        rendered.WaitForAssertion(() =>
        {
            var action = rendered.Find(".extension-contribution-actions a.text-button");
            Assert.AreEqual("Open llama-cpp-local provider →", action.TextContent.Trim());
            Assert.AreEqual("/modelproviders/llama-cpp-local?namespace=default", action.GetAttribute("href"));
        });
    }

    [TestMethod]
    public async Task DiscoveredModelProviderContributionOffersConfiguration()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(configured: false));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());

        ConfigureExtensionNavigation(context);
        var rendered = context.Render<Agentstration.Web.Components.Pages.ExtensionDetails>(parameters => parameters
            .Add(value => value.RegistrationName, "llama-cpp-local"));
        await SelectExtensionSectionAsync(rendered, "contributions");

        rendered.WaitForAssertion(() =>
        {
            var action = rendered.Find(".extension-contribution-actions a.text-button");
            Assert.AreEqual("Configure llama.cpp provider →", action.TextContent.Trim());
            StringAssert.StartsWith(action.GetAttribute("href"), "/modelproviders/new?");
        });
    }

    [TestMethod]
    public async Task BundleContributionShowsLinkedToolSetProfileAndFlowResources()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new BundleExtensionsClient());
        context.Services.AddSingleton<IToolsClient>(new BundleToolsClient());
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());

        ConfigureExtensionNavigation(context);
        var rendered = context.Render<Agentstration.Web.Components.Pages.ExtensionDetails>(parameters => parameters
            .Add(value => value.RegistrationName, "crawl4ai"));
        await SelectExtensionSectionAsync(rendered, "contributions");

        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(6, rendered.FindAll(".extension-contribution-card"));
            var bundle = rendered.Find("[data-contribution-id='crawl4ai-web']");
            StringAssert.Contains(bundle.TextContent, "3 ressource(s) gouvernée(s) matérialisée(s)");
            Assert.IsNotNull(bundle.QuerySelector("a[href='/tools/sets/crawl4ai-web-acquisition?namespace=default&scopeRef=%2Finstance&context=%2Finstance#overview']"));
            Assert.IsNotNull(bundle.QuerySelector("a[href='/data-source-profiles/crawl4ai-web?namespace=default&scopeRef=%2Finstance&context=%2Finstance#overview']"));
            Assert.IsNotNull(bundle.QuerySelector("a[href='/flows/crawl4ai-web-acquisition?scopeRef=%2Finstance&context=%2Finstance#overview']"));

            var tool = rendered.Find("[data-contribution-id='web.crawl']");
            Assert.IsNotNull(tool.QuerySelector("a[href='/tools/paired-crawl4ai.web.crawl?namespace=default&scopeRef=%2Finstance&context=%2Finstance#overview']"));
        });
    }

    [TestMethod]
    public async Task ExtensionInventoryShowsEnrollmentAndAvailabilitySeparately()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
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
            var row = rendered.Find("tbody tr");
            StringAssert.Contains(row.TextContent, "En attente");
            StringAssert.Contains(row.TextContent, "Disponible");
            Assert.AreEqual("/extensions/llama-cpp-local?namespace=default&scopeRef=%2Finstance&context=%2Finstance", row.QuerySelector("a")?.GetAttribute("href"));
        });
    }

    [TestMethod]
    public async Task ExtensionDetailsExposeEnrollmentInformationAndLinks()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(configured: false);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);

        ConfigureExtensionNavigation(context);
        var rendered = context.Render<Agentstration.Web.Components.Pages.ExtensionDetails>(parameters => parameters
            .Add(value => value.RegistrationName, "llama-cpp-local"));
        await SelectExtensionSectionAsync(rendered, "enrollment");

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
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(configured: false, availableEnrollment: true);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);

        ConfigureExtensionNavigation(context);
        var rendered = context.Render<Agentstration.Web.Components.Pages.ExtensionDetails>(parameters => parameters
            .Add(value => value.RegistrationName, "llama-cpp-local"));
        await SelectExtensionSectionAsync(rendered, "enrollment");
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
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
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
            var enrollmentCell = rendered.Find("tbody .enrollment-cell");
            StringAssert.Contains(enrollmentCell.TextContent, "En attente");
            Assert.HasCount(1, enrollmentCell.QuerySelectorAll("[data-testid=extension-enrollment-shortcut]"));
            StringAssert.Contains(enrollmentCell.TextContent, "Gérer");
            Assert.AreEqual(0, client.RotateCalls);
        });
    }

    [TestMethod]
    public async Task AvailableEnrollmentOffersUnenrollmentAfterCredentialRevocation()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(configured: false, availableEnrollment: true);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await OpenEnrollmentAsync(rendered);

        var actions = rendered.WaitForElements("[data-testid='enrollment-card'] .enrollment-management-actions button");
        Assert.HasCount(3, actions);
        Assert.HasCount(2, rendered.FindAll(".enrollment-management-actions .collection-primary-actions button"));
        Assert.HasCount(1, rendered.FindAll(".enrollment-management-actions .collection-actions-panel button"));
        Assert.AreEqual("Renouveler l’identifiant", actions[0].TextContent.Trim());
        Assert.AreEqual("Désenrôler", actions[1].TextContent.Trim());
        Assert.AreEqual("Révoquer l’identifiant", actions[2].TextContent.Trim());

        await actions[1].ClickAsync(new());
        var dialog = rendered.Find("[role='alertdialog']");
        StringAssert.Contains(dialog.TextContent, "L’identifiant actuel sera invalidé");
        await dialog.QuerySelector("button.button-danger")!.ClickAsync(new());

        rendered.WaitForAssertion(() => Assert.AreEqual(1, client.UnenrollmentCalls));
    }

    [TestMethod]
    public async Task UnpairedEnrollmentLooksPendingAndShowsTheScopeSelectorAgain()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(configured: false, availableEnrollment: true);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await OpenEnrollmentAsync(rendered);
        var unenroll = rendered.WaitForElements("[data-testid='enrollment-card'] .enrollment-management-actions .collection-primary-actions button")[1];
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
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(configured: false, availableEnrollment: true));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var contextState = new ConsoleContextState(new WritableContextProvider());
        await contextState.LoadAsync(default);
        context.Services.AddSingleton(contextState);
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await OpenEnrollmentAsync(rendered);
        var unenroll = rendered.WaitForElements("[data-testid='enrollment-card'] .enrollment-management-actions .collection-primary-actions button")[1];
        await unenroll.ClickAsync(new());
        await rendered.Find("[role='alertdialog'] button.button-danger").ClickAsync(new());

        rendered.WaitForAssertion(() =>
        {
            var enrollmentMetric = rendered.FindAll(".collection-page-metrics .metric-card")[1];
            StringAssert.Contains(enrollmentMetric.TextContent, "Enrôlements");
            StringAssert.Contains(enrollmentMetric.TextContent, "1 en cours");
            Assert.AreEqual("1", enrollmentMetric.QuerySelector("strong")?.TextContent.Trim());
        });
    }

    [TestMethod]
    public async Task SourceProviderContributionOffersExplicitConfigurationFromInventory()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(configured: false, source: true));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton<ISourceProvidersClient>(new FakeSourceProvidersClient());
        context.Services.AddSingleton(new NotificationState());
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();

        rendered.WaitForAssertion(() =>
        {
            var action = rendered.Find(".inventory-contribution-actions a.text-button");
            Assert.AreEqual("Configure Source provider git", action.TextContent.Trim());
            StringAssert.StartsWith(action.GetAttribute("href"), "/sourceproviders/new?");
        });
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public void ToolCatalogLinksReadableExtensionWithoutDisplayingPairingIdentity(string cultureName)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new BundleExtensionsClient());
        context.Services.AddSingleton<IToolsClient>(new BundleToolsClient());
        var rendered = context.Render<Agentstration.Web.Components.Pages.Tools>();

        rendered.WaitForAssertion(() =>
        {
            var owners = rendered.FindAll(".tool-catalog-owner");
            Assert.HasCount(5, owners);
            foreach (var owner in owners)
            {
                var link = owner.QuerySelector("a")!;
                Assert.AreEqual("Crawl4AI Web Acquisition", link.TextContent.Trim());
                Assert.AreEqual("/extensions/crawl4ai?namespace=default&scopeRef=%2Finstance&context=%2Finstance", link.GetAttribute("href"));
                Assert.IsNull(link.QuerySelector("svg"));
                StringAssert.Contains(owner.TextContent, "Agentstration.Extensions.Crawl4AI");
                Assert.IsFalse(owner.TextContent.Contains("paired-", StringComparison.Ordinal));
                Assert.IsNotNull(owner.QuerySelector(".resource-scope-badge"));
            }
            StringAssert.Contains(rendered.Find("a[href*='/tools/paired-crawl4ai.web.crawl']").GetAttribute("href")!, "context=%2Finstance");
            Assert.IsFalse(rendered.Find("[data-testid=resource-tools]").TextContent.Contains('→'));
        });
    }

    [TestMethod]
    [DataRow("en-US", "available", "Available", true)]
    [DataRow("en-US", "unavailable", "Unavailable", true)]
    [DataRow("en-US", "missing", "Not materialized", false)]
    [DataRow("en-US", "conflict", "Conflict", false)]
    [DataRow("fr-FR", "available", "Disponible", true)]
    [DataRow("fr-FR", "unavailable", "Indisponible", true)]
    [DataRow("fr-FR", "missing", "Non matérialisée", false)]
    [DataRow("fr-FR", "conflict", "En conflit", false)]
    public async Task ToolContributionShowsLocalizedStateAndOnlyUnambiguousLinks(
        string cultureName, string state, string label, bool hasToolLink)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new BundleExtensionsClient());
        context.Services.AddSingleton<IToolsClient>(new BundleToolsClient(state));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        ConfigureExtensionNavigation(context);
        var rendered = context.Render<Agentstration.Web.Components.Pages.ExtensionDetails>(parameters =>
            parameters.Add(value => value.RegistrationName, "crawl4ai"));
        await SelectExtensionSectionAsync(rendered, "contributions");
        rendered.WaitForAssertion(() =>
        {
            var card = rendered.Find("[data-contribution-id='web.crawl']");
            StringAssert.Contains(card.TextContent, label);
            Assert.AreEqual(hasToolLink, card.QuerySelector(".extension-materialized-resources a") is not null);
            Assert.AreEqual(state != "conflict", card.QuerySelector("a[href='/tools/providers/crawl4ai?namespace=default&scopeRef=%2Finstance&context=%2Finstance']") is not null);
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ExactScopedToolProviderKeepsManagedContentReadOnlyAndOrdinaryContentEditable(bool managed)
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new BundleToolsClient(managedProvider: managed);
        context.Services.AddSingleton<IToolsClient>(client);
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/tools/providers/crawl4ai?namespace=default&scopeRef=%2Finstance&context=%2Finstance");
        var navigationModule = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        navigationModule.Mode = JSRuntimeMode.Loose;
        var providerUri = context.Services.GetRequiredService<NavigationManager>().Uri;
        foreach (var section in new[] { "overview", "definition/identity" })
        {
            navigationModule.Setup<string>("navigate", invocation => invocation.Arguments[0] is string target
                && target.EndsWith("#" + section, StringComparison.Ordinal)).SetResult(providerUri + "#" + section);
        }
        var rendered = context.Render<Agentstration.Web.Components.Pages.ToolProviderEditor>(parameters =>
            parameters.Add(value => value.Name, "crawl4ai"));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(ResourceScopeRef.Instance, client.RequestedProviderScope);
            Assert.AreEqual(managed, rendered.Find(".resource-header input[role=switch]").HasAttribute("disabled"));
            Assert.IsEmpty(rendered.FindAll(".resource-form"));
            Assert.IsEmpty(rendered.FindAll(".unsaved-changes-bar"));
            Assert.IsNotNull(rendered.Find("a[href='/extensions/crawl4ai?namespace=default&scopeRef=%2Finstance&context=%2Finstance']"));
        });
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(managed, rendered.Find("#tool-provider-resource-form > fieldset").HasAttribute("disabled"));
            var identity = rendered.Find("[data-definition-section='definition/identity']");
            Assert.IsFalse(identity.HasAttribute("hidden"));
            Assert.AreEqual(managed, identity.HasAttribute("disabled"));
            Assert.IsEmpty(rendered.FindAll(".unsaved-changes-bar"));
        });
    }

    [TestMethod]
    [DataRow("en-US", "Conflict", "Technical details")]
    [DataRow("fr-FR", "En conflit", "Détails techniques")]
    public async Task DegradedBundleKeepsItsEvidenceWithoutOfferingResourceNavigation(string cultureName, string label, string technicalDetails)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new BundleExtensionsClient(ExtensionBundleInstallationState.Degraded));
        context.Services.AddSingleton<IToolsClient>(new BundleToolsClient());
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        ConfigureExtensionNavigation(context);
        var rendered = context.Render<Agentstration.Web.Components.Pages.ExtensionDetails>(parameters =>
            parameters.Add(value => value.RegistrationName, "crawl4ai"));
        await SelectExtensionSectionAsync(rendered, "contributions");
        var card = rendered.Find("[data-contribution-id='crawl4ai-web']");
        StringAssert.Contains(card.TextContent, label);
        Assert.IsEmpty(card.QuerySelectorAll("a"));
        await SelectExtensionSectionAsync(rendered, "resources");
        StringAssert.Contains(rendered.Markup, "crawl4ai-web-acquisition");
        Assert.IsEmpty(rendered.FindAll("article.resource-section td a"));
        var evidence = rendered.Find(".extension-bundle-evidence");
        Assert.AreEqual(technicalDetails, evidence.QuerySelector("summary")!.TextContent);
        Assert.IsFalse(evidence.HasAttribute("open"));
        StringAssert.Contains(evidence.TextContent, "Agentstration.Extensions.Crawl4AI");
        StringAssert.Contains(evidence.TextContent, "digest");
    }

    [TestMethod]
    [DataRow("en-US", "Published versions")]
    [DataRow("fr-FR", "Versions publiées")]
    public async Task BundleInventoryKeepsPublicationsInTechnicalDetailsWithoutLinks(string cultureName, string publicationsLabel)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new BundleExtensionsClient());
        context.Services.AddSingleton<IToolsClient>(new BundleToolsClient());
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        ConfigureExtensionNavigation(context);
        var rendered = context.Render<Agentstration.Web.Components.Pages.ExtensionDetails>(parameters =>
            parameters.Add(value => value.RegistrationName, "crawl4ai"));
        await SelectExtensionSectionAsync(rendered, "resources");

        var grid = rendered.Find(".extension-bundle-grid");
        Assert.HasCount(3, grid.QuerySelectorAll("tbody tr"));
        Assert.HasCount(3, grid.QuerySelectorAll("a"));
        Assert.IsFalse(grid.TextContent.Contains("--92521fc3", StringComparison.Ordinal));
        Assert.IsFalse(grid.TextContent.Contains("data-source-profile-revision", StringComparison.Ordinal));
        var evidence = rendered.Find(".extension-bundle-evidence");
        Assert.AreEqual(publicationsLabel, evidence.QuerySelector("h4")!.TextContent);
        var publications = evidence.QuerySelector(".extension-bundle-publications")!;
        Assert.HasCount(3, publications.QuerySelectorAll("li"));
        Assert.IsEmpty(publications.QuerySelectorAll("a"));
        StringAssert.Contains(publications.TextContent, "crawl4ai-web-acquisition--92521fc3");
        StringAssert.Contains(publications.TextContent, "data-source-profile-revision--abcd");
        Assert.HasCount(3, publications.QuerySelectorAll(".resource-owner-icon .scope-letter"));
        Assert.IsTrue(publications.QuerySelectorAll(".resource-owner-icon button").All(button => button.GetAttribute("aria-label") == "Instance"));
        Assert.HasCount(3, publications.QuerySelectorAll("span code").Where(value => value.TextContent == "1.0.0"));
    }

    private sealed class BundleExtensionsClient(ExtensionBundleInstallationState state = ExtensionBundleInstallationState.Installed) : IExtensionsClient
    {
        private static readonly ResourceScopeRef Scope = ResourceScopeRef.Instance;
        private static readonly ExtensionResponse Value = new(
            "crawl4ai",
            "default",
            new Uri("http://localhost:5290/"),
            "available",
            new("Agentstration.Extensions.Crawl4AI", "Crawl4AI Web Acquisition", "1.0.0", "Web acquisition"),
            [
                new("tool", "web.fetch"),
                new("tool", "web.crawl"),
                new("tool", "content.extract"),
                new("tool", "content.read"),
                new("tool", "content.delete"),
                new("data-source-profile-bundle", "crawl4ai-web")
            ],
            [],
            [],
            [],
            null,
            "aep",
            RegistrationScopeRef: Scope,
            BundleInstallations:
            [
                new(
                    "crawl4ai-web",
                    Scope,
                    "Agentstration.Extensions.Crawl4AI",
                    "1.0.0",
                    "crawl4ai-web",
                    "1.0.0",
                    "digest",
                    ExtensionBundleInstallationState.Installed,
                    new Dictionary<string, ResourceReference>(),
                    [
                        new(ToolResourceKinds.ToolSet, "default", "crawl4ai-web-acquisition", Scope, Guid.NewGuid(), 1, "1.0.0", "toolset-hash", false),
                        new(DataSourceResourceKinds.DataSourceProfile, "default", "crawl4ai-web", Scope, Guid.NewGuid(), 1, "1.0.0", "profile-hash", false),
                        new(FlowResourceKinds.Flow, "default", "crawl4ai-web-acquisition", Scope, Guid.NewGuid(), null, "1.0.0", "flow-hash", false),
                        new(ToolResourceKinds.ToolSetVersion, "default", "crawl4ai-web-acquisition--92521fc3", Scope, Guid.NewGuid(), 1, "1.0.0", "published-toolset-hash", true),
                        new("FlowVersion", "default", "crawl4ai-web-acquisition", Scope, null, null, "1.0.0", "published-flow-hash", true),
                        new(DataSourceResourceKinds.DataSourceProfileRevision, "default", "data-source-profile-revision--abcd", Scope, Guid.NewGuid(), 1, "1.0.0", "published-profile-hash", true)
                    ],
                    null)
            ]);

        private ExtensionResponse EffectiveValue => Value with
        {
            BundleInstallations = Value.BundleInstallations!.Select(value => value with { State = state }).ToArray()
        };
        public Task<IReadOnlyList<ExtensionInventoryItemResponse>> GetExtensionInventoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionInventoryItemResponse>>([new(
                "registration:default/crawl4ai", "crawl4ai", "default", Scope, null,
                "Crawl4AI Web Acquisition", "Agentstration.Extensions.Crawl4AI", "1.0.0",
                Value.Endpoint, "aep", true, "available", null, null, EffectiveValue, [])]);

        public Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionResponse>>([EffectiveValue]);

        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionRegistrationResource>>([]);

        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
            GetRegistrationAsync(@namespace, name, cancellationToken);

        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceSnapshot<ExtensionRegistrationResource>(new ExtensionRegistrationResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ExtensionKinds.ExtensionRegistration,
                Metadata = new ResourceMetadata { Name = name, Namespace = @namespace },
                ScopeRef = Scope,
                Definition = new ExtensionRegistrationProperties
                {
                    DisplayName = "Crawl4AI Web Acquisition",
                    Endpoint = Value.Endpoint,
                    Source = ExtensionRegistrationSource.Manual
                }
            }, "\"etag-1\""));

        public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class BundleToolsClient(string state = "available", bool managedProvider = true) : IToolsClient
    {
        private static readonly IReadOnlyList<ToolResource> Tools =
        [
            Tool("web.fetch"), Tool("web.crawl"), Tool("content.extract"), Tool("content.read"), Tool("content.delete")
        ];

        public IReadOnlyList<ToolResource>? Catalog { get; set; }
        private IReadOnlyList<ToolResource> Values => Catalog ?? (state switch
        {
            "missing" => Tools.Where(value => value.Definition.ExternalId != "web.crawl").ToArray(),
            "conflict" => [.. Tools, Tool("web.crawl") with { Metadata = new ResourceMetadata { Name = "duplicate-crawl" } }],
            "unavailable" => Tools.Select(value => value with
            {
                Definition = value.Definition with
                { Discovery = value.Definition.Discovery! with { Available = false } }
            }).ToArray(),
            _ => Tools
        });
        public Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default) => Task.FromResult(Values);
        public Task<IReadOnlyList<ToolResource>> GetToolsAsync(ResourceScopeRef contextScope, string? provider = null, CancellationToken cancellationToken = default) => Task.FromResult(Values);
        public ResourceScopeRef? RequestedProviderScope { get; private set; }
        private ToolProviderResource Provider() => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            Metadata = new ResourceMetadata { Name = "crawl4ai" },
            ScopeRef = ResourceScopeRef.Instance,
            Governance = managedProvider ? ResourceGovernance.Extension("registration:crawl4ai", "Crawl4AI", "1.0.0") : ResourceGovernance.User,
            Definition = new()
            {
                DisplayName = "Crawl4AI provider",
                ProviderType = ToolProviderType.Aep,
                Aep = new() { Extension = new("crawl4ai", ResourceScopeRef.Instance, ResourceNamespace.Default) }
            }
        };
        public Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ToolProviderResource>>([Provider()]);
        public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceSnapshot<ToolProviderResource>(Provider(), "\"provider-etag\""));
        public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
        {
            RequestedProviderScope = scopeRef;
            return GetProviderAsync(name, cancellationToken);
        }
        public Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolConnectionTestResponse> TestAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolDiscoveryDiffResponse> RefreshAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public int ExactToolReads { get; private set; }
        public ScopedResourceAddress? RequestedTool { get; private set; }
        public Task<ResourceSnapshot<ToolResource>> GetToolAsync(ResourceScopeRef scope, ResourceNamespace ns, string name, CancellationToken cancellationToken)
        {
            ExactToolReads++; RequestedTool = ScopedResourceAddress.Create(scope, ns, ToolResourceKinds.Tool, name);
            return Task.FromResult(new ResourceSnapshot<ToolResource>(Values.Single(value => value.ScopeRef == scope && value.Namespace == ns && value.Name == name), "\"tool-etag\""));
        }
        public Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken) => throw new NotSupportedException();

        public static ToolResource Tool(string externalId) => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.Tool,
            Metadata = new ResourceMetadata { Name = $"paired-crawl4ai.{externalId}" },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new ToolResourceProperties
            {
                DisplayName = externalId,
                ExternalId = externalId,
                Provider = new ResourceReference("crawl4ai", ResourceScopeRef.Instance, ResourceNamespace.Default),
                Discovery = new() { Available = true, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch }
            }
        };
    }

    [TestMethod]
    public async Task EnrollmentDetailsFollowTheRowAcrossViewsThroughTheSharedActionMenu()
    {
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(false));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        var shortcut = rendered.WaitForElement("[data-testid=extension-enrollment-shortcut]");
        Assert.IsNull(shortcut.Closest("details"), "Enrollment must remain directly accessible outside the action menu.");
        await shortcut.ClickAsync(new());
        Assert.HasCount(1, rendered.FindAll(".collection-expanded [data-testid=enrollment-card]"));
        Assert.HasCount(1, rendered.FindAll(".collection-actions-menu"));
        Assert.HasCount(1, rendered.FindAll(".collection-actions-panel a[href$='#overview']"));
        Assert.IsEmpty(rendered.FindAll("[data-testid=extension-preview]"), "A missing owner must not be replaced by an inferred scope for Preview.");
        var contribution = rendered.Find("a.extension-contributions-link");
        StringAssert.Contains(contribution.TextContent, "1 contribution");
        StringAssert.Contains(contribution.GetAttribute("href"), "scopeRef=%2Finstance&context=%2Finstance#contributions");
        Assert.AreEqual("model-provider:llama.cpp", contribution.GetAttribute("title"));
        await rendered.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.HasCount(1, rendered.FindAll(".collection-card"));
        Assert.HasCount(1, rendered.FindAll(".collection-expanded [data-testid=enrollment-card]"));
        Assert.AreEqual("true", rendered.Find("[data-testid=extension-enrollment-shortcut]").GetAttribute("aria-expanded"));
        await rendered.Find("[data-testid=extension-enrollment-open]").ClickAsync(new());
        Assert.IsEmpty(rendered.FindAll("[data-testid=enrollment-card]"));
        Assert.AreEqual("false", rendered.Find("[data-testid=extension-enrollment-open]").GetAttribute("aria-expanded"));
        Assert.AreEqual("false", rendered.Find("[data-testid=extension-enrollment-shortcut]").GetAttribute("aria-expanded"));
        await rendered.Find("[data-testid=extension-enrollment-shortcut]").ClickAsync(new());
        Assert.HasCount(1, rendered.FindAll(".collection-expanded [data-testid=enrollment-card]"));
    }

    [TestMethod]
    public async Task EnrollmentCommandsRequireWritePermissionAndAnExactInstanceMatch()
    {
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(false);
        var seed = (await client.GetExtensionInventoryAsync(default))[0];
        client.InventoryLoader = _ => Task.FromResult<IReadOnlyList<ExtensionInventoryItemResponse>>([seed with { EnrollmentInstanceId = Guid.NewGuid() }]);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid=extension-inventory-row]")));
        Assert.IsEmpty(rendered.FindAll("[data-testid=extension-enrollment-open], [data-testid=extension-enrollment-shortcut], [data-testid=enrollment-card]"));
        Assert.IsGreaterThanOrEqualTo(1, client.EnrollmentCalls);
    }

    [TestMethod]
    public void ReadOnlyInventoryDoesNotReadOrExposeEnrollmentAdministration()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(false);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid=extension-inventory-row]")));
        Assert.AreEqual(0, client.EnrollmentCalls);
        Assert.HasCount(2, rendered.FindAll(".collection-page-metrics .metric-card"));
        Assert.IsEmpty(rendered.FindAll("[data-testid=extension-enrollment-open], [data-testid=extension-enrollment-shortcut], [data-testid=extension-enrollment-settings], header button.button-primary"));
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public void ContributionsLinkOpensTheRequestedSection(string locale)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(configured: true));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/extensions/llama-cpp-local?namespace=default#contributions");
        ConfigureExtensionNavigation(context);
        var rendered = context.Render<Agentstration.Web.Components.Pages.ExtensionDetails>(parameters => parameters.Add(value => value.RegistrationName, "llama-cpp-local"));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("page", rendered.Find("[data-resource-section=contributions]").GetAttribute("aria-current"));
            var contribution = rendered.Find("[data-testid=extension-contribution-list] [data-contribution-id='llama.cpp']");
            Assert.IsNotNull(contribution.QuerySelector("a[href='/modelproviders/llama-cpp-local?namespace=default']"));
        });
    }

    [TestMethod]
    public async Task EnrollmentSwitchesKeepSavedValuesOnCancelAndSubmitTheExistingSettingsEtag()
    {
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(false, new(true, true, true, true, "settings-etag"));
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await rendered.WaitForElement("[data-testid=extension-enrollment-settings]").ClickAsync(new());
        var switches = rendered.FindAll("dialog input[role=switch]");
        switches[0].Change(false);
        Assert.IsTrue(switches[1].HasAttribute("checked"));
        StringAssert.Contains(rendered.FindAll(".metric-card")[3].TextContent, "2/2");
        await rendered.Find("dialog .form-actions .button-secondary").ClickAsync(new());
        Assert.IsNull(client.UpdatedSettings);
        await rendered.Find("[data-testid=extension-enrollment-settings]").ClickAsync(new());
        Assert.IsTrue(rendered.Find("dialog input[role=switch]").HasAttribute("checked"));
        rendered.Find("dialog input[role=switch]").Change(false);
        await rendered.Find("dialog .form-actions .button-primary").ClickAsync(new());
        Assert.AreEqual((false, true, "settings-etag"), client.UpdatedSettings!.Value);
        Assert.IsEmpty(rendered.FindAll("dialog"));
        StringAssert.Contains(rendered.FindAll(".metric-card")[3].TextContent, "1/2");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DirectEnrollmentLinkOpensOnlyTheRequestedInstance(bool matches)
    {
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(false);
        var seed = (await client.GetExtensionInventoryAsync(default))[0];
        var requested = matches ? seed.EnrollmentInstanceId : Guid.NewGuid();
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/extensions?tab=enrollments&enrollment={requested:D}");
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(matches ? 1 : 0, rendered.FindAll("[data-testid=enrollment-card]"));
            Assert.AreEqual(matches ? "true" : "false", rendered.Find("[data-testid=extension-enrollment-open]").GetAttribute("aria-expanded"));
        });
    }

    [TestMethod]
    public async Task PreviewQuickActionClosesTheModalAndOpensTheExactInlineEnrollment()
    {
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(false, source: true);
        var item = (await client.GetExtensionInventoryAsync(default))[0];
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        await rendered.WaitForElement("[data-testid=extension-preview]").ClickAsync(new());
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindComponents<ExtensionResourceSummary>()));
        Assert.Contains("#preview/ExtensionRegistration/%2Finstance/default/llama-cpp-local", context.Services.GetRequiredService<NavigationManager>().Uri);
        Assert.IsEmpty(rendered.FindAll("[data-testid=enrollment-card], [data-testid=extension-preview-edit]"));
        await rendered.Find("[data-testid=extension-preview-enrollment]").ClickAsync(new());
        Assert.IsEmpty(rendered.FindComponents<ExtensionResourcePreview>());
        Assert.HasCount(1, rendered.FindAll($"#extension-enrollment-{item.EnrollmentInstanceId:N}"));
        Assert.AreEqual("true", rendered.Find("[data-testid=extension-enrollment-open]").GetAttribute("aria-expanded"));
        Assert.DoesNotContain("#preview/", context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [TestMethod]
    public async Task UnregisteredEnrollmentKeepsOpenAndEnrollmentWithoutInventingARegistrationPreview()
    {
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeExtensionsClient(false);
        var item = (await client.GetExtensionInventoryAsync(default))[0];
        client.InventoryLoader = _ => Task.FromResult<IReadOnlyList<ExtensionInventoryItemResponse>>([item with { RegistrationName = null, Extension = null }]);
        context.Services.AddSingleton<IExtensionsClient>(client);
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        context.Services.AddSingleton(new NotificationState());
        var rendered = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid=extension-enrollment-open]")));
        Assert.IsEmpty(rendered.FindAll("[data-testid=extension-preview], a[href$='#definition/configuration']"));
        Assert.HasCount(1, rendered.FindAll($".collection-actions-panel a[href='/extensions/enrollment/{item.EnrollmentInstanceId:D}#overview']"));
        await OpenEnrollmentAsync(rendered);
        Assert.HasCount(1, rendered.FindAll("[data-testid=enrollment-card]"));
        Assert.AreEqual(0, client.RotateCalls, "Opening a menu or an enrollment editor is not an enrollment command.");
    }

    private static async Task OpenEnrollmentAsync(IRenderedComponent<Agentstration.Web.Components.Pages.Extensions> rendered)
    {
        var toggle = rendered.WaitForElement("[data-testid=extension-enrollment-open]");
        if (toggle.GetAttribute("aria-expanded") != "true") await toggle.ClickAsync(new());
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
        public (bool PairingCodeEnabled, bool SharedKeyFileEnabled, string? ETag)? UpdatedSettings { get; private set; }

        public Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken)
        {
            ExtensionCalls++;
            return Task.FromResult<IReadOnlyList<ExtensionResponse>>([Extension()]);
        }

        public Func<CancellationToken, Task<IReadOnlyList<ExtensionInventoryItemResponse>>>? InventoryLoader { get; set; }
        public Task<IReadOnlyList<ExtensionInventoryItemResponse>> GetExtensionInventoryAsync(CancellationToken cancellationToken)
        {
            if (InventoryLoader is not null) return InventoryLoader(cancellationToken);
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

        public Task<AepEnrollmentSettingsSnapshot> UpdateEnrollmentSettingsAsync(bool pairingCodeEnabled, bool sharedKeyFileEnabled, string? etag, CancellationToken cancellationToken)
        {
            UpdatedSettings = (pairingCodeEnabled, sharedKeyFileEnabled, etag);
            return Task.FromResult(new AepEnrollmentSettingsSnapshot(pairingCodeEnabled, sharedKeyFileEnabled, true, true, "updated-settings-etag"));
        }

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
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ExtensionKinds.AepEnrollmentRequest,
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

        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
            GetRegistrationAsync(@namespace, name, cancellationToken);

        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceSnapshot<ExtensionRegistrationResource>(new ExtensionRegistrationResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ExtensionKinds.ExtensionRegistration,
                Metadata = new ResourceMetadata { Name = name, Namespace = @namespace },
                Generation = 1,
                ScopeRef = source ? ResourceScopeRef.Instance : null,
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

    private sealed class DeferredContextProvider(Task<ConsoleContextSnapshot> snapshot) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => snapshot.WaitAsync(cancellationToken);
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
            new HashSet<string>([AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite], StringComparer.Ordinal),
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
