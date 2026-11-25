using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using static Agentstration.Management.Tests.BootstrapApplicationTestFixture;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class WorkspaceSeedIngestionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task FullArchiveAndMinimalPointerRetainTheSamePinnedProfileWithoutAnExistingSourceOrProject()
    {
        var bytes = Archive(); var remote = new SeedHttp(bytes);
        await using var host = LoadTestFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient("workspace-seed-ingestion").ConfigurePrimaryHttpMessageHandler(() => remote)));
        var actor = await host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        using var http = host.CreateClient(); var target = new BootstrapApplicationTarget(actor.TenantId, actor.WorkspaceId);
        var request = new IngestWorkspaceSeedRequest(Guid.NewGuid(), target, Convert.ToBase64String(bytes));
        var full = await IngestAsync(http, request);
        var pointer = new WorkspaceSeedPointer(WorkspaceSeedFormat.Version, new("https://seed.example/content.zip"), Digest(bytes));
        var minimal = await IngestAsync(http, new(Guid.NewGuid(), target, Document: ResourceManifestSerializer.ToYaml(pointer)));
        Assert.AreEqual(full.Provenance.ArchiveDigest, minimal.Provenance.ArchiveDigest);
        Assert.AreEqual(full.Profile.Digest, minimal.Profile.Digest);
        Assert.AreEqual(JsonSerializer.Serialize(full.Manifest, Json), JsonSerializer.Serialize(minimal.Manifest, Json));
        Assert.AreEqual(1, remote.Calls);
        var replay = await IngestAsync(http, request); Assert.AreEqual(JsonSerializer.Serialize(full.Provenance, Json), JsonSerializer.Serialize(replay.Provenance, Json));
        var receiptPath = $"/api/bootstrap/workspaces/{actor.WorkspaceId:D}/seeds/{request.RequestId:D}?tenantId={actor.TenantId:D}";
        var saved = (await http.GetFromJsonAsync<WorkspaceSeedReceipt>(receiptPath))!;
        Assert.AreEqual(JsonSerializer.Serialize(full.Provenance, Json), JsonSerializer.Serialize(saved.Provenance, Json));
        var response = await http.GetStringAsync(receiptPath);
        Assert.DoesNotContain("storageKey", response); Assert.DoesNotContain(request.ArchiveBase64!, response);
        using var conflict = await http.PostAsJsonAsync("/api/bootstrap/workspace-seeds", request with { Document = "{}", ArchiveBase64 = null });
        Assert.AreEqual(HttpStatusCode.BadRequest, conflict.StatusCode);
        Assert.Contains("seed_request_conflict", await conflict.Content.ReadAsStringAsync());
        using var foreign = await http.GetAsync($"/api/bootstrap/workspaces/{Guid.NewGuid():D}/seeds/{request.RequestId:D}?tenantId={actor.TenantId:D}");
        Assert.AreEqual(HttpStatusCode.BadRequest, foreign.StatusCode);
        var pin = new WorkspaceSeedSelection(full.Provenance.Id, full.Provenance.ArchiveDigest);
        using var previewResponse = await http.PostAsJsonAsync("/api/bootstrap/profiles/preview", new BootstrapProfilePreviewRequest([], target) { Seed = pin });
        Assert.AreEqual(HttpStatusCode.OK, previewResponse.StatusCode, await previewResponse.Content.ReadAsStringAsync());
        var preview = (await previewResponse.Content.ReadFromJsonAsync<BootstrapCompositionPreview>())!;
        Assert.IsFalse(preview.CanApply); Assert.AreEqual(JsonSerializer.Serialize(full.Provenance, Json), JsonSerializer.Serialize(preview.SeedProvenance, Json));
        Assert.IsTrue(preview.SeedPrerequisites.Any(item => !item.Available && item.Kind == "Worker"));
        using var filesResponse = await http.PostAsJsonAsync("/api/bootstrap/profiles/files", new BootstrapProfileFilesRequest(full.Profile.Name,
            ExpectedDigest: full.Profile.Digest)
        { Seed = pin, Target = target });
        Assert.AreEqual(HttpStatusCode.OK, filesResponse.StatusCode, await filesResponse.Content.ReadAsStringAsync());
        Assert.HasCount(2, (await filesResponse.Content.ReadFromJsonAsync<BootstrapProfileFiles>())!.Files);
        using var nested = await http.PostAsJsonAsync("/api/bootstrap/profiles/files", new BootstrapProfileFilesRequest(full.Profile.Name,
            "connections/project.yaml", ExpectedDigest: full.Profile.Digest) { Seed = pin, Target = target });
        Assert.AreEqual(HttpStatusCode.OK, nested.StatusCode, await nested.Content.ReadAsStringAsync());
        Assert.Contains("ProjectSharing", (await nested.Content.ReadFromJsonAsync<BootstrapProfileFiles>())!.Yaml!);
        using var accepted = await http.PostAsJsonAsync("/api/bootstrap/applications", new ApplyBootstrapProfilesRequest([], preview.Digest, target,
            RequestId: Guid.NewGuid())
        { Seed = pin });
        Assert.AreEqual(HttpStatusCode.Accepted, accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
        var done = await WaitForApplicationAsync(http, (await accepted.Content.ReadFromJsonAsync<BootstrapApplicationResource>())!.Name);
        Assert.AreEqual(BootstrapApplicationStatus.Failed, done.Definition.Status);
        Assert.HasCount(0, done.Definition.Resources);
        Assert.IsTrue(done.Definition.SeedPrerequisites.Any(item => !item.Available));
        using var actorScope = host.Services.GetRequiredService<IRequestContextScopeFactory>().Push(actor);
        Assert.IsFalse((await host.Services.GetRequiredService<WorkProjectService>().FindAsync(new(actor.WorkspaceId), default, "fixture", default)).IsSuccess);
    }

    [TestMethod]
    [DataRow("instance", "seed_workspace_only")]
    [DataRow("tenant", "seed_workspace_only")]
    [DataRow("workspace", "seed_content_invalid")]
    public async Task MixedScopeOrUnsafeArchiveContentCannotBecomeAnApplicableSeed(string scope, string error)
    {
        await using var host = LoadTestFactory();
        var actor = await host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        using var http = host.CreateClient();
        var bytes = Archive(scope, traversal: scope == "workspace");
        using var response = await http.PostAsJsonAsync("/api/bootstrap/workspace-seeds",
            new IngestWorkspaceSeedRequest(Guid.NewGuid(), new(actor.TenantId, actor.WorkspaceId), Convert.ToBase64String(bytes)));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.Contains(error, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task RawSeedUrlResolvesOnlyExplicitContentAndRejectsUnsafeOriginsAndWrongIntegrity()
    {
        var remote = new SeedHttp(Archive());
        await using var host = LoadTestFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient("workspace-seed-ingestion").ConfigurePrimaryHttpMessageHandler(() => remote)));
        var actor = await host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        using var http = host.CreateClient(); var target = new BootstrapApplicationTarget(actor.TenantId, actor.WorkspaceId);
        _ = await IngestAsync(http, new(Guid.NewGuid(), target, DocumentUrl: new("https://seed.example/seed.yaml")));
        Assert.AreEqual(2, remote.Calls);
        foreach (var url in new[] { "http://127.0.0.1/content.zip", "https://127.0.0.1/content.zip", "https://seed.example/", "https://seed.example/content.zip?token=private" })
        {
            using var rejected = await http.PostAsJsonAsync("/api/bootstrap/workspace-seeds", new IngestWorkspaceSeedRequest(Guid.NewGuid(), target,
                Document: ResourceManifestSerializer.ToYaml(new WorkspaceSeedPointer(WorkspaceSeedFormat.Version, new(url), Digest(remote.Bytes)))));
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("seed_url_invalid", await rejected.Content.ReadAsStringAsync());
        }
        Assert.AreEqual(2, remote.Calls, "Unsafe URLs must fail before making a request.");
        using var mismatch = await http.PostAsJsonAsync("/api/bootstrap/workspace-seeds", new IngestWorkspaceSeedRequest(Guid.NewGuid(), target,
            Document: ResourceManifestSerializer.ToYaml(new WorkspaceSeedPointer(WorkspaceSeedFormat.Version, new("https://seed.example/content.zip"), new string('0', 64)))));
        Assert.AreEqual(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("seed_integrity_mismatch", await mismatch.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("../connections/project.yaml")]
    [DataRow("/connections/project.yaml")]
    [DataRow("missing.yaml")]
    [DataRow("profile.yaml")]
    public async Task ExplicitNestedCompositionRejectsUnsafeMissingAndDescriptorSelections(string selection)
    {
        await using var host = LoadTestFactory();
        var actor = await host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        using var http = host.CreateClient();
        using var response = await http.PostAsJsonAsync("/api/bootstrap/workspace-seeds", new IngestWorkspaceSeedRequest(Guid.NewGuid(),
            new(actor.TenantId, actor.WorkspaceId), Convert.ToBase64String(Archive(selectedResource: selection))));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("seed_profile_invalid", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("formatVersion: \"2.0\"\nformatVersion: \"2.0\"")]
    [DataRow("formatVersion: \"2.0\"\nunknown: value")]
    [DataRow("formatVersion: &version \"2.0\"")]
    public async Task SeedPointersUseTheStrictYamlContract(string document)
    {
        await using var host = LoadTestFactory();
        var actor = await host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        using var http = host.CreateClient();
        using var response = await http.PostAsJsonAsync("/api/bootstrap/workspace-seeds", new IngestWorkspaceSeedRequest(Guid.NewGuid(),
            new(actor.TenantId, actor.WorkspaceId), Document: document));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("seed_content_invalid", await response.Content.ReadAsStringAsync());
    }

    private static async Task<WorkspaceSeedReceipt> IngestAsync(HttpClient http, IngestWorkspaceSeedRequest request)
    {
        using var response = await http.PostAsJsonAsync("/api/bootstrap/workspace-seeds", request);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.IsNotNull(response.Headers.Location);
        return (await response.Content.ReadFromJsonAsync<WorkspaceSeedReceipt>())!;
    }

    private static byte[] Archive(string scope = "workspace", bool traversal = false, string selectedResource = "connections/project.yaml")
    {
        var manifest = new WorkspaceSeedManifest(WorkspaceSeedFormat.Version, "1.0.0", "fixture-v1", "workspace-coding", "bootstrap", new("fixture", default),
            new([new() { Kind = "Tool", ExtensionId = "Agentstration.Extensions.Git", ContributionId = "git.repository" }], [new("seed-model", ResourceScopeRef.Instance)], [new("coding-local", ResourceScopeRef.Instance)],
                new("coding-cli", "1.0", "1.0")));
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            Write(zip, WorkspaceSeedFormat.ManifestPath, ResourceManifestSerializer.ToYaml(manifest));
            Write(zip, "bootstrap/profile.yaml", $"apiVersion: agentstration.io/v1\nkind: BootstrapProfile\nmetadata:\n  name: workspace-coding\ndefinition:\n  targetScope: {scope}\n  resources:\n    - {JsonSerializer.Serialize(selectedResource)}\n");
            Write(zip, "bootstrap/connections/project.yaml", "apiVersion: agentstration.io/v1\nkind: ProjectSharing\nmetadata:\n  name: fixture\ndefinition:\n  projectId:\n    value: 04bf923e-0c0a-4c30-a013-43a9b6ed90a0\n  participantId:\n    value: b7d1e58a-4526-4f6a-9b92-e1577f1be3c8\n  location: shared\n  toolName: agentstration.fixture.v1.0.0\n");
            if (traversal) Write(zip, "../outside.yaml", "refused");
        }
        return memory.ToArray();
    }
    private static void Write(ZipArchive zip, string path, string value)
    { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(value); }
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed class SeedHttp(byte[] bytes) : HttpMessageHandler
    {
        internal byte[] Bytes => bytes;
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            if (request.RequestUri!.AbsolutePath == "/seed.yaml")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ResourceManifestSerializer.ToYaml(new WorkspaceSeedPointer(WorkspaceSeedFormat.Version, new("https://seed.example/content.zip"), Digest(bytes))), Encoding.UTF8, "application/yaml") });
            var content = new ByteArrayContent(bytes); content.Headers.ContentType = new("application/zip");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
