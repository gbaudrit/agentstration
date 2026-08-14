using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Infrastructure.Packs;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Storage.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class PackTests
{
    [TestMethod]
    public async Task ArchiveReaderRejectsParentTraversal()
    {
        await using var archive = CreateZip(new Dictionary<string, string>
        {
            ["pack.yaml"] = Manifest("../agent.yaml"),
            ["../agent.yaml"] = Resource("Agent", "unsafe")
        });

        var exception = await Assert.ThrowsExactlyAsync<PackValidationException>(() =>
            new ZipPackArchiveReader().ReadAsync(archive, "unsafe.zip", default));

        Assert.AreEqual("pack_archive_path_invalid", exception.Code);
    }

    [TestMethod]
    public async Task ArchiveReaderPreservesYamlScalarTypes()
    {
        await using var archive = CreateZip(new Dictionary<string, string>
        {
            ["pack.yaml"] = Manifest("profiles/runtime.yaml"),
            ["profiles/runtime.yaml"] = """
                apiVersion: agentstration.io/v1
                kind: RuntimeProfile
                metadata:
                  name: typed-runtime
                definition:
                  displayName: Typed Runtime
                  runtimeType: microsoft-agent-framework
                  enabled: true
                  retryCount: 3
                """
        });

        var parsed = await new ZipPackArchiveReader().ReadAsync(archive, "typed.zip", default);
        var definition = parsed.Resources.Single().Manifest.GetProperty("definition");

        Assert.AreEqual(JsonValueKind.True, definition.GetProperty("enabled").ValueKind);
        Assert.AreEqual(JsonValueKind.Number, definition.GetProperty("retryCount").ValueKind);
    }

    [TestMethod]
    public async Task FailedInstallationCompensatesAppliedResourcesAndRecordsFailure()
    {
        await using var fixture = await PackFixture.CreateAsync();
        var events = new List<string>();
        var first = new FakeHandler("First", 10, events);
        var second = new FakeHandler("Second", 20, events) { FailInstallation = true };
        var service = new PackManagementService(fixture.Store, [first, second], TimeProvider.System);
        var archive = Archive(Document("First", "one"), Document("Second", "two"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.InstallAsync(archive, default));

        CollectionAssert.AreEqual(new[] { "install:First/one", "install:Second/two", "delete:First/one" }, events);
        Assert.IsFalse(first.Exists);
        var failed = await service.GetAsync(new("agentstration", "test-pack"), default);
        Assert.IsNotNull(failed);
        Assert.AreEqual(InstalledPackState.Failed, failed.Value.Definition.State);
        Assert.HasCount(0, failed.Value.Definition.ManagedResources);
    }

    [TestMethod]
    public async Task UninstallPreservesResourceChangedAfterInstallation()
    {
        await using var fixture = await PackFixture.CreateAsync();
        var handler = new FakeHandler("First", 10, []);
        var service = new PackManagementService(fixture.Store, [handler], TimeProvider.System);
        await service.InstallAsync(Archive(Document("First", "one")), default);
        handler.CurrentToken = "locally-modified";

        await Assert.ThrowsExactlyAsync<PackResourceModifiedException>(() =>
            service.UninstallAsync(new("agentstration", "test-pack"), default));

        Assert.IsTrue(handler.Exists);
        var degraded = await service.GetAsync(new("agentstration", "test-pack"), default);
        Assert.IsNotNull(degraded);
        Assert.AreEqual(InstalledPackState.Degraded, degraded.Value.Definition.State);
    }

    [TestMethod]
    public async Task HttpApiInstallsListsAndUninstallsLocalPackResource()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        await using var archive = CreateZip(new Dictionary<string, string>
        {
            ["pack.yaml"] = Manifest("profiles/pack-runtime.yaml"),
            ["profiles/pack-runtime.yaml"] = """
                apiVersion: agentstration.io/v1
                kind: RuntimeProfile
                metadata:
                  name: pack-runtime
                definition:
                  displayName: Pack Runtime
                  runtimeType: microsoft-agent-framework
                """
        });
        var archiveBytes = archive.ToArray();
        using var previewContent = new ByteArrayContent(archiveBytes);
        previewContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        previewContent.Headers.Add("X-Pack-File-Name", "empty-pack.zip");
        using var previewed = await client.PostAsync("/api/packs/preview", previewContent);
        Assert.AreEqual(HttpStatusCode.OK, previewed.StatusCode);
        using var previewBody = JsonDocument.Parse(await previewed.Content.ReadAsStringAsync());
        Assert.IsTrue(previewBody.RootElement.GetProperty("canInstall").GetBoolean());
        Assert.AreEqual("pack-runtime", previewBody.RootElement.GetProperty("resources")[0].GetProperty("name").GetString());
        using var absentBeforeInstall = await client.GetAsync("/api/runtimeprofiles/pack-runtime");
        Assert.AreEqual(HttpStatusCode.NotFound, absentBeforeInstall.StatusCode);

        using var content = new ByteArrayContent(archiveBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Headers.Add("X-Pack-File-Name", "empty-pack.zip");

        using var installed = await client.PostAsync("/api/packs", content);
        Assert.AreEqual(HttpStatusCode.Created, installed.StatusCode);
        using var body = JsonDocument.Parse(await installed.Content.ReadAsStringAsync());
        Assert.AreEqual("test-pack", body.RootElement.GetProperty("definition").GetProperty("packName").GetString());
        Assert.AreEqual("installed", body.RootElement.GetProperty("definition").GetProperty("state").GetString());

        var listed = await client.GetFromJsonAsync<JsonElement[]>("/api/packs");
        Assert.IsNotNull(listed);
        Assert.IsTrue(listed.Any(value => value.GetProperty("definition").GetProperty("packName").GetString() == "test-pack"));
        using var resource = await client.GetAsync("/api/runtimeprofiles/pack-runtime");
        Assert.AreEqual(HttpStatusCode.OK, resource.StatusCode);

        using var removed = await client.DeleteAsync("/api/packs/agentstration/test-pack");
        Assert.AreEqual(HttpStatusCode.NoContent, removed.StatusCode);
        using var removedResource = await client.GetAsync("/api/runtimeprofiles/pack-runtime");
        Assert.AreEqual(HttpStatusCode.NotFound, removedResource.StatusCode);
        using var missing = await client.GetAsync("/api/packs/agentstration/test-pack");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public async Task WhoAmISamplePreviewsAndInstallsFiveRepresentativeResources()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var sampleDirectory = Path.Combine(repositoryRoot, "samples", "packs", "who-am-i");
        Assert.IsTrue(Directory.Exists(sampleDirectory), $"Sample Pack was not found at '{sampleDirectory}'.");
        await using var archive = CreateZipFromDirectory(sampleDirectory);
        var bytes = archive.ToArray();

        using var previewContent = ArchiveContent(bytes, "who-am-i.pack.zip");
        using var previewResponse = await client.PostAsync("/api/packs/preview", previewContent);
        Assert.AreEqual(HttpStatusCode.OK, previewResponse.StatusCode, await previewResponse.Content.ReadAsStringAsync());
        var preview = await previewResponse.Content.ReadFromJsonAsync<PackInstallationPreview>();
        Assert.IsNotNull(preview);
        Assert.IsTrue(preview.CanInstall);
        Assert.HasCount(5, preview.Resources);
        CollectionAssert.AreEquivalent(
            new[] { ResourceKinds.Agent, ResourceKinds.Agent, ResourceKinds.Agent, ResourceKinds.Flow, ResourceKinds.Entry },
            preview.Resources.Select(resource => resource.Kind).ToArray());

        using var installContent = ArchiveContent(bytes, "who-am-i.pack.zip");
        using var installResponse = await client.PostAsync("/api/packs", installContent);
        Assert.AreEqual(HttpStatusCode.Created, installResponse.StatusCode, await installResponse.Content.ReadAsStringAsync());
        var installed = await installResponse.Content.ReadFromJsonAsync<InstalledPackResource>();
        Assert.IsNotNull(installed);
        Assert.AreEqual(InstalledPackState.Installed, installed.Definition.State);
        Assert.HasCount(5, installed.Definition.ManagedResources);
        Assert.IsNotNull(installed.Definition.SourceArtifact);

        using var flowVersionResponse = await client.GetAsync("/api/flows/who-am-i-game/versions/0.1.0");
        Assert.AreEqual(HttpStatusCode.OK, flowVersionResponse.StatusCode, await flowVersionResponse.Content.ReadAsStringAsync());
        using var flowVersion = JsonDocument.Parse(await flowVersionResponse.Content.ReadAsStreamAsync());
        var graph = flowVersion.RootElement.GetProperty("graph");
        Assert.AreEqual("input", graph.GetProperty("entryStep").GetString());
        Assert.AreEqual(3, graph.GetProperty("steps").GetArrayLength());

        using var forkResponse = await client.PostAsJsonAsync(
            "/api/packs/agentstration/who-am-i/fork",
            new ForkPackCommand("local", "who-am-i-lab", "0.1.0-dev.1", "Who Am I? Lab", "Locally forked sample."));
        Assert.AreEqual(HttpStatusCode.Created, forkResponse.StatusCode, await forkResponse.Content.ReadAsStringAsync());
        var project = await forkResponse.Content.ReadFromJsonAsync<PackProjectResource>();
        Assert.IsNotNull(project);
        Assert.AreEqual("agentstration", project.Definition.Origin.Publisher);

        using var buildResponse = await client.PostAsync($"/api/pack-projects/{project.Uid:D}/builds", null);
        Assert.AreEqual(HttpStatusCode.Created, buildResponse.StatusCode, await buildResponse.Content.ReadAsStringAsync());
        var build = await buildResponse.Content.ReadFromJsonAsync<PackProjectBuildResource>();
        Assert.IsNotNull(build);
        Assert.AreEqual("0.1.0-dev.1", build.Definition.Version);

        using var conflictingPreviewResponse = await client.PostAsync($"/api/pack-projects/{project.Uid:D}/builds/{build.Uid:D}/preview", null);
        Assert.AreEqual(HttpStatusCode.OK, conflictingPreviewResponse.StatusCode);
        var conflictingPreview = await conflictingPreviewResponse.Content.ReadFromJsonAsync<PackInstallationPreview>();
        Assert.IsNotNull(conflictingPreview);
        Assert.IsFalse(conflictingPreview.CanInstall, "The fork keeps resource names and must conflict with its installed origin in the same Workspace.");

        using var removeResponse = await client.DeleteAsync("/api/packs/agentstration/who-am-i");
        Assert.AreEqual(HttpStatusCode.NoContent, removeResponse.StatusCode, await removeResponse.Content.ReadAsStringAsync());

        using var localInstallResponse = await client.PostAsync($"/api/pack-projects/{project.Uid:D}/builds/{build.Uid:D}/install", null);
        Assert.AreEqual(HttpStatusCode.Created, localInstallResponse.StatusCode, await localInstallResponse.Content.ReadAsStringAsync());
        var localInstallation = await localInstallResponse.Content.ReadFromJsonAsync<InstalledPackResource>();
        Assert.IsNotNull(localInstallation);
        Assert.AreEqual("local", localInstallation.Definition.Publisher);
        Assert.AreEqual("who-am-i-lab", localInstallation.Definition.PackName);

        using var downloadResponse = await client.GetAsync($"/api/pack-projects/{project.Uid:D}/builds/{build.Uid:D}/download");
        Assert.AreEqual(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.AreEqual("application/zip", downloadResponse.Content.Headers.ContentType?.MediaType);
        Assert.IsGreaterThan(0, (await downloadResponse.Content.ReadAsByteArrayAsync()).Length);

        using var removeLocalResponse = await client.DeleteAsync("/api/packs/local/who-am-i-lab");
        Assert.AreEqual(HttpStatusCode.NoContent, removeLocalResponse.StatusCode);
    }

    private static PackArchive Archive(params PackResourceDocument[] resources) => new(
        new PackManifest
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = PackKinds.Pack,
            Metadata = new PackMetadata { Publisher = "agentstration", Name = "test-pack", Version = "1.0.0" },
            Spec = new PackSpec { Resources = resources.Select(value => value.Path).ToArray() }
        }, resources, "test.pack.zip");

    private static PackResourceDocument Document(string kind, string name)
    {
        var document = JsonSerializer.SerializeToElement(new
        {
            apiVersion = ManagementApiVersions.CoreV1,
            kind,
            metadata = new { name },
            definition = new { }
        });
        return new($"resources/{name}.json", ManagementApiVersions.CoreV1, kind, name, document);
    }

    private static MemoryStream CreateZip(IReadOnlyDictionary<string, string> files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Key);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(file.Value);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateZipFromDirectory(string directory)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(directory, path).Replace('\\', '/');
                var entry = archive.CreateEntry(relativePath);
                using var input = File.OpenRead(path);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static ByteArrayContent ArchiveContent(byte[] bytes, string fileName)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Headers.Add("X-Pack-File-Name", fileName);
        return content;
    }

    private static string Manifest(params string[] resources) => $$"""
        apiVersion: agentstration.io/v1
        kind: Pack
        metadata:
          name: test-pack
          publisher: agentstration
          version: 1.0.0
        spec:
          resources: [{{string.Join(", ", resources)}}]
        """;

    private static string Resource(string kind, string name) => $$"""
        apiVersion: agentstration.io/v1
        kind: {{kind}}
        metadata:
          name: {{name}}
        definition: {}
        """;

    private sealed class FakeHandler(string kind, int order, List<string> events) : IPackResourceHandler
    {
        public string Kind { get; } = kind;
        public int InstallOrder { get; } = order;
        public bool FailInstallation { get; init; }
        public bool Exists { get; private set; }
        public string CurrentToken { get; set; } = "v1";

        public Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) => Task.FromResult(Exists);
        public Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, string packVersion, CancellationToken cancellationToken)
        {
            events.Add($"install:{resource.Kind}/{resource.Name}");
            if (FailInstallation) throw new InvalidOperationException("simulated failure");
            Exists = true;
            return Task.FromResult(new ManagedPackResource { Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = CurrentToken });
        }
        public Task<string?> GetVersionTokenAsync(string name, CancellationToken cancellationToken) => Task.FromResult(Exists ? CurrentToken : null);
        public Task DeleteAsync(ManagedPackResource resource, CancellationToken cancellationToken)
        {
            events.Add($"delete:{resource.Kind}/{resource.Name}"); Exists = false; return Task.CompletedTask;
        }
    }

    private sealed class PackFixture(string directory, ServiceProvider provider, IControlPlaneStore store) : IAsyncDisposable
    {
        public IControlPlaneStore Store { get; } = store;
        public static async Task<PackFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "agentstration-pack-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var provider = new ServiceCollection()
                .AddSingleton(TimeProvider.System)
                .AddSingleton<ICurrentRequestContext, SystemOperationRequestContext>()
                .AddSqliteControlPlane($"Data Source={Path.Combine(directory, "management.db")};Pooling=False")
                .BuildServiceProvider();
            var store = provider.GetRequiredService<IControlPlaneStore>();
            await store.InitializeAsync(default);
            return new(directory, provider, store);
        }
        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync(); SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
