using System.Text;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Flows.Application;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.Infrastructure.Storage;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Explicit deployment probe, never a product endpoint or an optional passing/skipped functional test.
if (args.Length != 3 || args[0] is not ("write" or "read"))
    throw new ArgumentException("Usage: StorageVolumeProbe write|read <control-plane-directory> <local-storage-root>");
var dataDirectory = Path.GetFullPath(args[1]);
var storageRoot = Path.GetFullPath(args[2]);
Directory.CreateDirectory(dataDirectory);
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddSingleton(new LocalBootstrapOptions { GrantPlatformAdministrator = true });
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "probe-reference-keys")));
builder.Services.AddAgentstration(new AgentstrationCompositionOptions
{
    DataDirectory = dataDirectory,
    LocalStorageRootDirectory = storageRoot,
    EnableHostedServices = false
});
using var host = builder.Build();
var services = host.Services;
var scopes = services.GetRequiredService<IRequestContextScopeFactory>();
RequestContext context;
using (scopes.PushSystem())
{
    await services.GetRequiredService<IAgentstrationStorageInitializer>().InitializeAsync(default);
    await services.GetRequiredService<AgentManagementService>().InitializeAsync(default);
    await services.GetRequiredService<FlowService>().InitializeAsync(default);
    await services.GetRequiredService<FlowRunService>().InitializeAsync(default);
    context = await services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
    await services.GetRequiredService<IOperationalTopologyProvisioner>().EnsureAllAsync(default);
    await services.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
}
using (scopes.PushSystem())
{
    var originalWorkspace = (await services.GetRequiredService<IIdentityStore>().GetWorkspaceAsync(context.TenantId, context.WorkspaceId, default))!;
    var blocked = await services.GetRequiredService<LocalStorageDefaultsProvisioner>().EnsureAsync(originalWorkspace, default);
    if (blocked.Availability != StorageAvailability.Unavailable)
        throw new InvalidOperationException("The restricted business default unexpectedly became usable.");
    var operational = await services.GetRequiredService<IOperationalTopologyProvisioner>().GetInstanceOperationsWorkspaceAsync(default);
    context = context with { TenantId = operational.TenantId, WorkspaceId = operational.Id };
}
using var caller = scopes.PushInstance(context);
var scope = ResourceScopeRef.Instance;
var resource = (await services.GetRequiredService<IResourceStore>().GetExactAsync<StorageResource>(
    new(scope, ResourceNamespace.Default, StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName), default))!.Value;
var target = new StorageContentRequest(new(new(scope, resource.Namespace, resource.Kind, resource.Name), resource.Uid,
    resource.Generation, StorageDigests.Binding(resource.Definition)), new(scope, resource.Address), new(Write: true, Prefix: "probe/"));
var gateway = services.GetRequiredService<IStorageContentGateway>();
var artifacts = services.GetRequiredService<ArtifactManagementService>();
var durable = services.GetRequiredService<IArtifactDurableStore>();
var workspace = new WorkspaceId(context.WorkspaceId);
var statePath = Path.Combine(dataDirectory, "storage-volume-proof.json");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var bytes = Encoding.UTF8.GetBytes("Storage volume persistence · été · exact UID");
if (args[0] == "write")
{
    var written = await gateway.WriteAsync(target, new("docker-volume-create", DateTimeOffset.UtcNow.AddMinutes(4),
        "probe/content", Convert.ToBase64String(bytes)));
    var source = await InWorkspaceAsync(() => artifacts.CreateStagedAsync(new("proof.txt", "text/plain",
        new ArtifactProducer { Kind = ArtifactProducerKind.FlowRun, Id = "probe-producer", FlowRunId = "probe-producer", FlowStepId = "produce" }), default));
    _ = await InWorkspaceAsync(() => artifacts.WriteAsync(source.Value.ArtifactId, 0, bytes, default));
    _ = await InWorkspaceAsync(() => artifacts.SealAsync(source.Value.ArtifactId, default));
    var opaque = await services.GetRequiredService<WorkspaceArtifactStorage>().CreateReferenceAsync(workspace,
        new string('a', 64), bytes.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), default);
    await durable.ResetAsync(workspace, opaque, default);
    _ = await durable.WriteAsync(workspace, opaque, 0, bytes, default);
    var stat = await durable.StatAsync(workspace, opaque, default);
    var receipt = new ArtifactStorageReceipt
    {
        StorageFlowRunId = "probe-storage",
        OpaqueReference = opaque,
        MediaType = "text/plain",
        Length = stat.Length,
        Sha256 = stat.Sha256,
        Provenance = new Dictionary<string, string> { ["provider"] = "filesystem", ["probe"] = "volume" }
    };
    var completed = await InWorkspaceAsync(() => artifacts.CompleteFlowRunArtifactAsync(source.Value.ArtifactId, new("probe-producer", "produce", receipt), default));
    var historical = services.GetRequiredService<FileSystemDurableArtifactStore>();
    await historical.ResetAsync(workspace, "historical-proof", default);
    _ = await historical.WriteAsync(workspace, "historical-proof", 0, bytes, default);
    await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new Proof(resource.Uid, context.WorkspaceId,
        written.Reference, written.Sha256, completed.Value.ArtifactId.ToString(), opaque), json));
    Console.WriteLine($"write: Storage={resource.Uid:N}; content and Artifact receipt committed");
}
else
{
    var saved = JsonSerializer.Deserialize<Proof>(await File.ReadAllTextAsync(statePath), json)!;
    if (saved.StorageUid != resource.Uid || saved.WorkspaceId != context.WorkspaceId)
        throw new InvalidOperationException("Provisioning changed the exact persisted resource identity.");
    var read = await gateway.ReadAsync(target, saved.ContentReference, 0, 65536);
    if (!Convert.FromBase64String(read.ContentBase64).SequenceEqual(bytes) || read.Item.Sha256 != saved.Digest)
        throw new InvalidOperationException("Content did not survive container replacement.");
    var artifact = (await InWorkspaceAsync(() => artifacts.GetFlowRunArtifactAsync(FlowRunArtifactId.Parse(saved.ArtifactId), default)))!;
    if (artifact.Value.Receipt.OpaqueReference != saved.ArtifactReference)
        throw new InvalidOperationException("The historical Artifact receipt was rewritten.");
    if (!(await durable.ReadAsync(workspace, saved.ArtifactReference, 0, bytes.Length, default)).Span.SequenceEqual(bytes)
        || !(await durable.ReadAsync(workspace, "historical-proof", 0, bytes.Length, default)).Span.SequenceEqual(bytes))
        throw new InvalidOperationException("Selectable or historical Artifact content did not survive container replacement.");
    Console.WriteLine($"read: Storage={resource.Uid:N}; identity, content, exact Artifact receipt and historical content preserved");
}

async Task<T> InWorkspaceAsync<T>(Func<Task<T>> action)
{
    using var workspaceScope = scopes.Push(context);
    return await action();
}

internal sealed record Proof(Guid StorageUid, Guid WorkspaceId, string ContentReference, string Digest, string ArtifactId, string ArtifactReference);
