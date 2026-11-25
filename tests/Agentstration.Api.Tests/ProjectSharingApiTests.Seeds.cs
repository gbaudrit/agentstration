using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Application.Work;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Testing;
using Agentstration.Tools;
using Agentstration.Work;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ProjectSharingApiTests
{
    [TestMethod]
    public async Task SeedCreatesProtectedConnectionAndCodingResourcesThenSynchronizesAndRetriesAfterRestartWithoutReplacingLocalEdits()
    {
        await using var native = await AuthenticatedGitFixture.CreateAsync();
        await using var host = await ConnectedHostAsync(native, "seed", publishRelay: false);
        var target = new BootstrapApplicationTarget(host.Context.TenantId, host.Context.WorkspaceId);
        var participant = Guid.NewGuid();
        var import = await SeedSharedPlanAsync(native, participant);
        var bytes = SeedArchive(native, host.Tool, import);
        using (host.Scope()) await SeedPrerequisitesAsync(host);
        using var ingestion = await host.Http.PostAsJsonAsync("/api/bootstrap/workspace-seeds", new IngestWorkspaceSeedRequest(Guid.NewGuid(), target, Convert.ToBase64String(bytes)));
        Assert.AreEqual(HttpStatusCode.Created, ingestion.StatusCode, await ingestion.Content.ReadAsStringAsync());
        var receipt = (await ingestion.Content.ReadFromJsonAsync<WorkspaceSeedReceipt>())!;
        ((HandlerFactory)host.Host.Services.GetRequiredService<IHttpMessageHandlerFactory>()).SeedContent = bytes;
        var pointer = new WorkspaceSeedPointer("1.0", new("https://seed.example/content.zip"), receipt.Provenance.ArchiveDigest);
        using var minimalResponse = await host.Http.PostAsJsonAsync("/api/bootstrap/workspace-seeds", new IngestWorkspaceSeedRequest(Guid.NewGuid(), target, Document: JsonSerializer.Serialize(pointer, Json)));
        Assert.AreEqual(HttpStatusCode.Created, minimalResponse.StatusCode, await minimalResponse.Content.ReadAsStringAsync());
        var minimal = (await minimalResponse.Content.ReadFromJsonAsync<WorkspaceSeedReceipt>())!;
        Assert.AreEqual(receipt.Profile.Digest, minimal.Profile.Digest);
        Assert.AreEqual(receipt.Provenance.ArchiveDigest, minimal.Provenance.ArchiveDigest);
        var selection = new WorkspaceSeedSelection(receipt.Provenance.Id, receipt.Provenance.ArchiveDigest);
        var inputs = new BootstrapInputSelection("seed", "Agentstration:Bootstrap:Parameters:ParticipantId", "literal", Literal: JsonSerializer.SerializeToElement(participant.ToString()));
        var preview = await SeedPreviewAsync(host, target, selection, inputs);
        Assert.IsTrue(preview.CanApply, JsonSerializer.Serialize(preview, Json));
        Assert.IsTrue(preview.SeedPrerequisites.All(item => item.Available));
        await SeedModelGrantAsync(host, false);
        var denied = await SeedPreviewAsync(host, target, selection, inputs);
        Assert.IsFalse(denied.CanApply);
        Assert.IsTrue(denied.SeedPrerequisites.Any(item => item.Kind == "ModelProvider" && item.ReasonCode == "seed_prerequisite_access_denied"));
        using var stale = await host.Http.PostAsJsonAsync("/api/bootstrap/applications", new ApplyBootstrapProfilesRequest([], preview.Digest, target,
            RequestId: Guid.NewGuid(), Inputs: [inputs])
        { Seed = selection });
        Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode, await stale.Content.ReadAsStringAsync());
        await SeedModelGrantAsync(host, true);
        preview = await SeedPreviewAsync(host, target, selection, inputs);
        using (host.Scope())
        {
            Assert.IsFalse((await host.Host.Services.GetRequiredService<WorkProjectService>().FindAsync(new(host.Context.WorkspaceId), default, "seed-project", default)).IsSuccess);
            Assert.IsNull(await host.Host.Services.GetRequiredService<ToolDefinitionService>().GetExactAsync("seed-sharing", default, ResourceScopeRef.Workspace(host.Context.WorkspaceId), default));
        }
        // Native heartbeat freshness does not change the reviewed configuration digest.
        var workers = host.Host.Services.GetRequiredService<RuntimeWorkerDispatchService>(); var worker = workers.ListRegistrations().Single();
        workers.Touch(worker.WorkerId, worker.WorkerSessionId);
        Assert.AreEqual(preview.Digest, (await SeedPreviewAsync(host, target, selection, inputs)).Digest);
        native.Unavailable = true;
        var partial = await ApplySeedAsync(host, target, selection, inputs, preview.Digest);
        Assert.AreEqual(BootstrapApplicationStatus.PartiallyApplied, partial.Definition.Status, JsonSerializer.Serialize(partial, Json));
        Assert.AreEqual(WorkspaceSeedHandoverStatus.Failed, partial.Definition.SeedHandover?.Status);
        WorkProjectSnapshot project;
        using (host.Scope())
        {
            var projects = host.Host.Services.GetRequiredService<WorkProjectService>();
            project = (await projects.FindAsync(new(host.Context.WorkspaceId), default, "seed-project", default)).Value!;
            var edit = await projects.UpdateAsync(project.WorkspaceId, project.Id, new(Guid.NewGuid(), project.Version, project.Definition with { DisplayName = "Operator edit" }), default);
            Assert.IsTrue(edit.IsSuccess, edit.ErrorCode);
        }
        native.Unavailable = false;
        await host.RestartAsync();
        using (host.Scope()) await RegisterSeedWorkerAsync(host);
        selection = new(minimal.Provenance.Id, minimal.Provenance.ArchiveDigest);
        var rerun = await SeedPreviewAsync(host, target, selection, inputs);
        Assert.IsTrue(rerun.CanApply, JsonSerializer.Serialize(rerun, Json));
        Assert.IsTrue(rerun.Resources.All(item => item.Disposition == BootstrapResourceDisposition.Skip));
        var applied = await ApplySeedAsync(host, target, selection, inputs, rerun.Digest);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, applied.Definition.Status, JsonSerializer.Serialize(applied, Json));
        Assert.AreEqual(JsonSerializer.Serialize(minimal.Provenance, Json), JsonSerializer.Serialize(applied.Definition.SeedProvenance, Json));
        Assert.AreEqual(project.Id.Value, applied.Definition.SeedHandover!.ProjectId);
        Assert.AreEqual(WorkspaceSeedHandoverStatus.Succeeded, applied.Definition.SeedHandover.Status);
        Assert.IsNotNull(applied.Definition.SeedHandover.Revision);
        CollectionAssert.AreEqual(new[] { import.PlanId }, applied.Definition.SeedHandover.ImportedPlans.ToArray());
        using (host.Scope())
        {
            var loaded = (await host.Host.Services.GetRequiredService<WorkProjectService>().GetAsync(project.WorkspaceId, project.Id, default)).Value!;
            Assert.AreEqual("Operator edit", loaded.Definition.DisplayName);
            CollectionAssert.AreEqual(new[] { new WorkPlanId(import.PlanId) }, loaded.Definition.PlanIds.ToArray());
            var imported = await host.Host.Services.GetRequiredService<WorkPlanService>().GetAsync(project.WorkspaceId, new(import.PlanId), default);
            Assert.IsTrue(imported.IsSuccess, imported.ErrorCode); Assert.AreEqual(WorkPlanStatus.Draft, imported.Value!.Plan.Status);
            Assert.HasCount(0, imported.Value.Plan.Attempts);
            Assert.AreEqual(participant, loaded.Definition.Sharing!.ParticipantId.Value);
            var versions = await host.Host.Services.GetRequiredService<ToolDefinitionService>().ListVersionsAsync("seed-sharing", default, default);
            Assert.HasCount(1, versions);
            Assert.IsNotNull(await host.Host.Services.GetRequiredService<FlowService>().GetVersionExactAsync(ResourceScopeRef.Workspace(host.Context.WorkspaceId), new("seed-code"), "1.0.0", default));
            Assert.IsTrue((await host.Host.Services.GetRequiredService<HarnessProfileService>().GetVersionAsync(project.WorkspaceId, default, "seed-coder", "1.0.0", default)).IsSuccess);
        }
        var other = Guid.NewGuid();
        var identities = host.Host.Services.GetRequiredService<IIdentityStore>();
        await identities.AddPrincipalAsync(new(other, PrincipalKind.Human, "Other fixture administrator", null, PrincipalStatus.Active, DateTimeOffset.UnixEpoch), default);
        await identities.AddPlatformAdministratorAsync(new(other, DateTimeOffset.UnixEpoch), default);
        await using var bootstrapScope = host.Host.Services.CreateAsyncScope();
        var bootstrap = bootstrapScope.ServiceProvider.GetRequiredService<IBootstrapProfileApiService>();
        Assert.IsNull(await bootstrap.GetApplicationAsync(applied.Name, other, default));
        Assert.IsNull(await bootstrap.CancelAsync(applied.Name, other, default));
        Assert.IsFalse((await bootstrap.GetAsync(other, default)).Applications.Any(value => value.Name == applied.Name));
        await Assert.ThrowsAsync<BootstrapRequestException>(() => bootstrap.GetWorkspaceSeedAsync(selection.Id, target, other, default));
        // A changed pin is refused; a new receipt cannot be loaded through another Workspace.
        using var tampered = await host.Http.PostAsJsonAsync("/api/bootstrap/profiles/preview", new BootstrapProfilePreviewRequest([], target, Inputs: [inputs])
        { Seed = selection with { ArchiveDigest = new string('0', 64) } });
        Assert.AreEqual(HttpStatusCode.BadRequest, tampered.StatusCode);
    }

    private static async Task<BootstrapCompositionPreview> SeedPreviewAsync(SharingHost host, BootstrapApplicationTarget target, WorkspaceSeedSelection pin, BootstrapInputSelection input)
    {
        using var response = await host.Http.PostAsJsonAsync("/api/bootstrap/profiles/preview", new BootstrapProfilePreviewRequest([], target, Inputs: [input]) { Seed = pin });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<BootstrapCompositionPreview>())!;
    }
    private static async Task<BootstrapApplicationResource> ApplySeedAsync(SharingHost host, BootstrapApplicationTarget target, WorkspaceSeedSelection pin, BootstrapInputSelection input, string digest)
    {
        using var response = await host.Http.PostAsJsonAsync("/api/bootstrap/applications", new ApplyBootstrapProfilesRequest([], digest, target, RequestId: Guid.NewGuid(), Inputs: [input]) { Seed = pin });
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        var application = (await response.Content.ReadFromJsonAsync<BootstrapApplicationResource>())!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (application.Definition.Status == BootstrapApplicationStatus.Running)
        {
            await Task.Delay(50, timeout.Token);
            application = (await host.Http.GetFromJsonAsync<BootstrapApplicationResource>($"/api/bootstrap/applications/{application.Name}", timeout.Token))!;
        }
        return application;
    }
    private static async Task SeedModelGrantAsync(SharingHost host, bool allowed)
    {
        using var system = host.Host.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var provider = (await host.Host.Services.GetRequiredService<IResourceStore>().GetExactAsync<ModelProviderResource>(
            ScopedResourceAddress.Create(ResourceScopeRef.Instance, default, ModelResourceKinds.ModelProvider, "seed-model"), default))!.Value;
        var policies = host.Host.Services.GetRequiredService<IResourceAccessPolicyService>(); var existing = await policies.GetAsync(provider.Uid, default);
        await policies.PutAsync(new(new(provider.Uid, ResourceScopeRef.Instance, provider.Namespace, provider.Kind, provider.Generation),
            allowed ? [new(ResourceScopeRef.Workspace(host.Context.WorkspaceId), [ResourceAccessAction.Discover, ResourceAccessAction.Use])] : []), existing?.ETag, default);
    }
    private static async Task RegisterSeedWorkerAsync(SharingHost host) => _ = await host.Host.Services.GetRequiredService<RuntimeWorkerDispatchService>().RegisterAsync(
        new(Guid.NewGuid()), new(Guid.NewGuid()), "fixture", 1, [new("coding-cli", "1.0", new HashSet<string> { "1.0" }, "fixture")], default);
    private static async Task SeedPrerequisitesAsync(SharingHost host)
    {
        using var system = host.Host.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var resources = host.Host.Services.GetRequiredService<IResourceStore>();
        // The legacy provider fixture also projects an unmanaged Tool. Model an installed public
        // extension contribution with its normal immutable manager identity for seed resolution.
        var authority = ResourceGovernance.Extension($"{host.Tool.ScopeRef!.Value.Value}/ExtensionRegistration/default/git-fixture", "Agentstration.Extensions.Git", "1.0.0");
        using (host.Host.Services.GetRequiredService<IResourceGovernanceContext>().Push(authority))
            await resources.PutExactAsync(host.Tool.ScopeRef.Value, new ToolResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.Tool,
                ScopeRef = host.Tool.ScopeRef,
                Metadata = new() { Name = "seed-git-source" },
                Governance = authority,
                Definition = host.Tool.Definition
            }, null, true, default);
        var runtime = (await resources.PutExactAsync(ResourceScopeRef.Instance, new RuntimeProfileResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = RuntimeProfileResourceKinds.RuntimeProfile,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "seed-runtime" },
            Generation = 1,
            Definition = new() { DisplayName = "Seed native runtime", RuntimeType = "coding-cli" }
        }, null, true, default)).Value;
        await resources.PutExactAsync(ResourceScopeRef.Instance, new ExtensionRegistrationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ExtensionKinds.ExtensionRegistration,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "seed-model-extension" },
            Definition = new() { DisplayName = "Installed model fixture", Enabled = true, Endpoint = new("http://model-fixture/"), ExpectedExtensionId = "fixture-model" }
        }, null, true, default);
        var provider = (await resources.PutExactAsync(ResourceScopeRef.Instance, new ModelProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ModelResourceKinds.ModelProvider,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "seed-model" },
            Definition = new() { DisplayName = "Installed model capability", ContributionId = "fixture", Extension = new("seed-model-extension", ResourceScopeRef.Instance) }
        }, null, true, default)).Value;
        var policies = host.Host.Services.GetRequiredService<IResourceAccessPolicyService>();
        foreach (var resource in new Resource[] { runtime, provider })
            await policies.PutAsync(new(new(resource.Uid, ResourceScopeRef.Instance, resource.Namespace, resource.Kind, resource.Generation),
                [new(ResourceScopeRef.Workspace(host.Context.WorkspaceId), [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
        await RegisterSeedWorkerAsync(host);
    }
    private static byte[] SeedArchive(AuthenticatedGitFixture native, ToolResource source, WorkspaceSeedPlanImport import)
    {
        var manifest = new WorkspaceSeedManifest("1.0", "1.0.0", "fixture-v1", "seed", "bootstrap", new("seed-project", default),
            new([new() { Kind = "Tool", ExtensionId = "Agentstration.Extensions.Git", ContributionId = "git.repository" }],
                [new("seed-model", ResourceScopeRef.Instance)], [new("seed-runtime", ResourceScopeRef.Instance)], new("coding-cli", "1.0", "1.0")))
        { ImportPlans = [import] };
        var nativeConfiguration = new HarnessNativeConfigurationDefinition("codex", "1.0.0", new("fixture-model", "workspace-write"));
        var harness = new HarnessProfileDefinition("Seed coder", "Implement the requested task.",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }), new("seed-native", "1.0.0", default));
        var digest = CodexHarnessMaterializer.ProfileDigest(harness, new(ResourceScopeRef.Instance, default, "seed-native", "1.0.0", Guid.Empty, 1,
            CodexHarnessMaterializer.ContentDigest(nativeConfiguration), nativeConfiguration));
        var relay = JsonSerializer.SerializeToNode<ToolDefinitionProperties>(new RelayToolDefinitionProperties
        {
            DisplayName = "Seed sharing",
            Publish = true,
            Target = new() { Name = source.Name, ScopeRef = source.ScopeRef!.Value, Uid = source.Uid, Generation = source.Generation },
            Metadata = new Dictionary<string, JsonElement> { ["agentstration.git.repository"] = source.Definition.Metadata["agentstration.git.repository"] },
            Bindings = [new SecretToolArgumentBinding { Path = "/credential", Secret = new(new(default, "Secret", "git-credential"), source.ScopeRef) },
                Literal("/repository", native.Repository), Literal("/authentication", "Basic"), Literal("/username", "fixture"),
                Literal("/request/refName", "refs/heads/main"), Literal("/allowedRefPrefixes", new[] { "refs/heads/main" }), Literal("/allowedPathPrefixes", new[] { "shared" })]
        }, Json)!.AsObject();
        relay["target"] = JsonSerializer.SerializeToNode(new { extensionContribution = new { kind = "Tool", extensionId = "Agentstration.Extensions.Git", contributionId = "git.repository" } }, Json);
        relay["bindings"]![0]!["secret"] = JsonSerializer.SerializeToNode(new { resourceRef = new { name = "git-credential" } }, Json);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            WriteSeed(zip, WorkspaceSeedFormat.ManifestPath, JsonSerializer.Serialize(manifest, Json));
            WriteSeed(zip, "bootstrap/profile.yaml", "apiVersion: agentstration.io/v1\nkind: BootstrapProfile\nmetadata:\n  name: seed\ndefinition:\n  targetScope: workspace\n");
            WriteSeed(zip, "bootstrap/10-relay.yaml", SeedResource("ToolDefinition", "seed-sharing", relay));
            WriteSeed(zip, "bootstrap/20-project.yaml", SeedResource("ProjectSharing", "seed-project", new
            {
                projectId = new { value = Guid.Parse("04bf923e-0c0a-4c30-a013-43a9b6ed90a0") },
                participantId = new { value = new { valueFrom = new { configuration = "Agentstration:Bootstrap:Parameters:ParticipantId", type = "string" } } },
                location = "shared",
                toolName = "agentstration.seed-sharing.v1.0.0"
            }));
            WriteSeed(zip, "bootstrap/30-native.yaml", SeedResource("HarnessNativeConfiguration", "seed-native", new
            { version = "1.0.0", nativeConfiguration.Materializer, nativeConfiguration.MaterializerVersion, nativeConfiguration.Settings }));
            WriteSeed(zip, "bootstrap/40-harness.yaml", SeedResource("HarnessProfile", "seed-coder", new
            { version = "1.0.0", profile = harness }));
            var runtime = new RuntimeFlowDefinition("coding-cli", "1.0", "1.0", JsonSerializer.SerializeToElement(
                new CodingExecutionConfiguration(new("seed-runtime", default), new("seed-coder", default, "1.0.0", digest), "example/fixture"), Json));
            WriteSeed(zip, "bootstrap/50-flow.yaml", SeedResource("Flow", "seed-code", new
            { version = "1.0.0", spec = JsonSerializer.SerializeToElement<FlowDefinition>(runtime, Json), publish = true, activate = true }));
        }
        return output.ToArray();
    }
    private static async Task<WorkspaceSeedPlanImport> SeedSharedPlanAsync(AuthenticatedGitFixture native, Guid participant)
    {
        var root = new WorkTaskId(Guid.NewGuid()); var plan = WorkPlanId.New();
        var value = new WorkSharingPlan(plan, new("Imported seed plan", [root], []),
            [new(root, [new(root, WorkTaskKind.Atomic, "Implement fixture", "Produce deterministic code evidence")])], [new(root, new(participant))]);
        var content = WorkSharingContent.Normalize(new(new(WorkSharingDomain.Plan, plan.ToString()), 0, JsonSerializer.SerializeToElement(value, Json)));
        Assert.IsTrue(content.IsSuccess, content.ErrorCode);
        var record = new WorkSharingRecord(content.Value!.Key, 1, content.Value.Content, WorkPlanDefinition.Digest(content.Value.Content), new(participant), DateTimeOffset.UnixEpoch);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        var space = new WorkSharingSpace(new(Guid.Parse("04bf923e-0c0a-4c30-a013-43a9b6ed90a0")), [new(record.Key, record.Version, record.Digest, bytes.Length)]);
        var directory = Path.Combine(native.Work, "shared", "records", record.Key.Domain.ToString(), record.Key.Name); Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, $"{record.Version:D10}-{record.Digest}.json"), bytes);
        await File.WriteAllBytesAsync(Path.Combine(native.Work, "shared", "index.json"), JsonSerializer.SerializeToUtf8Bytes(space, Json));
        await native.GitAsync(native.Work, "add", "shared"); await native.GitAsync(native.Work, "commit", "-m", "Seed portable plan fixture");
        await native.GitAsync(native.Work, "push", native.RepositoryPath, "HEAD:refs/heads/main");
        return new(plan.Value, record.Digest);
    }
    private static string SeedResource(string kind, string name, object definition) => ResourceManifestSerializer.ToYaml(new
    { apiVersion = ResourceApiVersions.CoreV1, kind, metadata = new { name, @namespace = "default" }, definition });
    private static void WriteSeed(ZipArchive zip, string path, string content)
    { using var writer = new StreamWriter(zip.CreateEntry(path).Open()); writer.Write(content); }
}
