using System.Net;
using System.Security.Claims;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Bootstrap;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class BootstrapStartupProgressTests
{
    [TestMethod]
    [DataRow("en-US", "Worker communications", "Built-in resources", "Waiting for a compatible Worker")]
    [DataRow("fr-FR", "Communication avec les Workers", "Ressources builtin", "Attente d’un Worker compatible")]
    public void InitialDiscoveryShowsLocalizedPreparationAndTheExactWorkerWait(string culture, string workers, string builtins, string waiting)
    {
        using var cultures = new TestCultureScope(culture);
        var api = new StartupClient();
        api.Snapshot = api.Snapshot with
        {
            Steps = [new("workers", "succeeded"), new("builtins", "succeeded"), new("resources", "waiting")],
            ActiveStepId = "resources",
            Activity = new("ConnectedServiceOperationDiscovery", "discover", WaitReason: "waiting_worker", ResourceId: "discovery",
                FlowRunId: "flowrun-discovery", FlowRunContext: "/instance", StageStartedAt: DateTimeOffset.UnixEpoch,
                RuntimeCapability: "microsoft-agent-framework", Deadline: DateTimeOffset.UnixEpoch.AddMinutes(10)),
            Families = [new("resources", "ConnectedServiceOperationDiscovery", "waiting", 1, 0, 0, 0, 0, 0, 0)]
        };
        api.ResourceRows = [new("discovery", "resources", "fixture", "discovery.yaml", "ConnectedServiceOperationDiscovery", "discover", "default", "/instance", "waiting", [])];
        using var context = Context(api);
        foreach (var step in new[] { ("workers", workers), ("builtins", builtins) })
        {
            var panel = context.Render<BootstrapStartupStepPanel>(p => p.Add(x => x.Snapshot, api.Snapshot).Add(x => x.StepId, step.Item1));
            Assert.AreEqual(step.Item2, panel.Find("h2").TextContent);
        }
        var cut = context.Render<BootstrapStartupResourceList>(p => p.Add(x => x.Snapshot, api.Snapshot).Add(x => x.StepId, "resources"));
        var details = cut.Find("[data-testid='startup-activity-details']");
        StringAssert.Contains(details.TextContent, waiting);
        StringAssert.Contains(details.TextContent, "microsoft-agent-framework");
        Assert.AreEqual("/flow-runs/flowrun-discovery?context=%2Finstance", details.QuerySelector("a")!.GetAttribute("href"));
    }
    [TestMethod]
    [DataRow("en-US", "This resource was not applied because Bootstrap stopped", "initial Bootstrap", "System > Bootstrap profiles")]
    [DataRow("fr-FR", "le bootstrap s’est arrêté sur l’erreur", "bootstrap initial", "Système > Profils Bootstrap")]
    public void BlockedResourcesShowTheCausalFailureWithoutBlamingTheirOwnConfiguration(string culture, string explanation, string cause, string recovery)
    {
        using var cultures = new TestCultureScope(culture);
        var api = new StartupClient();
        api.Snapshot = api.Snapshot with
        {
            State = "failed",
            Failure = new("configuration", "bootstrap_discovery_requires_manual_application", "shared-log-reference"),
            Activity = new("ConnectedServiceOperationDiscovery", "nantes-operations", ResourceId: "discovery", GroupId: "discovery"),
            Families = [new("resources", "ModelProvider", "blocked", 1, 0, 0, 0, 0, 0, 0) { GroupId = "models", Profile = "ollama-instance" },
                new("resources", "ConnectedServiceOperationDiscovery", "failed", 1, 1, 0, 0, 1, 0, 0) { GroupId = "discovery", Profile = "nantes-alertes" }]
        };
        api.ResourceRows = [new("provider", "resources", "ollama-instance", "20-model-provider.yaml", "ModelProvider", "bootstrap-ollama", "default", "/instance", "blocked", []) { GroupId = "models" }];
        using var context = Context(api);
        var cut = context.Render<BootstrapStartupResourceList>(p => p.Add(x => x.Snapshot, api.Snapshot)
            .Add(x => x.StepId, "resources").Add(x => x.Kind, "ModelProvider"));
        var blocked = cut.Find("[data-testid='startup-resource-blocker']").TextContent;
        foreach (var expected in new[] { explanation, cause, recovery, "bootstrap_discovery_requires_manual_application", "shared-log-reference", "nantes-alertes/ConnectedServiceOperationDiscovery/nantes-operations" })
            StringAssert.Contains(blocked, expected);
        Assert.IsNull(api.ResourceRows[0].Failure, "A blocked consumer must not be recorded as a failed resource.");
        Assert.DoesNotContain(culture == "fr-FR" ? "champs requis" : "required fields", blocked, StringComparison.Ordinal);
        Assert.DoesNotContain(culture == "fr-FR" ? "Cette ressource n’a pas encore été appliquée." : "This resource has not been applied yet.", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("en-US", "initial Bootstrap", "Operation discovery")]
    [DataRow("fr-FR", "bootstrap initial", "Découverte des opérations")]
    public void TheFailedInstructionUsesTheSharedStartupDiagnosticWhenItHasNoSeparateFailure(string culture, string cause, string family)
    {
        using var cultures = new TestCultureScope(culture);
        var api = new StartupClient();
        api.Snapshot = api.Snapshot with
        {
            State = "failed",
            Failure = new("configuration", "bootstrap_discovery_requires_manual_application", "startup-reference"),
            Activity = new("ConnectedServiceOperationDiscovery", "nantes-operations", ResourceId: "discovery"),
            Families = [new("resources", "ConnectedServiceOperationDiscovery", "failed", 1, 1, 0, 0, 1, 0, 0)]
        };
        api.ResourceRows = [new("discovery", "resources", "nantes-alertes", "60-discovery.yaml", "ConnectedServiceOperationDiscovery", "nantes-operations", "default", "/instance", "failed", [new("resource_validation", "failed", DateTimeOffset.UnixEpoch)])];
        using var context = Context(api);
        var cut = context.Render<BootstrapStartupStepPanel>(p => p.Add(x => x.Snapshot, api.Snapshot)
            .Add(x => x.StepId, "resources").Add(x => x.Kind, "ConnectedServiceOperationDiscovery"));
        Assert.AreEqual(family, cut.Find(".startup-step-heading h2").TextContent);
        var failure = cut.Find("[data-testid='bootstrap-failure']").TextContent;
        StringAssert.Contains(failure, cause);
        StringAssert.Contains(failure, "startup-reference");
        Assert.AreEqual(0, cut.FindAll("[data-testid='startup-resource-blocker']").Count);
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public void AWaitingContributionShowsItsOwnDependencyEvenWhenAnotherRowIsActive(string culture)
    {
        using var cultures = new TestCultureScope(culture);
        var api = new StartupClient();
        api.ResourceRows = [new("postgres", "extensions", "nantes-alertes", "contribution:nantes-alertes/30-postgresql.yaml", "ExtensionContribution",
            "postgresql", "default", "/instance", "waiting", [new("contribution_availability", "waiting", DateTimeOffset.UnixEpoch)])
        {
            WaitReason = "extension_unavailable",
            Dependency = new("agentstration.extensions.postgresql", "ConnectedServiceType", "postgresql", "1.0.0", null, "ConnectedService", "nantes-alertes-postgresql")
        }];
        using var context = Context(api);
        var cut = context.Render<BootstrapStartupResourceList>(p => p.Add(x => x.Snapshot, api.Snapshot).Add(x => x.StepId, "extensions"));
        cut.WaitForAssertion(() =>
        {
            var details = cut.Find("[data-testid='bootstrap-dependency-details']").TextContent;
            foreach (var value in new[] { "agentstration.extensions.postgresql", "ConnectedServiceType", "1.0.0", "ConnectedService/nantes-alertes-postgresql" })
                StringAssert.Contains(details, value);
            Assert.AreEqual(1, cut.FindAll("[data-testid='bootstrap-source-viewer']").Count);
        });
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public void TheFirstBlockingContributionOpensWithItsOwnDescriptionAndNoRepeatedPanel(string culture)
    {
        using var cultures = new TestCultureScope(culture);
        var api = ContributionClient();
        using var context = Context(api);
        var cut = context.Render<BootstrapStartupStepPanel>(p => p.Add(x => x.Snapshot, api.Snapshot)
            .Add(x => x.StepId, "extensions").Add(x => x.Kind, "ExtensionContribution").Add(x => x.FollowCurrent, true));
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("true", cut.Find("[data-progress-resource='postgres'] .collection-actions button").GetAttribute("aria-expanded"));
            Assert.AreEqual("false", cut.Find("[data-progress-resource='openapi'] .collection-actions button").GetAttribute("aria-expanded"));
            var details = cut.Find("[data-testid='startup-resource-operations']");
            StringAssert.Contains(details.TextContent, "ConnectedService/nantes-alertes-postgresql");
            Assert.DoesNotContain("nantes-alertes-openapi", details.TextContent, StringComparison.Ordinal);
            Assert.AreEqual(1, details.QuerySelectorAll("[data-testid='bootstrap-dependency-details']").Length);
            Assert.AreEqual(0, cut.FindAll("[data-testid='startup-activity-details']").Count);
            Assert.AreEqual(0, cut.FindAll(".startup-step-content > .startup-resource-facts").Count);
        });
    }

    [TestMethod]
    public void PollingPreservesManualExpansionAndClosingUntilFollowingIsResumed()
    {
        var api = ContributionClient();
        using var context = Context(api);
        var following = true;
        var cut = context.Render<BootstrapStartupResourceList>(p => p.Add(x => x.Snapshot, api.Snapshot)
            .Add(x => x.StepId, "extensions").Add(x => x.FollowCurrent, following)
            .Add(x => x.FollowCurrentChanged, value => following = value));
        cut.Find("[data-progress-resource='jsonb'] .collection-actions button").Click();
        Assert.IsFalse(following);
        api.Snapshot = api.Snapshot with { Revision = 2 };
        cut.Render(p => p.Add(x => x.Snapshot, api.Snapshot).Add(x => x.FollowCurrent, following));
        StringAssert.Contains(cut.Find("[data-testid='startup-resource-operations']").TextContent, "Storage/nantes-alertes-jsonb");
        cut.Find("[data-progress-resource='jsonb'] .collection-actions button").Click();
        api.Snapshot = api.Snapshot with { Revision = 3 };
        cut.Render(p => p.Add(x => x.Snapshot, api.Snapshot));
        Assert.AreEqual(0, cut.FindAll("[data-testid='startup-resource-operations']").Count);
        cut.Render(p => p.Add(x => x.FollowCurrent, true));
        StringAssert.Contains(cut.Find("[data-testid='startup-resource-operations']").TextContent, "ConnectedService/nantes-alertes-postgresql");
        api.ResourceRows = api.ResourceRows.Select(row => row.Id == "postgres" ? row with { State = "succeeded", WaitReason = null } : row).ToArray();
        api.Snapshot = api.Snapshot with { Revision = 4 };
        cut.Render(p => p.Add(x => x.Snapshot, api.Snapshot));
        StringAssert.Contains(cut.Find("[data-testid='startup-resource-operations']").TextContent, "Storage/nantes-alertes-jsonb");
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public void AModelWaitAndItsCorrectionAreShownOnlyInsideTheExpandedModelRow(string culture)
    {
        using var cultures = new TestCultureScope(culture);
        var api = new StartupClient();
        api.Snapshot = api.Snapshot with
        {
            Activity = api.Snapshot.Activity! with { ResourceId = "model", CanChangeModel = true },
            Families = [new("resources", "ModelProfile", "waiting", 1, 0, 0, 0, 0, 0, 1)]
        };
        api.ResourceRows = [new("model", "resources", "models", "profile.yaml", "ModelProfile", "profile", "default", "/instance", "waiting", [])];
        using var context = Context(api);
        var cut = context.Render<BootstrapStartupStepPanel>(p => p.Add(x => x.Snapshot, api.Snapshot)
            .Add(x => x.StepId, "resources").Add(x => x.Kind, "ModelProfile").Add(x => x.FollowCurrent, true));
        var details = cut.Find("[data-testid='startup-resource-operations']");
        Assert.AreEqual(1, details.QuerySelectorAll("[data-testid='startup-activity-details']").Length);
        Assert.AreEqual(1, cut.FindAll("[data-testid='startup-activity-details']").Count);
        StringAssert.Contains(details.TextContent, "model:exact");
        cut.Find("[data-testid='startup-change-model']").Click();
        Assert.AreEqual(1, cut.FindAll("[data-testid='startup-model-provider']").Count);
    }

    [TestMethod]
    public void StartupProgressDoesNotRepeatItsOwnNavigationNotice()
    {
        using var context = Context(new StartupClient());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/startup/progress");
        var cut = context.Render<BootstrapStartupLayout>(p => p.Add(x => x.Body, builder => builder.AddContent(0, "Progress body")));
        Assert.AreEqual(0, cut.FindAll("[data-testid='bootstrap-startup-notice']").Count);
        Assert.AreEqual(1, cut.FindAll("[data-testid='console-shell']").Count);
    }

    [TestMethod]
    public void PendingStartupRedirectsWithoutRenderingTheOverview()
    {
        var api = new StartupClient();
        using var context = Context(api);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/?source=login");
        var rendered = false;
        _ = context.Render<BootstrapStartupGate>(p => p.Add(x => x.ChildContent, builder => { rendered = true; builder.AddContent(0, "overview"); }));
        Assert.IsFalse(rendered);
        StringAssert.Contains(navigation.Uri, "/startup/progress?ReturnUrl=%2F%3Fsource%3Dlogin");
        Assert.AreEqual(0, api.Reads);
    }

    [TestMethod]
    public void DeniedCallersSeeNoRouteOrProgress()
    {
        var api = new StartupClient { AdmissionDenied = true };
        using var context = Context(api);
        var cut = context.Render<BootstrapStartupGate>(p => p.AddChildContent("normal-route"));
        StringAssert.Contains(cut.Markup, "Access temporarily unavailable");
        Assert.IsFalse(cut.Markup.Contains("normal-route", StringComparison.Ordinal));
        Assert.AreEqual(0, api.Reads);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AdmittedRoutesKeepTheirComponentDuringContextUpdates(bool ready)
    {
        var api = new StartupClient { Ready = ready };
        using var context = Context(api);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(ready ? "/" : "/startup/progress");
        var cut = context.Render<BootstrapStartupGate>(p => p.AddChildContent(builder =>
        {
            builder.OpenComponent<RouteProbe>(0);
            builder.CloseComponent();
        }));
        var route = cut.FindComponent<RouteProbe>().Instance;
        api.NextAdmission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        navigation.NavigateTo(ready ? "/agents?context=%2Finstance" : "/startup/progress?context=%2Finstance");
        cut.Render();
        Assert.AreSame(route, cut.FindComponent<RouteProbe>().Instance);
        Assert.AreEqual(1, api.AdmissionReads);
        if (!ready)
        {
            api.NextAdmission = null;
            navigation.NavigateTo("/agent-runs");
            cut.Render();
            Assert.AreEqual(2, api.AdmissionReads);
            StringAssert.Contains(navigation.Uri, "/startup/progress?ReturnUrl=%2Fagent-runs");
            Assert.AreEqual(0, cut.FindComponents<RouteProbe>().Count);
        }
    }

    [TestMethod]
    [DataRow("en-US", "Instance initialization", "Required model")]
    [DataRow("fr-FR", "Initialisation de l’instance", "Modèle attendu")]
    public void TheSharedProgressPanelUsesProviderNeutralDataAndLocalizedLabels(string culture, string title, string modelLabel)
    {
        using var cultures = new TestCultureScope(culture);
        var api = new StartupClient();
        using var context = Context(api);
        var cut = context.Render<BootstrapStartupProgress>();
        StringAssert.Contains(System.Net.WebUtility.HtmlDecode(cut.Markup), title);
        StringAssert.Contains(cut.Find("[role='tabpanel']").TextContent, modelLabel);
        StringAssert.Contains(cut.Find("[role='tabpanel']").TextContent, "model:exact");
        Assert.AreEqual(0, cut.FindAll("[role='tabpanel'] a").Count);
        Assert.AreEqual(1, api.Reads);
    }

    [TestMethod]
    public void StartupLayoutUsesTheStandardFrameWithConsultationAndPersistentNotice()
    {
        using var context = Context(new StartupClient());
        var cut = context.Render<BootstrapStartupLayout>(p => p.Add(x => x.Body, builder => builder.AddContent(0, "Progress body")));
        Assert.IsFalse(cut.FindComponent<MainLayout>().Instance.EnableCommandPalette);
        Assert.AreEqual(1, cut.FindAll("[data-testid='console-shell']").Count);
        Assert.AreEqual(1, cut.FindAll(".sidebar").Count);
        Assert.AreEqual(1, cut.FindAll("[data-testid='bootstrap-startup-notice']").Count);
        Assert.AreEqual(1, cut.FindAll("a[href='/logout']").Count);
        StringAssert.Contains(cut.Find("main").TextContent, "Progress body");
    }

    [TestMethod]
    public void OnlyPlannedFamiliesAppearAndTheResourceCollectionIsPagedAndFilterable()
    {
        var api = new StartupClient();
        api.Snapshot = api.Snapshot with
        {
            Families = [new("resources", "Parameter", "running", 512, 511, 511, 0, 1, 0, 0)]
        };
        api.ResourceRows = Enumerable.Range(0, 512).Select(index => new BootstrapStartupResource(
            "row-" + index, "resources", "load-test", index + ".yaml", "Parameter", $"item-{index:D3}",
            "default", "/tenants/dev", index == 511 ? "failed" : "created",
            [new("resource_apply", index == 511 ? "failed" : "created", DateTimeOffset.UnixEpoch)])).ToArray();
        using var context = Context(api);
        var cut = context.Render<BootstrapStartupProgress>();
        Assert.AreEqual(5, cut.FindAll("[role='tab']").Count, "Only the four stages and one selected family should appear.");
        Assert.AreEqual(0, cut.FindAll("[data-step-id='resources:Agent']").Count);
        cut.Find("[data-step-id='resources:Parameter']").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(25, cut.FindAll("[data-testid='startup-resource-row']").Count));
        Assert.AreEqual(25, api.ResourceQueries.Last().Limit);
        cut.FindAll(".collection-pager button").Single(button => button.TextContent.Trim() == "Next").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(25, api.ResourceQueries.Last().Offset));
        cut.Find("[data-testid='startup-resource-state']").Change("failed");
        cut.WaitForAssertion(() => Assert.AreEqual(1, cut.FindAll("[data-testid='startup-resource-row']").Count));
        Assert.AreEqual(0, api.ResourceQueries.Last().Offset);
        cut.Find("[data-testid='startup-resource-row'] .collection-actions button").Click();
        StringAssert.Contains(cut.Find("[data-testid='startup-resource-operations']").TextContent, "Apply resource");
        Assert.AreEqual("failed", api.ResourceQueries.Last().State);
        cut.Find(".step-progress-follow").Click();
        cut.WaitForAssertion(() => Assert.IsNull(api.ResourceQueries.Last().State));
        Assert.AreEqual(25, cut.FindAll("[data-testid='startup-resource-row']").Count);
        cut.Find("[data-testid='startup-resource-row'] .collection-actions button").Click();
        Assert.AreEqual("false", cut.Find("[data-step-progress]").GetAttribute("data-follow-active"));
    }

    [TestMethod]
    public void ARepeatedPolicyGroupUsesItsOwnPanelAndFilteredResources()
    {
        var api = new StartupClient();
        api.Snapshot = api.Snapshot with
        {
            Activity = new("ResourceAccessPolicy", "policy-two", GroupId: "policy-second"),
            Families = [new("resources", "ResourceAccessPolicy", "succeeded", 1, 1, 1, 0, 0, 0, 0) { GroupId = "policy-first" },
                new("resources", "ModelProfile", "succeeded", 1, 1, 1, 0, 0, 0, 0) { GroupId = "profile" },
                new("resources", "ResourceAccessPolicy", "running", 1, 0, 0, 0, 0, 0, 0) { GroupId = "policy-second" }]
        };
        api.ResourceRows = [new("row-one", "resources", "base", "one.yaml", "ResourceAccessPolicy", "policy-one", "default", "/instance", "created", []) { GroupId = "policy-first" },
            new("row-two", "resources", "tenant", "two.yaml", "ResourceAccessPolicy", "policy-two", "default", "/tenants/dev", "running", []) { GroupId = "policy-second" }];
        using var context = Context(api);
        var cut = context.Render<BootstrapStartupProgress>();
        Assert.AreEqual("policy-second", api.ResourceQueries.Last().GroupId);
        StringAssert.Contains(cut.Find("[role='tabpanel']").TextContent, "policy-two");
        Assert.IsFalse(cut.Find("[role='tabpanel']").TextContent.Contains("policy-one", StringComparison.Ordinal));
        cut.Find("[data-step-id='resources:ResourceAccessPolicy:policy-first']").Click();
        Assert.AreEqual("policy-first", api.ResourceQueries.Last().GroupId);
        StringAssert.Contains(cut.Find("[role='tabpanel']").TextContent, "policy-one");
    }

    [TestMethod]
    [DataRow("en-US", "Change provider or model")]
    [DataRow("fr-FR", "Modifier le fournisseur ou le modèle")]
    public void ABlockedProfileCanSelectAnotherProviderAndKnownModelWithoutLeavingProgress(string culture, string label)
    {
        using var cultures = new TestCultureScope(culture);
        var api = new StartupClient();
        using var context = Context(api);
        var cut = context.Render<BootstrapStartupModelCorrection>(p => p.Add(x => x.AttemptId, api.Snapshot.AttemptId).Add(x => x.ResourceId, "profile-id"));
        StringAssert.Contains(cut.Find("button").TextContent, label);
        cut.Find("[data-testid='startup-change-model']").Click();
        cut.Find("[data-testid='startup-model-provider']").Change(api.BackupProvider.ToString("D"));
        Assert.AreEqual("SELECT", cut.Find("[data-testid='startup-model-name']").TagName);
        Assert.AreEqual(0, cut.FindAll("datalist").Count);
        cut.Find("[data-testid='startup-model-name']").Change("replacement:model");
        cut.Find("[data-testid='startup-model-apply']").Click();
        Assert.AreEqual(1, api.ModelChanges.Count);
        Assert.AreEqual("backup", api.ModelChanges[0].Provider.Name);
        Assert.AreEqual(ResourceScopeRef.Instance, api.ModelChanges[0].Provider.ScopeRef);
        Assert.AreEqual("replacement:model", api.ModelChanges[0].Model);
        Assert.AreEqual(api.Snapshot.AttemptId, api.ModelChanges[0].AttemptId);
        Assert.AreEqual(0L, api.ModelChanges[0].Version);
        Assert.AreEqual(0, cut.FindAll("[data-testid='startup-model-apply']").Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PollingRecoversAndRetainsCompletedProgressUntilExplicitConsoleNavigation(bool timeout)
    {
        var api = new StartupClient();
        var clock = new ManualClock();
        using var context = Context(api, clock);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/startup/progress?ReturnUrl=%2F%2Fexternal.example");
        var cut = context.Render<BootstrapStartupProgress>();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        // The Console shell preserves context through a location-changing handler.
        using var registration = navigation.RegisterLocationChangingHandler(change =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(change.TargetLocation);
            return ValueTask.CompletedTask;
        });
        api.Timeout = timeout; api.FailRead = true;
        clock.Tick();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Observation will resume"));
        Assert.IsFalse(cut.Markup.Contains("PRIVATE-TRANSPORT-ERROR", StringComparison.Ordinal));
        api.FailRead = false; api.Snapshot = api.Snapshot with { Ready = true, State = "ready" };
        clock.Tick();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Bootstrap completed"));
        Assert.AreEqual("http://localhost/startup/progress?ReturnUrl=%2F%2Fexternal.example", navigation.Uri);
        var reads = api.Reads;
        clock.Tick();
        Assert.AreEqual(reads, api.Reads);
        cut.Find("button.button-primary").Click();
        Assert.AreEqual("http://localhost/", navigation.Uri);
        Assert.IsTrue(api.Reads >= 3);
    }

    [TestMethod]
    [DataRow("en-US", "Open Console")]
    [DataRow("fr-FR", "Ouvrir la console")]
    public void AlreadyCompletedProgressKeepsResultsAndAnExplicitSafeReturnAction(string culture, string label)
    {
        using var cultures = new TestCultureScope(culture);
        var api = new StartupClient();
        api.Snapshot = api.Snapshot with
        {
            Ready = true,
            State = "ready",
            ActiveStepId = null,
            Steps = api.Snapshot.Steps.Select(step => step with { State = "succeeded" }).ToArray()
        };
        var clock = new ManualClock();
        using var context = Context(api, clock);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/startup/progress?ReturnUrl=%2Fagents");
        var cut = context.Render<BootstrapStartupProgress>();
        var button = cut.Find("button.button-primary");
        StringAssert.Contains(button.TextContent, label);
        Assert.IsTrue(navigation.Uri.Contains("/startup/progress", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains("Stage_", StringComparison.Ordinal));
        clock.Tick();
        Assert.AreEqual(1, api.Reads);
        button.Click();
        Assert.AreEqual("http://localhost/agents", navigation.Uri);
    }

    [TestMethod]
    public void DependencyReleaseResumesFollowingAfterInspectingACompletedGroup()
    {
        var api = new StartupClient();
        var clock = new ManualClock();
        api.Snapshot = api.Snapshot with
        {
            Activity = new("ModelProfile", "waiting-profile", WaitReason: "required_model_missing", GroupId: "second"),
            Steps = api.Snapshot.Steps.Select(step => step.Id == "resources" ? step with { State = "waiting" } : step).ToArray(),
            Families = [new("resources", "ModelProfile", "succeeded", 1, 1, 1, 0, 0, 0, 0) { GroupId = "first" },
                new("resources", "ModelProfile", "waiting", 1, 0, 0, 0, 0, 0, 1) { GroupId = "second" },
                new("resources", "Agent", "pending", 41, 0, 0, 0, 0, 0, 0) { GroupId = "agents" },
                new("resources", "Flow", "pending", 40, 0, 0, 0, 0, 0, 0) { GroupId = "flows" }]
        };
        using var context = Context(api, clock);
        var cut = context.Render<BootstrapStartupProgress>();
        cut.Find("[data-step-id='resources:ModelProfile:first']").Click();
        Assert.AreEqual("false", cut.Find("[data-step-progress]").GetAttribute("data-follow-active"));
        Assert.AreEqual("resources:ModelProfile:first", cut.Find("[data-selected-step]").GetAttribute("data-selected-step"));
        clock.Tick();
        cut.WaitForAssertion(() => Assert.AreEqual("resources:ModelProfile:first", cut.Find("[data-selected-step]").GetAttribute("data-selected-step")));
        api.Snapshot = api.Snapshot with
        {
            Revision = api.Snapshot.Revision + 1,
            Activity = new("Agent", "next-agent", GroupId: "agents"),
            Steps = api.Snapshot.Steps.Select(step => step.Id == "resources" ? step with { State = "running" } : step).ToArray(),
            Families = api.Snapshot.Families.Select(family => family.GroupId == "second" ? family with { State = "succeeded" }
                : family.GroupId == "agents" ? family with { State = "running" } : family).ToArray()
        };
        clock.Tick();
        cut.WaitForAssertion(() => Assert.AreEqual("resources:Agent:agents", cut.Find("[data-selected-step]").GetAttribute("data-selected-step")));
        Assert.AreEqual("true", cut.Find("[data-step-progress]").GetAttribute("data-follow-active"));
        api.Snapshot = api.Snapshot with
        {
            Revision = api.Snapshot.Revision + 1,
            Activity = new("Flow", "next-flow", GroupId: "flows"),
            Families = api.Snapshot.Families.Select(family => family.GroupId == "agents" ? family with { State = "succeeded" }
                : family.GroupId == "flows" ? family with { State = "running" } : family).ToArray()
        };
        clock.Tick();
        cut.WaitForAssertion(() => Assert.AreEqual("resources:Flow:flows", cut.Find("[data-selected-step]").GetAttribute("data-selected-step")));
        Assert.AreEqual("true", cut.Find("[data-step-progress]").GetAttribute("data-follow-active"));
    }

    [TestMethod]
    public void TerminalFailureRetainsTheProgressViewWithRecoveryGuidance()
    {
        var api = new StartupClient();
        api.Snapshot = api.Snapshot with { State = "failed", Steps = api.Snapshot.Steps.Select(s => s.Id == "resources" ? s with { State = "failed" } : s).ToArray() };
        using var context = Context(api);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/startup/progress");
        var cut = context.Render<BootstrapStartupProgress>();
        StringAssert.Contains(cut.Markup, "Bootstrap failed");
        StringAssert.Contains(cut.Markup, "restart the instance");
        Assert.AreEqual("http://localhost/startup/progress", navigation.Uri);
    }

    private static StartupClient ContributionClient()
    {
        var api = new StartupClient();
        api.Snapshot = api.Snapshot with
        {
            ActiveStepId = "extensions",
            Activity = new("ConnectedService", "nantes-alertes-openapi", WaitReason: "contribution_unavailable", ResourceId: "openapi"),
            Families = [new("extensions", "ExtensionContribution", "waiting", 4, 1, 0, 0, 0, 0, 3)]
        };
        api.ResourceRows =
        [
            new("ollama", "extensions", "ollama", "ollama.yaml", "ExtensionContribution", "ollama", "default", "/instance", "succeeded", []),
            Contribution("postgres", "postgresql", "ConnectedServiceType", "ConnectedService", "nantes-alertes-postgresql"),
            Contribution("jsonb", "postgresql.jsonb", "StorageType", "Storage", "nantes-alertes-jsonb"),
            Contribution("openapi", "openapi", "ConnectedServiceType", "ConnectedService", "nantes-alertes-openapi")
        ];
        return api;
    }

    private static BootstrapStartupResource Contribution(string id, string contribution, string kind, string consumerKind, string consumerName) =>
        new(id, "extensions", "nantes-alertes", contribution + ".yaml", "ExtensionContribution", "extension/" + contribution,
            "default", "/instance", "waiting", [new("contribution_availability", "waiting", DateTimeOffset.UnixEpoch)])
        {
            WaitReason = "contribution_unavailable",
            Dependency = new("extension", kind, contribution, "1.0.0", null, consumerKind, consumerName)
        };

    private static BunitContext Context(StartupClient api, TimeProvider? clock = null)
    {
        var context = new BunitContext();
        context.Services.AddAgentstrationWebComponents();
        context.Services.AddSingleton<TimeProvider>(clock ?? TimeProvider.System);
        context.Services.AddSingleton<IBootstrapStartupApiClient>(api);
        context.Services.AddSingleton<IBootstrapProfilesApiClient>(new BootstrapProfilesApiClient(new HttpClient()));
        context.Services.AddSingleton<AuthenticationStateProvider>(new UserState());
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/step-progress.js").Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/collection-ui.js").Mode = JSRuntimeMode.Loose;
        return context;
    }
    private sealed class RouteProbe : ComponentBase { }
    private sealed class UserState : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Administrator")], "fixture"))));
    }
    private sealed class StartupClient : IBootstrapStartupApiClient
    {
        public bool Ready { get; set; }
        public bool AdmissionDenied { get; set; }
        public bool FailRead { get; set; }
        public bool Timeout { get; set; }
        public int Reads { get; private set; }
        public int AdmissionReads { get; private set; }
        public Guid MainProvider { get; } = Guid.NewGuid();
        public Guid BackupProvider { get; } = Guid.NewGuid();
        public List<BootstrapStartupModelChange> ModelChanges { get; } = [];
        public Task<BootstrapStartupModelOptions> GetModelOptionsAsync(Guid attemptId, string resourceId, string? providerName, string? providerNamespace, CancellationToken cancellationToken) =>
            Task.FromResult(new BootstrapStartupModelOptions(attemptId, resourceId, 0,
                new(providerName ?? "primary", ResourceScopeRef.Instance, ResourceNamespace.Default), "missing:model",
                [new(MainProvider, new("primary", ResourceScopeRef.Instance, ResourceNamespace.Default), "Primary"),
                 new(BackupProvider, new("backup", ResourceScopeRef.Instance, ResourceNamespace.Default), "Backup")], ["replacement:model"]));
        public Task ApplyModelChangeAsync(string resourceId, BootstrapStartupModelChange request, CancellationToken cancellationToken)
        { ModelChanges.Add(request); return Task.CompletedTask; }
        public IReadOnlyList<BootstrapStartupResource> ResourceRows { get; set; } = [];
        public List<BootstrapStartupResourceQuery> ResourceQueries { get; } = [];
        public Task<BootstrapStartupResourcePage> GetResourcesAsync(BootstrapStartupResourceQuery query, CancellationToken cancellationToken)
        {
            ResourceQueries.Add(query);
            var matches = ResourceRows.Where(row => (query.StepId is null || row.StepId == query.StepId)
                && (query.GroupId is null || row.GroupId == query.GroupId) && (query.Kind is null || row.Kind == query.Kind) && (query.State is null || row.State == query.State)).ToArray();
            return Task.FromResult(new BootstrapStartupResourcePage(Snapshot.AttemptId, Snapshot.Revision,
                matches.Length, query.Offset, query.Limit, matches.Skip(query.Offset).Take(query.Limit).ToArray()));
        }
        public TaskCompletionSource<BootstrapStartupAdmission>? NextAdmission { get; set; }
        public BootstrapStartupSnapshot Snapshot { get; set; } = new(Guid.NewGuid(), 1, false, true, "running",
            [new("topology", "succeeded"), new("extensions", "succeeded"), new("resources", "waiting"), new("finalization", "pending")],
            "resources", new("ModelProfile", "profile", "provider", "model:exact", "required_model_missing", "other", ProviderScopeRef: "/instance"), 2, 1, 0, 0);
        public Task<BootstrapStartupAdmission> GetAdmissionAsync(CancellationToken cancellationToken)
        {
            AdmissionReads++;
            return AdmissionDenied ? throw new AgentstrationApiException("PRIVATE", "fixture", HttpStatusCode.Forbidden)
                : NextAdmission?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(new BootstrapStartupAdmission(Ready));
        }
        public Task<BootstrapStartupSnapshot> GetAsync(CancellationToken cancellationToken)
        {
            Reads++;
            if (FailRead && Timeout) throw new TaskCanceledException("PRIVATE-TRANSPORT-ERROR");
            return FailRead ? throw new AgentstrationApiException("PRIVATE-TRANSPORT-ERROR", "fixture", HttpStatusCode.ServiceUnavailable)
                : Task.FromResult(Snapshot);
        }
    }
    private sealed class ManualClock : TimeProvider
    {
        private Action? tick;
        public void Tick() => tick?.Invoke();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(() => callback(state)); tick = timer.Tick; return timer;
        }
        private sealed class ManualTimer(Action callback) : ITimer
        {
            private bool disposed;
            public void Tick() { if (!disposed) callback(); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
