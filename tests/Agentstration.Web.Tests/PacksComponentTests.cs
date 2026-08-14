using Agentstration.Management.Abstractions;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class PacksComponentTests
{
    [TestMethod]
    public async Task PageSummarizesAndInspectsInstalledPacks()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());

        var rendered = context.Render<Packs>();
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Starter Pack", StringComparison.Ordinal)));

        CollectionAssert.AreEqual(new[] { "1", "2", "0", "0" }, rendered.FindAll(".metric-card strong").Select(element => element.TextContent).ToArray());
        await rendered.FindAll("button").Single(button => button.TextContent.Contains("Inspect", StringComparison.Ordinal)).ClickAsync(new());
        Assert.IsTrue(rendered.Markup.Contains("Managed resources", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("reasoning-default", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task InstallActionOpensSideEffectFreeArchivePreviewStep()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());
        var rendered = context.Render<Packs>();
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Starter Pack", StringComparison.Ordinal)));

        await rendered.FindAll("button").First(button => button.TextContent.Contains("Install local Pack", StringComparison.Ordinal)).ClickAsync(new());

        Assert.IsTrue(rendered.Markup.Contains("The archive is validated and previewed before any resource is created.", StringComparison.Ordinal));
        Assert.AreEqual("dialog", rendered.Find(".pack-dialog").GetAttribute("role"));
    }

    [TestMethod]
    public async Task LegacyPackForkPromptsForItsOriginalSourceArchive()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());
        var rendered = context.Render<Packs>();
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Starter Pack", StringComparison.Ordinal)));

        await rendered.FindAll("button").Single(button => button.TextContent.Contains("Inspect", StringComparison.Ordinal)).ClickAsync(new());
        await rendered.FindAll("button").Single(button => button.TextContent.Trim().Equals("Fork", StringComparison.Ordinal)).ClickAsync(new());

        Assert.IsTrue(rendered.Markup.Contains("Source archive required", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Select original Pack ZIP", StringComparison.Ordinal));
        Assert.IsFalse(rendered.FindAll("button").Any(button => button.TextContent.Contains("Create Pack Project", StringComparison.Ordinal)));
    }

    private sealed class FakePacksClient : IPacksClient
    {
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
                ManagedResources =
                [
                    new ManagedPackResource { Kind = ResourceKinds.ModelProfile, Name = "reasoning-default", Path = "profiles/reasoning.yaml", VersionToken = "v1" },
                    new ManagedPackResource { Kind = ResourceKinds.Agent, Name = "assistant", Path = "agents/assistant.yaml", VersionToken = "v1" }
                ]
            }
        };

        public Task<IReadOnlyList<InstalledPackResource>> GetPacksAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<InstalledPackResource>>([pack]);
        public Task<ResourceSnapshot<InstalledPackResource>> GetPackAsync(string publisher, string name, CancellationToken cancellationToken) => Task.FromResult(new ResourceSnapshot<InstalledPackResource>(pack, "\"etag-1\""));
        public Task<PackInstallationPreview> PreviewAsync(byte[] archive, string fileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<InstalledPackResource>> InstallAsync(byte[] archive, string fileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UninstallAsync(string publisher, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<InstalledPackResource>> AttachSourceAsync(string publisher, string name, byte[] archive, string fileName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<PackProjectResource>> ForkAsync(string publisher, string name, ForkPackCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PackProjectResource>> GetProjectsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PackProjectResource>>([]);
        public Task<ResourceSnapshot<PackProjectResource>> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<PackProjectResource>> UpdateProjectAsync(Guid projectId, UpdatePackProjectCommand command, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PackProjectBuildResource> BuildAsync(Guid projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PackProjectBuildResource>> GetBuildsAsync(Guid projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PackInstallationPreview> PreviewBuildAsync(Guid projectId, Guid buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<InstalledPackResource>> InstallBuildAsync(Guid projectId, Guid buildId, bool replaceExisting, bool replaceOrigin, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
