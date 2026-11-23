using Agentstration.Bootstrap.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Bootstrap;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BootstrapProgressComponentTests
{
    [TestMethod]
    public async Task FollowingTracksTheActiveResourcePageAndTerminalFailureWithoutStealingManualInspection()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Context(new StubClient());
        var plan = Enumerable.Range(1, 60).Select(index => new BootstrapPlannedResource("batch", index + ".yaml", "Parameter", "parameter-" + index)).ToArray();
        var application = new BootstrapApplicationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Metadata = new() { Name = "follow" },
            Definition = new()
            {
                Profiles = ["batch"],
                TotalResources = 60,
                ResourcePlan = plan,
                CurrentResource = new("batch", "41.yaml", "Parameter", "parameter-41", BootstrapResourceDisposition.Create)
            }
        };
        var cut = context.Render<BootstrapApplicationProgress>(p => p.Add(x => x.Application, application));
        StringAssert.Contains(cut.Find("[data-progress-current='true']").TextContent, "parameter-41");
        Assert.HasCount(25, cut.FindAll("[data-testid='bootstrap-application-resource']"));
        application = application with
        {
            Definition = application.Definition with
            {
                CurrentResource = null,
                Resources = plan.Take(41).Select(row => new BootstrapAppliedResource(row.Profile, row.Location, row.Kind, row.Name, BootstrapResourceDisposition.Create)).ToArray()
            }
        };
        cut.Render(p => p.Add(x => x.Application, application));
        StringAssert.Contains(cut.Find("[data-progress-current='true']").TextContent, "parameter-41");
        await cut.InvokeAsync(cut.FindComponent<StepProgress>().Instance.PauseFollowingAsync);
        cut.Find("input[type='search']").Input("parameter-26");
        application = application with { Definition = application.Definition with { CurrentResource = new("batch", "52.yaml", "Parameter", "parameter-52", BootstrapResourceDisposition.Create) } };
        cut.Render(p => p.Add(x => x.Application, application));
        StringAssert.Contains(cut.Markup, "parameter-26");
        cut.Find(".step-progress-follow").Click();
        StringAssert.Contains(cut.Find("[data-progress-current='true']").TextContent, "parameter-52");
        var failure = new BootstrapFailure("validation", "data_source_profile_not_published", "test-log-reference");
        application = application with
        {
            Definition = application.Definition with
            {
                Status = BootstrapApplicationStatus.PartiallyApplied,
                CurrentResource = null,
                Failure = failure,
                ResourcePlan = [.. plan, new("next", "failure.yaml", "DataSourceProfile", "failed-profile")],
                Resources = [.. plan.Select(row => new BootstrapAppliedResource(row.Profile, row.Location, row.Kind, row.Name, BootstrapResourceDisposition.Create)),
                new("next", "failure.yaml", "DataSourceProfile", "failed-profile", BootstrapResourceDisposition.Failed, "PRIVATE-ERROR-BODY") { Failure = failure }]
            }
        };
        cut.Render(p => p.Add(x => x.Application, application));
        StringAssert.Contains(cut.Find("[role='tabpanel']").TextContent, "data_source_profile_not_published");
        StringAssert.Contains(cut.Markup, "test-log-reference");
        Assert.IsFalse(cut.Markup.Contains("PRIVATE-ERROR-BODY", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("en-US", "Processed 1 of 512 resources", "Created", "Resource 2:")]
    [DataRow("fr-FR", "1 ressources traitées sur 512", "Créées", "Ressource 2 :")]
    public void ReloadResumesPersistedProgressWithoutSubmittingAgain(string locale, string progress, string created, string current)
    {
        using var culture = new TestCultureScope(locale);
        var client = new StubClient();
        using var context = Context(client);
        var rendered = context.Render<BootstrapProfiles>();
        rendered.WaitForAssertion(() =>
        {
            var panel = rendered.Find("[data-testid='bootstrap-application-progress']");
            StringAssert.Contains(panel.TextContent, progress);
            Assert.AreEqual("1", panel.QuerySelector(".metric-card strong")?.TextContent.Trim());
            StringAssert.Contains(panel.QuerySelector(".metric-card")!.TextContent, created);
            Assert.IsTrue(panel.QuerySelector(".step-progress") is not null);
            StringAssert.Contains(panel.TextContent, current);
            Assert.AreEqual("512", panel.QuerySelector("progress")?.GetAttribute("max"));
            Assert.AreEqual("1", panel.QuerySelector("progress")?.GetAttribute("value"));
            StringAssert.Contains(panel.TextContent, "Parameter/bootstrap-load-test-002");
        });
        Assert.AreEqual(0, client.Submissions);
    }

    [TestMethod]
    public void PinnedIdentitySurvivesANewerApplicationInHistory()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StubClient { IncludeNewerApplication = true };
        using var context = Context(client);
        context.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/settings/bootstrap?applicationId=retained-application");
        var rendered = context.Render<BootstrapProfiles>();
        rendered.WaitForAssertion(() => Assert.AreEqual("retained-application",
            rendered.Find("#bootstrap-application-detail").GetAttribute("data-application-id")));
        Assert.AreEqual(0, client.Submissions);
    }

    [TestMethod]
    public void SelectingHistoryPinsItsIdentityForReload()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Context(new StubClient(), application: false);
        var rendered = context.Render<BootstrapProfiles>();
        rendered.FindAll("button").Single(button => button.TextContent == "View").Click();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        Assert.AreEqual("?applicationId=retained-application", new Uri(navigation.Uri).Query);
    }

    [TestMethod]
    public void ObservationRetriesSameApplicationAndReachesTerminalState()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StubClient { DisconnectOnce = true };
        using var context = Context(client);
        var rendered = context.Render<BootstrapProfiles>();
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup,
            "Progress updates are temporarily unavailable"), TimeSpan.FromSeconds(5));
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find("[data-testid='bootstrap-application-progress']").TextContent, "Succeeded");
            Assert.AreEqual("2", rendered.Find("progress").GetAttribute("value"));
        }, TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, client.Submissions);
        Assert.IsTrue(client.Observations >= 2);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancelRequestsCancellationForExistingIdentity(bool changeSelection)
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StubClient { ExposeCatalog = changeSelection };
        using var context = Context(client);
        var rendered = context.Render<BootstrapProfiles>();
        if (changeSelection)
        {
            rendered.FindAll("button").Single(button => button.TextContent == "Back to preparation").Click();
            rendered.Find("[data-profile-name='base']").Change(true);
            rendered.FindAll("button").Single(button => button.TextContent == "View").Click();
        }
        rendered.FindAll("button").Single(button => button.TextContent == "Cancel application").Click();
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "Cancellation requested."));
        Assert.AreEqual("retained-application", client.CancelledId);
        Assert.AreEqual(0, client.Submissions);
    }

    [TestMethod]
    public void CatalogUsesTheSharedListAndPreviewStillPrecedesConfirmedSubmission()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StubClient { ExposeCatalog = true };
        using var context = Context(client, application: false);
        var cut = context.Render<BootstrapProfiles>();
        Assert.AreEqual(1, cut.FindComponents<ResourceCollectionTemplate<BootstrapProfileSummary>>().Count);
        cut.Find("[data-profile-name='base']").Change(true);
        Assert.AreEqual(0, client.Submissions);
        cut.FindAll("button").Single(button => button.TextContent == "Preview application").Click();
        Assert.AreEqual(1, client.Previews);
        Assert.AreEqual(0, client.Submissions);
        StringAssert.Contains(cut.Find(".bootstrap-preview-summary").TextContent, "Ready");
        cut.FindAll("button").Single(button => button.TextContent == "Apply preview").Click();
        Assert.AreEqual(0, client.Submissions);
        cut.Find("[role='dialog']").QuerySelectorAll("button").Single(button => button.TextContent == "Apply profiles").Click();
        Assert.AreEqual(1, client.Submissions);
        Assert.AreEqual(1, cut.FindAll("[data-testid='bootstrap-manual-step-progress']").Count);
    }

    [TestMethod]
    public void InvalidPreviewIsAPreparationFailureWithoutPretendingResourcesWereApplied()
    {
        using var context = Context(new StubClient());
        var application = new BootstrapApplicationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = BootstrapKinds.BootstrapApplication,
            Metadata = new() { Name = "invalid" },
            Definition = new()
            {
                Status = BootstrapApplicationStatus.Failed,
                Error = "bootstrap_application_invalid",
                TotalResources = 1,
                ResourcePlan = [new("base", "one.yaml", "Parameter", "one")]
            }
        };
        var cut = context.Render<BootstrapApplicationProgress>(p => p.Add(x => x.Application, application));
        Assert.IsTrue(cut.Find("[data-step-id='prepare']").ClassList.Contains("status-failed"));
        Assert.IsTrue(cut.Find("[data-step-id='resources']").ClassList.Contains("status-interrupted"));
        Assert.AreEqual("true", cut.Find("[data-step-id='prepare']").GetAttribute("aria-selected"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MixedScopesHaveIndividualTargetsAndPreviewIsASeparateScreenWithPreservedPreparation(bool plannedTargets)
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StubClient { MixedCatalog = true };
        using var context = Context(client, application: false);
        var cut = context.Render<BootstrapProfiles>();
        foreach (var name in new[] { "workspace", "tenant", "base" }) cut.Find($"[data-profile-name='{name}']").Change(true);
        CollectionAssert.AreEqual(new[] { "base", "tenant", "workspace" }, cut.FindAll("[data-bootstrap-profile]").Select(node => node.GetAttribute("data-bootstrap-profile")).ToArray());
        var tenantSelect = cut.Find("[data-target-profile='tenant']");
        tenantSelect.Change(plannedTargets ? tenantSelect.QuerySelectorAll("option").Single(option => option.TextContent.Contains("future-tenant", StringComparison.Ordinal)).GetAttribute("value") : StubClient.TenantId.ToString());
        var workspaceSelect = cut.Find("[data-target-profile='workspace']");
        workspaceSelect.Change(plannedTargets ? workspaceSelect.QuerySelectorAll("option").Single(option => option.TextContent.Contains("future-workspace", StringComparison.Ordinal)).GetAttribute("value") : StubClient.WorkspaceId.ToString());
        cut.FindAll("button").Single(button => button.TextContent == "Preview application").Click();
        var selection = client.LastSelection;
        Assert.IsNotNull(selection);
        Assert.IsNotNull(selection.Applications);
        CollectionAssert.AreEqual(new[] { "base", "tenant", "workspace" }, selection.Applications.Select(item => item.Profile).ToArray());
        Assert.AreEqual(0, cut.FindAll("[data-testid='distribution-bootstrap-profiles']").Count);
        Assert.AreEqual(1, cut.FindAll(".bootstrap-preview-summary").Count);
        Assert.AreEqual(0, client.Submissions);
        cut.FindAll("button").Single(button => button.TextContent == "Back to preparation").Click();
        Assert.AreEqual(1, cut.FindAll("[data-testid='distribution-bootstrap-profiles']").Count);
        Assert.AreEqual(0, cut.FindAll(".bootstrap-preview-summary").Count);
        Assert.IsFalse(string.IsNullOrEmpty(cut.Find("[data-target-profile='workspace']").GetAttribute("value")));
        Assert.HasCount(3, cut.FindAll("input[data-profile-name]:checked"));
    }

    [TestMethod]
    [DataRow("en-US", "Applied by", "Pack origin")]
    [DataRow("fr-FR", "Appliqué par", "Pack d’origine")]
    public void AppliedByShowsBootstrapAlongsidePackOrigin(string locale, string applicationLabel, string originLabel)
    {
        using var culture = new TestCultureScope(locale);
        using var context = Context(new StubClient(), application: false);
        var provenance = new ResourceBootstrapApplication("profile-id", "development", "1.2.0", Guid.NewGuid(),
            ResourceScopeRef.Instance, "development/10-pack.yaml document 1");
        var rendered = context.Render<BootstrapResourceProvenance>(parameters => parameters
            .Add(value => value.AppliedBy, provenance)
            .Add(value => value.Origin, new ResourceOrigin { Kind = ResourceOriginKind.Pack, Reference = "example/fixture", Version = "2.4.0" }));
        var panel = rendered.Find("[data-testid='bootstrap-resource-provenance']");
        StringAssert.Contains(panel.TextContent, applicationLabel);
        StringAssert.Contains(panel.TextContent, originLabel);
        StringAssert.Contains(panel.TextContent, "Bootstrap development 1.2.0");
        StringAssert.Contains(panel.TextContent, "example/fixture 2.4.0");
        Assert.HasCount(0, panel.QuerySelectorAll("a"));
    }

    [TestMethod]
    public void AppliedProfileListUsesTheSharedBannerAndFiltersTheAuthorizedProfiles()
    {
        using var culture = new TestCultureScope("fr-FR");
        var client = new StubClient
        {
            AppliedProfiles =
            [
                new("base", "base", "Local development", BootstrapProfileScope.Instance),
                new("workspace", "workspace-example", "Workspace example", BootstrapProfileScope.Workspace)
            ]
        };
        using var context = Context(client, application: false);
        var cut = context.Render<AppliedBootstrapProfiles>();

        Assert.AreEqual("Profils de bootstrap appliqués", cut.Find(".page-header h1").TextContent);
        Assert.AreEqual("Profil de bootstrap", cut.Find("th").TextContent);
        Assert.AreEqual("2", cut.Find(".metric-card strong").TextContent);
        Assert.HasCount(2, cut.FindAll("[data-testid='applied-bootstrap-profile-row']"));

        cut.Find("[data-filter-key='scope'] select").Change("Workspace");
        Assert.HasCount(1, cut.FindAll("[data-testid='applied-bootstrap-profile-row']"));
        Assert.AreEqual("Workspace example", cut.Find(".resource-name-link").TextContent);
        cut.Find("input[type='search']").Input("workspace-example");
        cut.Find("[data-testid='applied-bootstrap-profiles-refresh']").Click();
        Assert.AreEqual("Workspace example", cut.Find(".resource-name-link").TextContent);
        Assert.AreEqual("2", cut.Find(".metric-card strong").TextContent);
        cut.Find("input[type='search']").Input("Local development");
        Assert.HasCount(0, cut.FindAll("[data-testid='applied-bootstrap-profile-row']"));
    }

    [TestMethod]
    public void AppliedProfileDetailShowsTheSelectedVersionAndItsResourceOutcomes()
    {
        using var culture = new TestCultureScope("fr-FR");
        var registered = new AppliedBootstrapVersion("base", "1.2.3", BootstrapProfileScope.Instance, "profile-digest", DateTimeOffset.UnixEpoch, null);
        var latest = new ScopedBootstrapApplication(Guid.NewGuid(), Guid.NewGuid(), ResourceScopeRef.Instance, BootstrapApplicationSource.Startup,
            DateTimeOffset.UnixEpoch.AddMinutes(1), null, BootstrapApplicationStatus.Succeeded, [registered], [],
            [new("base", "parameter.yaml", "Parameter", "fixture", BootstrapResourceDisposition.Create)]);
        var client = new StubClient
        {
            AppliedProfiles = [new("base", "base", "Local development", BootstrapProfileScope.Instance)],
            AppliedVersions = [registered],
            AppliedHistory = [latest, latest with { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UnixEpoch, Resources = [] }]
        };
        using var context = Context(client, application: false);
        var cut = context.Render<AppliedBootstrapProfiles>(parameters => parameters.Add(page => page.ProfileId, "base"));

        Assert.AreEqual("Profil de bootstrap", cut.Find(".resource-type").TextContent);
        StringAssert.Contains(cut.Find(".resource-version-badge").TextContent, "1.2.3");
        Assert.AreEqual(("base", "1.2.3", ResourceScopeRef.Instance), client.HistoryRequest);
        var applications = cut.FindAll("[data-testid='bootstrap-scoped-application']");
        Assert.HasCount(2, applications);
        Assert.IsTrue(applications[0].HasAttribute("open"));
        Assert.IsFalse(applications[1].HasAttribute("open"));
        StringAssert.Contains(applications[0].QuerySelector("summary")!.TextContent, "1 ressource");
        StringAssert.Contains(applications[0].QuerySelector("tbody")!.TextContent, "fixture");
        StringAssert.Contains(applications[0].QuerySelector("tbody")!.TextContent, "Créée");
        Assert.IsFalse(cut.Find("[data-testid='bootstrap-profile-versions']").TextContent.Contains("profile-digest", StringComparison.Ordinal));
        var identity = cut.Find("[data-testid='resource-identity-popover']");
        StringAssert.Contains(identity.TextContent, "Empreinte du profil");
        StringAssert.Contains(identity.TextContent, "profile-digest");
    }

    [TestMethod]
    public void AppliedResourcesSearchAndFilterBeforePagingAndKeepEachApplicationIndependent()
    {
        using var culture = new TestCultureScope("fr-FR");
        var registered = new AppliedBootstrapVersion("base", "1.0.0", BootstrapProfileScope.Instance, "digest", DateTimeOffset.UnixEpoch, null);
        var rows = Enumerable.Range(1, 512).Select(index => new BootstrapAppliedResource("base", $"manifest.yaml document {index}",
            index == 512 ? "Flow" : "Parameter", $"resource-{index:D3}",
            index > 510 ? BootstrapResourceDisposition.Conflict : BootstrapResourceDisposition.Create)).ToArray();
        var latest = new ScopedBootstrapApplication(Guid.NewGuid(), Guid.NewGuid(), ResourceScopeRef.Instance, BootstrapApplicationSource.Manual,
            DateTimeOffset.UnixEpoch.AddMinutes(1), null, BootstrapApplicationStatus.Succeeded, [registered], [], rows);
        var previous = latest with { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UnixEpoch, Resources = [rows[0]] };
        var client = new StubClient
        {
            AppliedProfiles = [new("base", "base", "Local development", BootstrapProfileScope.Instance)],
            AppliedVersions = [registered],
            AppliedHistory = [latest, previous]
        };
        using var context = Context(client, application: false);
        var cut = context.Render<AppliedBootstrapProfiles>(parameters => parameters.Add(page => page.ProfileId, "base"));
        var grids = cut.FindComponents<BootstrapAppliedResources>();
        Assert.HasCount(2, grids);
        var grid = grids[0];
        const string resourceRow = "[data-testid='bootstrap-applied-resource-row']";

        Assert.HasCount(10, grid.FindAll(resourceRow));
        CollectionAssert.AreEqual(new[] { "10", "25", "50", "100" }, grid.FindAll(".collection-pager select option")
            .Select(option => option.GetAttribute("value")).ToArray());
        StringAssert.Contains(grid.Find(".collection-pager [role='status']").TextContent, "512");
        grid.Find(".collection-pager button[aria-label='Page 52']").Click();
        Assert.HasCount(2, grid.FindAll(resourceRow));
        StringAssert.Contains(grid.FindAll(resourceRow)[^1].TextContent, "resource-512");

        grid.Find("[data-filter-key='result'] select").Change("Conflict");
        Assert.HasCount(2, grid.FindAll(resourceRow));
        grid.Find("[data-filter-key='kind'] select").Change("Flow");
        StringAssert.Contains(grid.Find(resourceRow).TextContent, "resource-512");
        Assert.HasCount(1, grid.FindAll(resourceRow));
        grid.Find("input[type='search']").Input("resource-001");
        Assert.HasCount(0, grid.FindAll(resourceRow));
        grid.FindAll("button").Single(button => button.TextContent.Trim() == "Effacer les filtres").Click();
        Assert.HasCount(10, grid.FindAll(resourceRow));
        grid.Find(".collection-pager select").Change("50");
        Assert.HasCount(50, grid.FindAll(resourceRow));
        grid.Find("input[type='search']").Input("document 512");
        Assert.HasCount(1, grid.FindAll(resourceRow));
        StringAssert.Contains(grid.Find(resourceRow).TextContent, "resource-512");
        StringAssert.Contains(grids[1].Find(resourceRow).TextContent, "resource-001");
    }

    private static BunitContext Context(StubClient client, bool application = true)
    {
        var context = new BunitContext();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/collection-ui.js").Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/step-progress.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IBootstrapProfilesApiClient>(client);
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        if (application) context.Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/bootstrap?applicationId=retained-application");
        return context;
    }

    private sealed class StubClient : IBootstrapProfilesApiClient
    {
        private readonly BootstrapApplicationResource application = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = BootstrapKinds.BootstrapApplication,
            Metadata = new() { Name = "retained-application" },
            Definition = new()
            {
                Profiles = ["load-test"],
                Status = BootstrapApplicationStatus.Running,
                StartedAt = DateTimeOffset.UnixEpoch,
                TotalResources = 512,
                ResourcePlan = Enumerable.Range(1, 512).Select(index => new BootstrapPlannedResource("load-test", $"10-parameters.yaml#{index}", "Parameter", $"bootstrap-load-test-{index:D3}")).ToArray(),
                Resources = [new("load-test", "10-parameters.yaml#1", "Parameter", "bootstrap-load-test-001", BootstrapResourceDisposition.Create)],
                CurrentResource = new("load-test", "10-parameters.yaml#2", "Parameter", "bootstrap-load-test-002", BootstrapResourceDisposition.Create)
            }
        };
        public bool ExposeCatalog { get; init; }
        public bool MixedCatalog { get; init; }
        public IReadOnlyList<AppliedBootstrapProfile> AppliedProfiles { get; init; } = [];
        public IReadOnlyList<AppliedBootstrapVersion> AppliedVersions { get; init; } = [];
        public IReadOnlyList<ScopedBootstrapApplication> AppliedHistory { get; init; } = [];
        public (string ProfileId, string Version, ResourceScopeRef Scope) HistoryRequest { get; private set; }
        public BootstrapProfileSelection? LastSelection { get; private set; }
        public static Guid TenantId { get; } = Guid.NewGuid();
        public static Guid WorkspaceId { get; } = Guid.NewGuid();
        private static IReadOnlyList<BootstrapProfileSummary> MixedProfiles =>
        [
            CatalogProfile with { Topology = [new("Tenant", "future-tenant")] },
            new("tenant", "Tenant fixture", null, BootstrapProfileScope.Tenant, 1, 1, "tenant-digest", []) { Topology = [new("Workspace", "future-workspace")] },
            new("workspace", "Workspace fixture", null, BootstrapProfileScope.Workspace, 1, 1, "workspace-digest", [])
        ];
        public int Previews { get; private set; }
        private BootstrapApplicationResource? submitted;
        private static BootstrapProfileSummary CatalogProfile => new("base", "Base", "Fixture profile", BootstrapProfileScope.Instance, 1, 1, new string('a', 64), []);
        public bool DisconnectOnce { get; init; }
        public bool IncludeNewerApplication { get; init; }
        private BootstrapApplicationResource Newer => application with
        {
            Metadata = application.Metadata with { Name = "newer-application" },
            Definition = application.Definition with { StartedAt = DateTimeOffset.UnixEpoch.AddMinutes(1) }
        };
        public int Submissions { get; private set; }
        public int Observations { get; private set; }
        public string? CancelledId { get; private set; }
        public Task<BootstrapManagementView> GetAsync(CancellationToken cancellationToken) => Task.FromResult(
            new BootstrapManagementView(new(null, false, [], MixedCatalog ? MixedProfiles : ExposeCatalog ? [CatalogProfile] : []), MixedCatalog ? [new(TenantId, "current", "Current Tenant")] : [], MixedCatalog ? [new(TenantId, "current", "Current Tenant", WorkspaceId, "current", "Current Workspace")] : [], IncludeNewerApplication ? [Newer, application] : [application]));
        public Task<IReadOnlyList<AppliedBootstrapProfile>> ListAppliedProfilesAsync(CancellationToken cancellationToken) => Task.FromResult(AppliedProfiles);
        public Task<IReadOnlyList<AppliedBootstrapVersion>> ListVersionsAsync(string profileId, CancellationToken cancellationToken) => Task.FromResult(AppliedVersions);
        public Task<IReadOnlyList<ScopedBootstrapApplication>> ListHistoryAsync(string profileId, string version, ResourceScopeRef scope, CancellationToken cancellationToken)
        {
            HistoryRequest = (profileId, version, scope);
            return Task.FromResult(AppliedHistory);
        }
        public Task<BootstrapApplicationResource?> GetApplicationAsync(string applicationId, CancellationToken cancellationToken)
        {
            if (submitted?.Metadata.Name == applicationId) return Task.FromResult<BootstrapApplicationResource?>(submitted);
            if (applicationId == "newer-application") return Task.FromResult<BootstrapApplicationResource?>(Newer);
            Assert.AreEqual(application.Metadata.Name, applicationId);
            Observations++;
            if (DisconnectOnce && Observations == 2) throw new HttpRequestException("Synthetic disconnect");
            if (!DisconnectOnce || Observations == 1) return Task.FromResult<BootstrapApplicationResource?>(application);
            return Task.FromResult<BootstrapApplicationResource?>(application with
            {
                Definition = application.Definition with
                {
                    Status = BootstrapApplicationStatus.Succeeded,
                    CurrentResource = null,
                    Resources = [.. application.Definition.Resources,
                        new("load-test", "10-parameters.yaml#2", "Parameter", "bootstrap-load-test-002", BootstrapResourceDisposition.Skip)]
                }
            });
        }
        public Task<BootstrapApplicationResource> CancelAsync(string applicationId, CancellationToken cancellationToken)
        {
            CancelledId = applicationId;
            return Task.FromResult(application with { Definition = application.Definition with { CancellationRequested = true } });
        }
        public Task<BootstrapApplicationResource> ApplyAsync(BootstrapProfileSelection selection, string expectedDigest, CancellationToken cancellationToken, Guid? requestId = null)
        {
            Submissions++;
            submitted = application with { Metadata = application.Metadata with { Name = "submitted" }, Definition = new() { Profiles = selection.Profiles, Status = BootstrapApplicationStatus.Running } };
            return Task.FromResult(submitted);
        }
        public Task<BootstrapProfileSummary> GetSourceProfileAsync(BootstrapSourceProfileSelection selection, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<BootstrapBindingTargetOption>> GetBindingTargetsAsync(BootstrapBindingTargetsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BootstrapCompositionPreview> PreviewAsync(BootstrapProfileSelection selection, CancellationToken cancellationToken)
        {
            Previews++;
            LastSelection = selection;
            if (selection.Applications is { Count: > 0 } applications)
                return Task.FromResult(new BootstrapCompositionPreview(applications.Select(item => MixedProfiles.Single(profile => profile.Name == item.Profile)).ToArray(), BootstrapProfileScope.Instance, null, [], new string('a', 64),
                    applications.Select(item => new BootstrapResourcePreview(item.Profile, item.Profile + ".yaml", "Parameter", item.Profile, BootstrapResourceDisposition.Create)).ToArray())
                { Applications = applications.Select(item => new BootstrapProfileApplicationPreview(item.Profile, MixedProfiles.Single(profile => profile.Name == item.Profile).Scope, item.PlannedTarget?.TenantName ?? item.Target?.TenantId?.ToString() ?? "/instance", item.PlannedTarget is not null)).ToArray() });
            return Task.FromResult(new BootstrapCompositionPreview([CatalogProfile], BootstrapProfileScope.Instance, null, [], new string('a', 64),
                [new("base", "one.yaml", "Parameter", "one", BootstrapResourceDisposition.Create)]));
        }
    }
}
