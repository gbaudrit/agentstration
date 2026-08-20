using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Infrastructure.Packs;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.Resources;
using Agentstration.Runtime.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class PackTests
{
    [TestMethod]
    public async Task ComposerCreatesProjectFromWorkspaceCatalog()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var catalog = await client.GetFromJsonAsync<PackCompositionCatalogItem[]>("/api/pack-projects/composer/resources")
            ?? throw new AssertFailedException("The Pack Composer catalog response was empty.");
        var agent = catalog.Single(item => item.Resource.Kind == ResourceKinds.Agent && item.Resource.Name == "dotnet-expert");
        var modelProfile = catalog.Single(item => item.Resource.Kind == ResourceKinds.ModelProfile && item.Resource.Name == "reasoning-default");
        var modelProvider = catalog.Single(item => item.Resource.Kind == ResourceKinds.ModelProvider && item.Resource.Name == "ollama-local");
        var runtimeProfile = catalog.Single(item => item.Resource.Kind == ResourceKinds.RuntimeProfile && item.Resource.Name == "maf-default");
        Assert.AreEqual(PackCompositionAvailability.Selectable, agent.Availability);
        Assert.AreEqual(PackCompositionAvailability.Selectable, modelProfile.Availability);
        Assert.AreEqual(PackCompositionAvailability.Selectable, modelProvider.Availability);
        Assert.AreEqual(PackCompositionAvailability.Selectable, runtimeProfile.Availability);

        var previewResponse = await client.PostAsJsonAsync(
            "/api/pack-projects/composer/preview",
            new PreviewPackCompositionCommand([agent.Resource]));
        previewResponse.EnsureSuccessStatusCode();
        var preview = await previewResponse.Content.ReadFromJsonAsync<PackCompositionPreview>();
        Assert.IsNotNull(preview);
        Assert.IsTrue(preview.CanCreate);
        Assert.AreEqual("dotnet-expert", preview.Resources.Single().Resource.Name);
        CollectionAssert.AreEquivalent(
            new[] { PackBindingTargetKind.ModelProfile, PackBindingTargetKind.RuntimeProfile },
            preview.Bindings.Select(binding => binding.TargetKind).ToArray());

        previewResponse = await client.PostAsJsonAsync(
            "/api/pack-projects/composer/preview",
            new PreviewPackCompositionCommand([agent.Resource, modelProfile.Resource]));
        previewResponse.EnsureSuccessStatusCode();
        preview = await previewResponse.Content.ReadFromJsonAsync<PackCompositionPreview>();
        Assert.IsNotNull(preview);
        CollectionAssert.AreEquivalent(
            new[] { ResourceKinds.Agent, ResourceKinds.ModelProfile },
            preview.Resources.Select(resource => resource.Resource.Kind).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { PackBindingTargetKind.ModelProvider, PackBindingTargetKind.RuntimeProfile },
            preview.Bindings.Select(binding => binding.TargetKind).ToArray());

        previewResponse = await client.PostAsJsonAsync(
            "/api/pack-projects/composer/preview",
            new PreviewPackCompositionCommand([agent.Resource, modelProfile.Resource, modelProvider.Resource, runtimeProfile.Resource]));
        previewResponse.EnsureSuccessStatusCode();
        preview = await previewResponse.Content.ReadFromJsonAsync<PackCompositionPreview>();
        Assert.IsNotNull(preview);
        Assert.HasCount(4, preview.Resources);
        var extensionBinding = preview.Bindings.Single();
        Assert.AreEqual(PackBindingTargetKind.ExtensionRegistration, extensionBinding.TargetKind);
        Assert.AreEqual("ollama-extension", extensionBinding.WorkspaceResource.Name);

        var createResponse = await client.PostAsJsonAsync("/api/pack-projects", new CreatePackProjectFromWorkspaceCommand
        {
            Publisher = "local",
            Name = $"dotnet-pack-{Guid.NewGuid():N}",
            Version = "0.1.0",
            DisplayName = "Pack .NET",
            Resources = [agent.Resource, modelProfile.Resource, modelProvider.Resource, runtimeProfile.Resource]
        });
        Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);
        var project = await createResponse.Content.ReadFromJsonAsync<PackProjectResource>();
        Assert.IsNotNull(project);
        Assert.AreEqual(PackProjectSourceKind.WorkspaceSnapshot, project.Definition.SourceKind);
        Assert.HasCount(4, project.Definition.SourceResources);
        Assert.IsTrue(project.Definition.SourceResources.Any(resource => resource.Kind == ResourceKinds.ModelProfile && resource.Name == "reasoning-default"));
        Assert.IsTrue(project.Definition.SourceResources.Any(resource => resource.Kind == ResourceKinds.ModelProvider && resource.Name == "ollama-local"));
        Assert.IsTrue(project.Definition.SourceResources.Any(resource => resource.Kind == ResourceKinds.RuntimeProfile && resource.Name == "maf-default"));
    }

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
    public async Task ArchiveReaderRejectsLegacyPackSpecEnvelope()
    {
        await using var archive = CreateZip(new Dictionary<string, string>
        {
            ["pack.yaml"] = """
                apiVersion: agentstration.io/v1
                kind: Pack
                metadata:
                  name: legacy-pack
                  publisher: agentstration
                  version: 1.0.0
                spec:
                  resources: []
                """
        });

        var exception = await Assert.ThrowsExactlyAsync<PackValidationException>(() =>
            new ZipPackArchiveReader().ReadAsync(archive, "legacy.zip", default));

        Assert.AreEqual("pack_definition_missing", exception.Code);
        StringAssert.Contains(exception.Message, "legacy 'spec'");
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
    public async Task ResourceBindingsResolveReferencesAndSurviveReinstallation()
    {
        await using var fixture = await PackFixture.CreateAsync();
        _ = await fixture.Store.PutAsync(new ModelProfileResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.ModelProfile,
            Metadata = new ResourceMetadata { Name = "shared-chat" },
            Definition = new ModelProfileProperties
            {
                DisplayName = "Shared Chat",
                Provider = new("provider"),
                Model = new ModelSelection { Name = "model" }
            }
        }, null, true, default);
        _ = await fixture.Store.PutAsync(new SecretResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Secret,
            Metadata = new ResourceMetadata { Name = "shared-key" },
            Definition = new SecretProperties
            {
                DisplayName = "Shared Key",
                Vault = new("local"),
                Key = "shared-key"
            }
        }, null, true, default);
        _ = await fixture.Store.PutAsync(new RuntimeProfileResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.RuntimeProfile,
            Metadata = new ResourceMetadata { Name = "shared-runtime", Namespace = new ResourceNamespace("shared.platform") },
            Definition = new RuntimeProfileProperties
            {
                DisplayName = "Shared Runtime",
                RuntimeType = "microsoft-agent-framework"
            }
        }, null, true, default);
        var handler = new FakeHandler("Consumer", 10, []);
        var service = new PackManagementService(fixture.Store, [handler], TimeProvider.System);
        var document = new PackResourceDocument(
            "resources/consumer.json",
            ManagementApiVersions.CoreV1,
            "Consumer",
            "consumer",
            JsonSerializer.SerializeToElement(new
            {
                apiVersion = ManagementApiVersions.CoreV1,
                kind = "Consumer",
                metadata = new { name = "consumer" },
                definition = new
                {
                    modelProfile = new { binding = "chat" },
                    runtimeProfile = new { binding = "runtime" },
                    credential = new { binding = "credential" }
                }
            }));
        var archive = new PackArchive(
            new PackManifest
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = PackKinds.Pack,
                Metadata = new PackMetadata { Publisher = "agentstration", Name = "test-pack", Version = "1.0.0" },
                Definition = new PackDefinition
                {
                    Resources = [document.Path],
                    Bindings =
                    [
                        new PackBindingRequirement { Name = "chat", TargetKind = PackBindingTargetKind.ModelProfile },
                        new PackBindingRequirement { Name = "runtime", TargetKind = PackBindingTargetKind.RuntimeProfile },
                        new PackBindingRequirement { Name = "credential", TargetKind = PackBindingTargetKind.Secret }
                    ]
                }
            },
            [document],
            "bindings.pack.zip");

        var initialPreview = await service.PreviewAsync(archive, default);
        Assert.IsTrue(initialPreview.RequiresConfiguration);
        Assert.IsTrue(initialPreview.Bindings.All(binding => binding.UsedBy.Single().ResourceName == "consumer"));

        var wrongKind = await Assert.ThrowsExactlyAsync<PackValidationException>(() => service.InstallAsync(
            archive,
            [
                new PackBindingSelection("chat", new("shared-key", @namespace: ResourceNamespace.Default)),
                new PackBindingSelection("runtime", new("shared-key", @namespace: ResourceNamespace.Default)),
                new PackBindingSelection("credential", new("shared-key", @namespace: ResourceNamespace.Default))
            ],
            default));
        Assert.AreEqual("pack_binding_unresolved", wrongKind.Code);

        var installed = await service.InstallAsync(
            archive,
            [
                new PackBindingSelection("chat", new("shared-chat", @namespace: ResourceNamespace.Default)),
                new PackBindingSelection("runtime", new("shared-runtime", @namespace: new ResourceNamespace("shared.platform"))),
                new PackBindingSelection("credential", new("shared-key", @namespace: ResourceNamespace.Default))
            ],
            default);

        Assert.HasCount(3, installed.Value.Definition.Bindings);
        Assert.IsNotNull(handler.LastInstalledManifest);
        var definition = handler.LastInstalledManifest.Value.GetProperty("definition");
        Assert.AreEqual("shared-chat", definition.GetProperty("modelProfile").GetProperty("name").GetString());
        Assert.AreEqual("default", definition.GetProperty("modelProfile").GetProperty("namespace").GetString());
        Assert.AreEqual("shared-runtime", definition.GetProperty("runtimeProfile").GetProperty("name").GetString());
        Assert.AreEqual("shared.platform", definition.GetProperty("runtimeProfile").GetProperty("namespace").GetString());
        Assert.AreEqual("shared-key", definition.GetProperty("credential").GetProperty("name").GetString());

        await service.UninstallAsync(new("agentstration", "test-pack"), default);
        var restoredPreview = await service.PreviewAsync(archive, default);
        Assert.IsFalse(restoredPreview.RequiresConfiguration);
        Assert.IsTrue(restoredPreview.Bindings.All(binding => binding.TargetAvailable));

        var reinstalled = await service.InstallAsync(archive, default);
        Assert.HasCount(3, reinstalled.Value.Definition.Bindings);
        Assert.AreEqual("shared-key", reinstalled.Value.Definition.Bindings.Single(binding => binding.Name == "credential").Target.Name);
    }

    [TestMethod]
    public async Task RuntimeProfileBindingControlsThePackAgentsLocalDeployment()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var runtimeNamespace = new ResourceNamespace("shared.platform");
        var runtimeProfiles = factory.Services.GetRequiredService<RuntimeProfileManagementService>();
        _ = await runtimeProfiles.CreateAsync(new RuntimeProfileResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.RuntimeProfile,
            Metadata = new ResourceMetadata { Name = "pack-runtime", Namespace = runtimeNamespace },
            Definition = new RuntimeProfileProperties
            {
                DisplayName = "Pack runtime",
                RuntimeType = "microsoft-agent-framework"
            }
        }, default);
        await using var archive = CreateZip(new Dictionary<string, string>
        {
            ["pack.yaml"] = """
                apiVersion: agentstration.io/v1
                kind: Pack
                metadata:
                  name: runtime-binding-pack
                  publisher: agentstration
                  version: 1.0.0
                definition:
                  bindings:
                    - name: local-runtime
                      targetKind: runtimeProfile
                      required: true
                  resources:
                    - agents/assistant.yaml
                """,
            ["agents/assistant.yaml"] = """
                apiVersion: agentstration.io/v1
                kind: Agent
                metadata:
                  name: assistant
                definition:
                  displayName: Assistant
                  instructions: Help the user.
                  modelProfile:
                    name: reasoning-default
                    namespace: default
                  runtimeProfile:
                    binding: local-runtime
                """
        });
        using var content = InstallationContent(
            archive.ToArray(),
            "runtime-binding.pack.zip",
            [new PackBindingSelection("local-runtime", new("pack-runtime", @namespace: runtimeNamespace))]);

        using var response = await client.PostAsync("/api/packs", content);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        var agents = factory.Services.GetRequiredService<AgentManagementService>();
        var packNamespace = new ResourceNamespace("agentstration.runtime-binding-pack");
        var agent = await agents.GetAgentAsync(packNamespace, "assistant", default);
        Assert.IsNotNull(agent);
        Assert.AreEqual("pack-runtime", agent.Value.Definition.RuntimeProfile.Name);
        Assert.AreEqual(runtimeNamespace, agent.Value.Definition.RuntimeProfile.Namespace);
        var deployment = await agents.PrepareLocalRuntimeAsync(packNamespace, "assistant", agent.Value.Generation, default);
        Assert.AreEqual("pack-runtime", deployment.Value.RuntimeProfileName);
        Assert.AreEqual(runtimeNamespace, deployment.Value.RuntimeProfileNamespace);

        using var readinessResponse = await client.GetAsync($"/api/runtime/namespaces/{packNamespace.Value}/agents/assistant/readiness?generation={agent.Value.Generation}");
        Assert.AreEqual(HttpStatusCode.OK, readinessResponse.StatusCode, await readinessResponse.Content.ReadAsStringAsync());
        var readiness = await readinessResponse.Content.ReadFromJsonAsync<AgentRuntimeReadinessResponse>();
        Assert.IsNotNull(readiness);
        Assert.IsTrue(readiness.Ready);

        using var preparationResponse = await client.PostAsync($"/api/runtime/namespaces/{packNamespace.Value}/agents/assistant/prepare?generation={agent.Value.Generation}", null);
        Assert.AreEqual(HttpStatusCode.OK, preparationResponse.StatusCode, await preparationResponse.Content.ReadAsStringAsync());
        var prepared = await preparationResponse.Content.ReadFromJsonAsync<PrepareAgentRuntimeResponse>();
        Assert.IsNotNull(prepared);
        Assert.AreEqual("Ready", prepared.State);
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
        Assert.AreEqual("agentstration.test-pack", previewBody.RootElement.GetProperty("namespace").GetString());
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

        using var rejectedContent = new ByteArrayContent(archiveBytes);
        rejectedContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        rejectedContent.Headers.Add("X-Pack-File-Name", "empty-pack.zip");
        using var rejected = await client.PostAsync("/api/packs", rejectedContent);
        Assert.AreEqual(HttpStatusCode.Conflict, rejected.StatusCode);

        using var replacementContent = new ByteArrayContent(archiveBytes);
        replacementContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        replacementContent.Headers.Add("X-Pack-File-Name", "empty-pack.zip");
        using var replaced = await client.PostAsync("/api/packs?replaceExisting=true", replacementContent);
        Assert.AreEqual(HttpStatusCode.Created, replaced.StatusCode, await replaced.Content.ReadAsStringAsync());

        var listed = await client.GetFromJsonAsync<JsonElement[]>("/api/packs");
        Assert.IsNotNull(listed);
        Assert.IsTrue(listed.Any(value => value.GetProperty("definition").GetProperty("packName").GetString() == "test-pack"));
        using var defaultResource = await client.GetAsync("/api/runtimeprofiles/pack-runtime");
        Assert.AreEqual(HttpStatusCode.NotFound, defaultResource.StatusCode);

        var packService = factory.Services.GetRequiredService<PackManagementService>();
        var runtimeProfiles = factory.Services.GetRequiredService<RuntimeProfileManagementService>();
        Assert.IsNotNull(await runtimeProfiles.GetAsync(new ResourceNamespace("agentstration.test-pack"), "pack-runtime", default));
        var controlStore = factory.Services.GetRequiredService<IControlPlaneStore>();
        var retained = await packService.GetAsync(new("agentstration", "test-pack"), default);
        Assert.IsNotNull(retained);
        _ = await controlStore.PutAsync(retained.Value with
        {
            Generation = retained.Value.Generation + 1,
            Definition = retained.Value.Definition with { SourceArtifact = null }
        }, retained.ETag, false, default);

        using var legacy = await client.GetAsync("/api/packs/agentstration/test-pack");
        Assert.AreEqual(HttpStatusCode.OK, legacy.StatusCode);
        Assert.IsNotNull(legacy.Headers.ETag);
        using var attach = new HttpRequestMessage(HttpMethod.Post, "/api/packs/agentstration/test-pack/source");
        attach.Headers.IfMatch.Add(legacy.Headers.ETag);
        attach.Headers.Add("X-Pack-File-Name", "recovered-pack.zip");
        attach.Content = new ByteArrayContent(archiveBytes);
        attach.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var attached = await client.SendAsync(attach);
        Assert.AreEqual(HttpStatusCode.OK, attached.StatusCode, await attached.Content.ReadAsStringAsync());
        var recovered = await attached.Content.ReadFromJsonAsync<InstalledPackResource>();
        Assert.IsNotNull(recovered?.Definition.SourceArtifact);

        using var removed = await client.DeleteAsync("/api/packs/agentstration/test-pack");
        Assert.AreEqual(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.IsNull(await runtimeProfiles.GetAsync(new ResourceNamespace("agentstration.test-pack"), "pack-runtime", default));
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
        Assert.AreEqual(PackAudience.Personal, preview.Metadata.Audience);
        Assert.AreEqual(PackPurpose.Sample, preview.Metadata.Purpose);
        Assert.AreEqual(new ResourceNamespace("agentstration.who-am-i"), preview.Namespace);
        Assert.HasCount(5, preview.Resources);
        Assert.IsTrue(preview.RequiresConfiguration);
        Assert.HasCount(2, preview.Bindings);
        Assert.HasCount(3, preview.Bindings.Single(binding => binding.Name == "conversational-model").UsedBy);
        Assert.HasCount(3, preview.Bindings.Single(binding => binding.Name == "local-runtime").UsedBy);
        CollectionAssert.AreEquivalent(
            new[] { ResourceKinds.Agent, ResourceKinds.Agent, ResourceKinds.Agent, ResourceKinds.Flow, ResourceKinds.Entry },
            preview.Resources.Select(resource => resource.Kind).ToArray());

        using var installContent = InstallationContent(
            bytes,
            "who-am-i.pack.zip",
            [
                new PackBindingSelection("conversational-model", new("reasoning-default", @namespace: ResourceNamespace.Default)),
                new PackBindingSelection("local-runtime", new("maf-default", @namespace: ResourceNamespace.Default))
            ]);
        using var installResponse = await client.PostAsync("/api/packs", installContent);
        Assert.AreEqual(HttpStatusCode.Created, installResponse.StatusCode, await installResponse.Content.ReadAsStringAsync());
        var installed = await installResponse.Content.ReadFromJsonAsync<InstalledPackResource>();
        Assert.IsNotNull(installed);
        Assert.AreEqual(InstalledPackState.Installed, installed.Definition.State);
        Assert.AreEqual(new ResourceNamespace("agentstration.who-am-i"), installed.Definition.Namespace);
        Assert.HasCount(5, installed.Definition.ManagedResources);
        Assert.AreEqual("reasoning-default", installed.Definition.Bindings.Single(binding => binding.Name == "conversational-model").Target.Name);
        Assert.AreEqual("maf-default", installed.Definition.Bindings.Single(binding => binding.Name == "local-runtime").Target.Name);
        Assert.IsTrue(installed.Definition.ManagedResources.All(resource => resource.Namespace == installed.Definition.Namespace));
        Assert.IsNotNull(installed.Definition.SourceArtifact);

        var defaultFlows = await client.GetFromJsonAsync<FlowPageResponse>("/api/flows?top=100");
        Assert.IsNotNull(defaultFlows);
        Assert.IsFalse(defaultFlows.Value.Any(flow => flow.Id == "who-am-i-game"));
        var allFlows = await client.GetFromJsonAsync<FlowPageResponse>("/api/flows?allNamespaces=true&top=100");
        Assert.IsNotNull(allFlows);
        var installedFlow = allFlows.Value.Single(flow => flow.Id == "who-am-i-game");
        Assert.AreEqual(new ResourceNamespace("agentstration.who-am-i"), installedFlow.Namespace);

        var defaultAgents = await client.GetFromJsonAsync<PagedResponse<AgentResource>>("/api/agents?top=100");
        Assert.IsNotNull(defaultAgents);
        Assert.IsFalse(defaultAgents.Value.Any(agent => agent.Metadata.Name.StartsWith("who-am-i-", StringComparison.Ordinal)));
        var allAgents = await client.GetFromJsonAsync<PagedResponse<AgentResource>>("/api/agents?allNamespaces=true&top=100");
        Assert.IsNotNull(allAgents);
        var installedAgents = allAgents.Value.Where(agent => agent.Namespace == installed.Definition.Namespace).ToArray();
        Assert.HasCount(3, installedAgents);

        var flowVersions = await client.GetFromJsonAsync<FlowVersionResponse[]>("/api/namespaces/agentstration.who-am-i/flows/who-am-i-game/versions");
        Assert.IsNotNull(flowVersions);
        Assert.IsTrue(flowVersions.Any(version => version.Version == "0.1.0" && version.Namespace == installedFlow.Namespace));

        using var flowVersionResponse = await client.GetAsync("/api/namespaces/agentstration.who-am-i/flows/who-am-i-game/versions/0.1.0");
        Assert.AreEqual(HttpStatusCode.OK, flowVersionResponse.StatusCode, await flowVersionResponse.Content.ReadAsStringAsync());
        using var flowVersion = JsonDocument.Parse(await flowVersionResponse.Content.ReadAsStreamAsync());
        var graph = flowVersion.RootElement.GetProperty("graph");
        Assert.AreEqual("input", graph.GetProperty("entryStep").GetString());
        Assert.AreEqual(3, graph.GetProperty("steps").GetArrayLength());

        using var input = JsonDocument.Parse("""{"prompt":"Start the game"}""");
        using var runResponse = await client.PostAsJsonAsync(
            "/api/namespaces/agentstration.who-am-i/flows/who-am-i-game/runs",
            new CreateFlowRunRequest(input.RootElement.Clone(), Version: "0.1.0"));
        Assert.AreEqual(HttpStatusCode.Accepted, runResponse.StatusCode, await runResponse.Content.ReadAsStringAsync());
        var run = await runResponse.Content.ReadFromJsonAsync<FlowRun>();
        Assert.IsNotNull(run);
        for (var attempt = 0; attempt < 50 && !run.Status.IsTerminal(); attempt++)
        {
            await Task.Delay(100);
            run = await client.GetFromJsonAsync<FlowRun>($"/api/flowRuns/{run.Id}");
            Assert.IsNotNull(run);
        }
        Assert.AreEqual(FlowRunStatus.Succeeded, run.Status, run.Error?.Details ?? run.Error?.Message);

        using var forkResponse = await client.PostAsJsonAsync(
            "/api/packs/agentstration/who-am-i/fork",
            new ForkPackCommand("local", "who-am-i-lab", "0.1.0-dev.1", "Who Am I? Lab", "Locally forked sample."));
        Assert.AreEqual(HttpStatusCode.Created, forkResponse.StatusCode, await forkResponse.Content.ReadAsStringAsync());
        var project = await forkResponse.Content.ReadFromJsonAsync<PackProjectResource>();
        Assert.IsNotNull(project);
        Assert.IsNotNull(project.Definition.Origin);
        Assert.AreEqual("agentstration", project.Definition.Origin.Publisher);
        Assert.AreEqual(PackAudience.Personal, project.Definition.Audience);
        Assert.AreEqual(PackPurpose.Sample, project.Definition.Purpose);

        using var buildResponse = await client.PostAsync($"/api/pack-projects/{project.Uid:D}/builds", null);
        Assert.AreEqual(HttpStatusCode.Created, buildResponse.StatusCode, await buildResponse.Content.ReadAsStringAsync());
        var build = await buildResponse.Content.ReadFromJsonAsync<PackProjectBuildResource>();
        Assert.IsNotNull(build);
        Assert.AreEqual("0.1.0-dev.1", build.Definition.Version);

        using var repeatedBuildResponse = await client.PostAsync($"/api/pack-projects/{project.Uid:D}/builds", null);
        Assert.AreEqual(HttpStatusCode.Created, repeatedBuildResponse.StatusCode, await repeatedBuildResponse.Content.ReadAsStringAsync());
        var repeatedBuild = await repeatedBuildResponse.Content.ReadFromJsonAsync<PackProjectBuildResource>();
        Assert.IsNotNull(repeatedBuild);
        Assert.AreEqual(build.Definition.Artifact.Sha256, repeatedBuild.Definition.Artifact.Sha256, "The same project revision must produce the same Pack bytes.");

        using var forkPreviewResponse = await client.PostAsync($"/api/pack-projects/{project.Uid:D}/builds/{build.Uid:D}/preview", null);
        Assert.AreEqual(HttpStatusCode.OK, forkPreviewResponse.StatusCode);
        var forkPreview = await forkPreviewResponse.Content.ReadFromJsonAsync<PackInstallationPreview>();
        Assert.IsNotNull(forkPreview);
        Assert.IsTrue(forkPreview.CanInstall, "A fork must coexist with its source because its resources use a distinct Pack namespace.");
        Assert.IsTrue(forkPreview.RequiresConfiguration, "A fork keeps logical requirements but does not inherit the source Pack's local binding configuration.");
        Assert.AreEqual(PackAudience.Personal, forkPreview.Metadata.Audience);
        Assert.AreEqual(PackPurpose.Sample, forkPreview.Metadata.Purpose);
        Assert.AreEqual(new ResourceNamespace("local.who-am-i-lab"), forkPreview.Namespace);
        Assert.IsTrue(forkPreview.Resources.All(resource => !resource.AlreadyExists));

        using var localInstallResponse = await client.PostAsJsonAsync(
            $"/api/pack-projects/{project.Uid:D}/builds/{build.Uid:D}/install",
            new PackBuildInstallRequest(
                Bindings:
                [
                    new PackBindingSelection("conversational-model", new("reasoning-default", @namespace: ResourceNamespace.Default)),
                    new PackBindingSelection("local-runtime", new("maf-default", @namespace: ResourceNamespace.Default))
                ]));
        Assert.AreEqual(HttpStatusCode.Created, localInstallResponse.StatusCode, await localInstallResponse.Content.ReadAsStringAsync());
        var localInstallation = await localInstallResponse.Content.ReadFromJsonAsync<InstalledPackResource>();
        Assert.IsNotNull(localInstallation);
        Assert.AreEqual("local", localInstallation.Definition.Publisher);
        Assert.AreEqual("who-am-i-lab", localInstallation.Definition.PackName);
        Assert.AreEqual(new ResourceNamespace("local.who-am-i-lab"), localInstallation.Definition.Namespace);
        using var retainedOriginResponse = await client.GetAsync("/api/packs/agentstration/who-am-i");
        Assert.AreEqual(HttpStatusCode.OK, retainedOriginResponse.StatusCode);
        using var sourceAgentResponse = await client.GetAsync("/api/namespaces/agentstration.who-am-i/agents/who-am-i-judge");
        using var forkAgentResponse = await client.GetAsync("/api/namespaces/local.who-am-i-lab/agents/who-am-i-judge");
        Assert.AreEqual(HttpStatusCode.OK, sourceAgentResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, forkAgentResponse.StatusCode);
        using var forkFlowResponse = await client.GetAsync("/api/namespaces/local.who-am-i-lab/flows/who-am-i-game/versions/0.1.0");
        Assert.AreEqual(HttpStatusCode.OK, forkFlowResponse.StatusCode, await forkFlowResponse.Content.ReadAsStringAsync());
        using var forkEntryResponse = await client.GetAsync("/api/namespaces/local.who-am-i-lab/entries/who-am-i");
        Assert.AreEqual(HttpStatusCode.OK, forkEntryResponse.StatusCode, await forkEntryResponse.Content.ReadAsStringAsync());
        using var forkEntryDraftResponse = await client.GetAsync("/api/namespaces/local.who-am-i-lab/management/entries/who-am-i");
        Assert.AreEqual(HttpStatusCode.OK, forkEntryDraftResponse.StatusCode, await forkEntryDraftResponse.Content.ReadAsStringAsync());
        using var forkEntryDependenciesResponse = await client.GetAsync("/api/namespaces/local.who-am-i-lab/management/entries/who-am-i/dependencies");
        Assert.AreEqual(HttpStatusCode.OK, forkEntryDependenciesResponse.StatusCode, await forkEntryDependenciesResponse.Content.ReadAsStringAsync());
        using var forkEntry = JsonDocument.Parse(await forkEntryResponse.Content.ReadAsStreamAsync());
        Assert.AreEqual("local.who-am-i-lab", forkEntry.RootElement.GetProperty("namespace").GetString());
        Assert.AreEqual("local.who-am-i-lab", forkEntry.RootElement.GetProperty("resolvedTarget").GetProperty("namespace").GetString());

        using var localReinstallResponse = await client.PostAsync($"/api/pack-projects/{project.Uid:D}/builds/{build.Uid:D}/install?replaceExisting=true", null);
        Assert.AreEqual(HttpStatusCode.Created, localReinstallResponse.StatusCode, await localReinstallResponse.Content.ReadAsStringAsync());

        using var downloadResponse = await client.GetAsync($"/api/pack-projects/{project.Uid:D}/builds/{build.Uid:D}/download");
        Assert.AreEqual(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.AreEqual("application/zip", downloadResponse.Content.Headers.ContentType?.MediaType);
        var downloaded = await downloadResponse.Content.ReadAsByteArrayAsync();
        Assert.IsGreaterThan(0, downloaded.Length);
        using (var builtArchive = new ZipArchive(new MemoryStream(downloaded), ZipArchiveMode.Read))
        {
            var builtManifest = builtArchive.GetEntry("pack.json");
            Assert.IsNotNull(builtManifest);
            await using var manifestStream = builtManifest.Open();
            using var manifestDocument = await JsonDocument.ParseAsync(manifestStream);
            Assert.IsTrue(manifestDocument.RootElement.TryGetProperty("definition", out _));
            Assert.IsFalse(manifestDocument.RootElement.TryGetProperty("spec", out _));
        }

        using var removeLocalResponse = await client.DeleteAsync("/api/packs/local/who-am-i-lab");
        Assert.AreEqual(HttpStatusCode.NoContent, removeLocalResponse.StatusCode);
        using var retainedSourceAgentResponse = await client.GetAsync("/api/namespaces/agentstration.who-am-i/agents/who-am-i-judge");
        Assert.AreEqual(HttpStatusCode.OK, retainedSourceAgentResponse.StatusCode);
    }

    private static PackArchive Archive(params PackResourceDocument[] resources) => new(
        new PackManifest
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = PackKinds.Pack,
            Metadata = new PackMetadata { Publisher = "agentstration", Name = "test-pack", Version = "1.0.0" },
            Definition = new PackDefinition { Resources = resources.Select(value => value.Path).ToArray() }
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

    private static MultipartFormDataContent InstallationContent(
        byte[] bytes,
        string fileName,
        IReadOnlyList<PackBindingSelection> bindings)
    {
        var content = new MultipartFormDataContent();
        var archive = new ByteArrayContent(bytes);
        archive.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(archive, "archive", fileName);
        content.Add(new StringContent(JsonSerializer.Serialize(bindings), Encoding.UTF8, "application/json"), "bindings");
        return content;
    }

    private static string Manifest(params string[] resources) => $$"""
        apiVersion: agentstration.io/v1
        kind: Pack
        metadata:
          name: test-pack
          publisher: agentstration
          version: 1.0.0
        definition:
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
        public JsonElement? LastInstalledManifest { get; private set; }

        public Task ValidateAsync(PackResourceDocument resource, IReadOnlyList<PackResourceDocument> allResources, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ExistsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => Task.FromResult(Exists);
        public Task<ManagedPackResource> InstallAsync(PackResourceDocument resource, PackIdentity pack, ResourceNamespace @namespace, string packVersion, CancellationToken cancellationToken)
        {
            events.Add($"install:{resource.Kind}/{resource.Name}");
            if (FailInstallation) throw new InvalidOperationException("simulated failure");
            LastInstalledManifest = resource.Manifest.Clone();
            Exists = true;
            return Task.FromResult(new ManagedPackResource { Namespace = @namespace, Kind = resource.Kind, Name = resource.Name, Path = resource.Path, VersionToken = CurrentToken });
        }
        public Task<string?> GetVersionTokenAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => Task.FromResult(Exists ? CurrentToken : null);
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
