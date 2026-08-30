using System.Globalization;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PacksComponentTests
{
    private CultureInfo? originalCulture;
    private CultureInfo? originalUiCulture;

    [TestInitialize]
    public void SetEnglishCulture()
    {
        originalCulture = CultureInfo.CurrentCulture;
        originalUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    }

    [TestCleanup]
    public void RestoreCulture()
    {
        CultureInfo.CurrentCulture = originalCulture!;
        CultureInfo.CurrentUICulture = originalUiCulture!;
    }

    [TestMethod]
    public async Task PageSummarizesAndInspectsInstalledPacks()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());

        var rendered = context.Render<Packs>();
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Starter Pack", StringComparison.Ordinal)));

        CollectionAssert.AreEqual(new[] { "1", "2", "0", "0" }, rendered.FindAll(".metric-card strong").Select(element => element.TextContent).ToArray());
        await rendered.FindAll("button").Single(button => button.TextContent.Contains("Inspect", StringComparison.Ordinal)).ClickAsync(new());
        Assert.IsTrue(rendered.Markup.Contains("Managed resources", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("reasoning-default", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("default/openai-production", StringComparison.Ordinal));
        Assert.AreEqual("/namespaces/agentstration.starter/agents/assistant?view=definition", rendered.FindAll("a").Single(link => link.TextContent.Trim() == "Form").GetAttribute("href"));
    }

    [TestMethod]
    public void PageSelectsThePackRequestedByResourceNavigation()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/packs?publisher=agentstration&name=starter");

        var rendered = context.Render<Packs>();

        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll("tr.selected-pack"));
            Assert.IsTrue(rendered.Markup.Contains("default/openai-production", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public async Task InstallActionOpensSideEffectFreeArchivePreviewStep()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());
        var rendered = context.Render<Packs>();
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Starter Pack", StringComparison.Ordinal)));

        await rendered.FindAll("button").First(button => button.TextContent.Contains("Install local Pack", StringComparison.Ordinal)).ClickAsync(new());

        Assert.IsTrue(rendered.Markup.Contains("The archive is validated and previewed before any resource is created.", StringComparison.Ordinal));
        Assert.AreEqual("dialog", rendered.Find(".pack-dialog").GetAttribute("role"));
    }

    [TestMethod]
    public async Task ArchiveInstallOffersExplicitReplacementForAnInstalledPackIdentity()
    {
        using var context = CreateContext();
        var client = new FakePacksClient
        {
            PreviewResult = new PackInstallationPreview(
                new PackMetadata
                {
                    Publisher = "agentstration",
                    Name = "starter",
                    Version = "1.1.0",
                    DisplayName = "Starter Pack"
                },
                [new("agents/assistant.yaml", ResourceKinds.Agent, "assistant", true)],
                true)
        };
        context.Services.AddSingleton<IPacksClient>(client);
        var rendered = context.Render<Packs>();
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Starter Pack", StringComparison.Ordinal)));

        await rendered.FindAll("button").First(button => button.TextContent.Contains("Install local Pack", StringComparison.Ordinal)).ClickAsync(new());
        rendered.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary([1, 2, 3], "starter-1.1.0.pack.zip", contentType: "application/zip"));

        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Replace the installed Pack", StringComparison.Ordinal)));
        var install = rendered.FindAll("button").Single(button => button.TextContent.Contains("Install Pack", StringComparison.Ordinal));
        Assert.IsTrue(install.HasAttribute("disabled"));

        await rendered.Find(".pack-reinstall-option input").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        install = rendered.FindAll("button").Single(button => button.TextContent.Contains("Install Pack", StringComparison.Ordinal));
        Assert.IsFalse(install.HasAttribute("disabled"));
        await install.ClickAsync(new());

        Assert.AreEqual(true, client.LastReplaceExisting);
    }

    [TestMethod]
    public async Task LegacyPackForkPromptsForItsOriginalSourceArchive()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());
        var rendered = context.Render<Packs>();
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Starter Pack", StringComparison.Ordinal)));

        await rendered.FindAll("button").Single(button => button.TextContent.Contains("Inspect", StringComparison.Ordinal)).ClickAsync(new());
        await rendered.FindAll("button").Single(button => button.TextContent.Trim().Equals("Fork", StringComparison.Ordinal)).ClickAsync(new());

        Assert.IsTrue(rendered.Markup.Contains("Source archive required", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Select original Pack ZIP", StringComparison.Ordinal));
        Assert.IsFalse(rendered.FindAll("button").Any(button => button.TextContent.Contains("Create Pack Project", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ComposerMovesResourcesAndReviewsDependenciesAutomatically()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());
        var rendered = context.Render<PackComposer>();
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Daily Assistant", StringComparison.Ordinal)));

        Assert.IsFalse(rendered.FindAll("button").Any(button => button.TextContent.Contains("Review selection", StringComparison.Ordinal)));
        await rendered.Find(".composer-add").ClickAsync(new());

        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Required dependency", StringComparison.Ordinal)));
        Assert.AreEqual("1", rendered.FindAll(".composer-summary strong")[0].TextContent);
        Assert.IsTrue(rendered.FindAll(".composer-selected-resource").Any(resource => resource.TextContent.Contains("Daily Assistant", StringComparison.Ordinal)));
        Assert.IsTrue(rendered.Markup.Contains("model-reasoning", StringComparison.Ordinal));
        Assert.IsTrue(rendered.FindAll("button").Any(button => button.GetAttribute("aria-label") == "Add Reasoning to Pack"));

        await rendered.Find(".composer-remove").ClickAsync(new());
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Add resources from the workspace inventory", StringComparison.Ordinal)));
        Assert.IsTrue(rendered.FindAll(".composer-resource").Any(resource => resource.TextContent.Contains("Daily Assistant", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ComposerExplainsWhenRemovingASelectionLeavesARequiredDependency()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());
        var rendered = context.Render<PackComposer>();
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Daily Assistant", StringComparison.Ordinal)));

        await rendered.FindAll("button").Single(button => button.GetAttribute("aria-label") == "Add Concierge to Pack").ClickAsync(new());
        await rendered.FindAll("button").Single(button => button.GetAttribute("aria-label") == "Add Daily Assistant to Pack").ClickAsync(new());

        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Required by Entry Daily Assistant → Flow Main", StringComparison.Ordinal)));
        var demote = rendered.FindAll("button").Single(button => button.TextContent.Contains("Leave as dependency", StringComparison.Ordinal));
        await demote.ClickAsync(new());

        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Find(".composer-content-group.automatic").TextContent.Contains("Concierge", StringComparison.Ordinal)));
        Assert.IsFalse(rendered.FindAll(".composer-resource").Any(resource => resource.TextContent.Contains("Concierge", StringComparison.Ordinal)));
        Assert.IsTrue(rendered.FindAll("button").Any(button => button.GetAttribute("aria-label") == "Keep Concierge explicitly in Pack"));

        await rendered.FindAll("button").Single(button => button.GetAttribute("aria-label") == "Keep Concierge explicitly in Pack").ClickAsync(new());
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Find(".composer-content-group:not(.automatic)").TextContent.Contains("Concierge", StringComparison.Ordinal)));
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }

    private sealed class FakePacksClient : IPacksClient
    {
        public PackInstallationPreview? PreviewResult { get; init; }
        public bool? LastReplaceExisting { get; private set; }

        private readonly InstalledPackResource pack = new()
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.InstalledPack,
            Metadata = new ResourceMetadata { Name = "13-agentstration-starter" },
            Definition = new InstalledPackProperties
            {
                Publisher = "agentstration",
                PackName = "starter",
                Version = "1.0.0",
                DisplayName = "Starter Pack",
                Source = "starter.pack.zip",
                InstalledAt = new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero),
                State = InstalledPackState.Installed,
                Bindings =
                [
                    new PackBindingResolution("credential", PackBindingTargetKind.Secret, new("openai-production", @namespace: Agentstration.Resources.ResourceNamespace.Default))
                ],
                ManagedResources =
                [
                    new ManagedPackResource { Namespace = new ResourceNamespace("agentstration.starter"), Kind = ResourceKinds.ModelProfile, Name = "reasoning-default", Path = "profiles/reasoning.yaml", VersionToken = "v1" },
                    new ManagedPackResource { Namespace = new ResourceNamespace("agentstration.starter"), Kind = ResourceKinds.Agent, Name = "assistant", Path = "agents/assistant.yaml", VersionToken = "v1" }
                ]
            }
        };

        public Task<IReadOnlyList<InstalledPackResource>> GetPacksAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<InstalledPackResource>>([pack]);
        public Task<ResourceSnapshot<InstalledPackResource>> GetPackAsync(string publisher, string name, CancellationToken cancellationToken) => Task.FromResult(new ResourceSnapshot<InstalledPackResource>(pack, "\"etag-1\""));
        public Task<PackInstallationPreview> PreviewAsync(byte[] archive, string fileName, CancellationToken cancellationToken) =>
            Task.FromResult(PreviewResult ?? throw new NotSupportedException());
        public Task<ResourceSnapshot<InstalledPackResource>> InstallAsync(byte[] archive, string fileName, bool replaceExisting, bool removeDashboardReferences, IReadOnlyList<PackBindingSelection> bindings, CancellationToken cancellationToken)
        {
            LastReplaceExisting = replaceExisting;
            return Task.FromResult(new ResourceSnapshot<InstalledPackResource>(pack, "\"etag-2\""));
        }
        public Task UninstallAsync(string publisher, string name, string etag, bool removeDashboardReferences, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<InstalledPackResource>> AttachSourceAsync(string publisher, string name, byte[] archive, string fileName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<PackProjectResource>> ForkAsync(string publisher, string name, ForkPackCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PackCompositionCatalogItem>> GetCompositionResourcesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PackCompositionCatalogItem>>
        ([
            new() { Resource = new(ResourceKinds.Entry, "daily-assistant"), DisplayName = "Daily Assistant", Status = "Published" },
            new() { Resource = new(ResourceKinds.Agent, "concierge"), DisplayName = "Concierge", Status = "Accepted" },
            new() { Resource = new(ResourceKinds.ModelProfile, "reasoning"), DisplayName = "Reasoning" }
        ]);
        public Task<PackCompositionPreview> PreviewCompositionAsync(PreviewPackCompositionCommand command, CancellationToken cancellationToken)
        {
            var entrySelected = command.Resources.Any(resource => resource.Kind == ResourceKinds.Entry && resource.Name == "daily-assistant");
            var agentSelected = command.Resources.Any(resource => resource.Kind == ResourceKinds.Agent && resource.Name == "concierge");
            var included = new List<PackCompositionPreviewResource>();
            if (entrySelected)
            {
                included.Add(new(
                    new(ResourceKinds.Entry, "daily-assistant"),
                    "Daily Assistant",
                    "entries/daily-assistant.json",
                    true,
                    [new() { Target = new(ResourceKinds.Flow, "main"), Relationship = "flow" }]));
                included.Add(new(
                    new(ResourceKinds.Flow, "main"),
                    "Main",
                    "flows/main.json",
                    false,
                    [new() { Target = new(ResourceKinds.Agent, "concierge"), Relationship = "graphAgent" }]));
            }
            if (entrySelected || agentSelected)
            {
                included.Add(new(
                    new(ResourceKinds.Agent, "concierge"),
                    "Concierge",
                    "agents/concierge.json",
                    agentSelected,
                    [new()
                    {
                        Target = new(ResourceKinds.ModelProfile, "reasoning"),
                        Relationship = "modelProfile",
                        Mode = PackCompositionDependencyMode.Binding,
                        BindingTargetKind = PackBindingTargetKind.ModelProfile
                    }]));
            }
            IReadOnlyList<PackCompositionPreviewBinding> bindings = included.Any(resource => resource.Resource.Kind == ResourceKinds.Agent)
                ? [new("model-reasoning", PackBindingTargetKind.ModelProfile, "Reasoning", new(ResourceKinds.ModelProfile, "reasoning"), [new(ResourceKinds.Agent, "concierge")])]
                : [];
            return Task.FromResult(new PackCompositionPreview(included, bindings, []));
        }
        public Task<ResourceSnapshot<PackProjectResource>> CreateProjectFromWorkspaceAsync(CreatePackProjectFromWorkspaceCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PackProjectResource>> GetProjectsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PackProjectResource>>([]);
        public Task<ResourceSnapshot<PackProjectResource>> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<PackProjectResource>> UpdateProjectAsync(Guid projectId, UpdatePackProjectCommand command, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PackProjectBuildResource> BuildAsync(Guid projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PackProjectBuildResource>> GetBuildsAsync(Guid projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PackInstallationPreview> PreviewBuildAsync(Guid projectId, Guid buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<InstalledPackResource>> InstallBuildAsync(Guid projectId, Guid buildId, bool replaceExisting, IReadOnlyList<PackBindingSelection> bindings, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
