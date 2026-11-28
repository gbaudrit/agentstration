extern alias GitHubExtension;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Application.Work;
using Agentstration.Bootstrap.Contracts;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Tools;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Testing;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ProjectSharingApiTests
{
    [TestMethod]
    public async Task OnePortableSeedReviewsDifferentLocalServiceScopesAndRejectsStaleAssignmentsBeforeWrites()
    {
        await using var native = await AuthenticatedGitFixture.CreateAsync();
        using var vendor = new GitHubProviderTests.NativeGitHub(native);
        await using var extension = DeliveryGitHubFactory(native, vendor); _ = extension.CreateClient();
        await using var left = await ConnectedHostAsync(native, "named-seed-left", false, extension);
        await using var right = await ConnectedHostAsync(native, "named-seed-right", false, extension);
        _ = await DeliveryPrerequisitesAsync(left); _ = await DeliveryPrerequisitesAsync(right);
        WorkProjectConnectionSelection first; WorkProjectConnectionSelection second;
        using (left.Scope()) first = await GitHubProviderTests.InstallIssueServiceAsync(left.Host.Services, left.Context, extension, instanceService: true);
        using (right.Scope()) second = await GitHubProviderTests.InstallIssueServiceAsync(right.Host.Services, right.Context, extension, alternateAccount: true);
        await ManageSeedDescribeAsync(left); await ManageSeedDescribeAsync(right);
        Assert.AreEqual(ResourceScopeRef.Instance, first.Assignment.Service.Address.ScopeRef);
        Assert.AreEqual(ResourceScopeRef.Workspace(right.Context.WorkspaceId), second.Assignment.Service.Address.ScopeRef);
        Assert.AreNotEqual(first.Assignment.Service.Uid, second.Assignment.Service.Uid);
        var bytes = NamedConnectionSeed(SeedArchive(native, left.Tool, new(Guid.NewGuid(), new string('a', 64))), first.Requirement);
        using (var archive = new ZipArchive(new MemoryStream(bytes)))
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                Assert.IsFalse(reader.ReadToEnd().Contains(first.Assignment.Service.Uid.ToString(), StringComparison.Ordinal));
            }
        var physical = NamedConnectionSeed(bytes, first.Requirement, definition => definition["connectionAssignments"]![0]!["service"] = new JsonObject { ["uid"] = first.Assignment.Service.Uid.ToString() });
        using (var refused = await left.Http.PostAsJsonAsync("/api/bootstrap/workspace-seeds", new IngestWorkspaceSeedRequest(Guid.NewGuid(),
            new(left.Context.TenantId, left.Context.WorkspaceId), Convert.ToBase64String(physical))))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode, await refused.Content.ReadAsStringAsync());
            StringAssert.Contains(await refused.Content.ReadAsStringAsync(), "seed_local_identity_forbidden");
        }
        var initialized = new List<WorkProjectResponse>();
        foreach (var (host, selection) in new[] { (left, first), (right, second) })
        {
            var target = new BootstrapApplicationTarget(host.Context.TenantId, host.Context.WorkspaceId);
            using var ingestion = await host.Http.PostAsJsonAsync("/api/bootstrap/workspace-seeds", new IngestWorkspaceSeedRequest(Guid.NewGuid(), target, Convert.ToBase64String(bytes)));
            Assert.AreEqual(HttpStatusCode.Created, ingestion.StatusCode, await ingestion.Content.ReadAsStringAsync());
            var receipt = (await ingestion.Content.ReadFromJsonAsync<WorkspaceSeedReceipt>())!;
            var seed = new WorkspaceSeedSelection(receipt.Provenance.Id, receipt.Provenance.ArchiveDigest);
            var participant = new BootstrapInputSelection("seed", "Agentstration:Bootstrap:Parameters:ParticipantId", "literal", Literal: JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()));
            var binding = new BootstrapBindingSelection("seed", "local-tracking", new(selection.Assignment.Service.Address.Name,
                selection.Assignment.Service.Address.ScopeRef, selection.Assignment.Service.Address.Namespace));
            using var candidates = await host.Http.PostAsJsonAsync("/api/bootstrap/binding-targets",
                new BootstrapBindingTargetsRequest(target, BootstrapBindingTargetKind.ConnectedService, []) { Seed = seed });
            Assert.AreEqual(HttpStatusCode.OK, candidates.StatusCode, await candidates.Content.ReadAsStringAsync());
            var options = (await candidates.Content.ReadFromJsonAsync<BootstrapBindingTargetOption[]>())!;
            Assert.IsTrue(options.Any(value => value.Name == binding.Target.Name && value.ScopeRef == binding.Target.ScopeRef));
            var request = new BootstrapProfilePreviewRequest([], target, [binding], Inputs: [participant]) { Seed = seed };
            using var missing = await host.Http.PostAsJsonAsync("/api/bootstrap/profiles/preview", request with { Bindings = [] });
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode, await missing.Content.ReadAsStringAsync());
            using var previewResponse = await host.Http.PostAsJsonAsync("/api/bootstrap/profiles/preview", request);
            Assert.AreEqual(HttpStatusCode.OK, previewResponse.StatusCode, await previewResponse.Content.ReadAsStringAsync());
            var preview = (await previewResponse.Content.ReadFromJsonAsync<BootstrapCompositionPreview>())!;
            Assert.IsTrue(preview.CanApply, JsonSerializer.Serialize(preview, Json));
            var review = preview.Resources.Single(value => value.Kind == "ProjectSharing").Details!.Single(value => value.Contract is not null);
            Assert.AreEqual("issues", review.Name); Assert.AreEqual(selection.Assignment.Service.Address.ScopeRef, review.Reference!.ScopeRef);
            using var denied = await host.Http.PostAsJsonAsync("/api/bootstrap/profiles/preview", request with
            { Bindings = [binding with { Target = binding.Target with { ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) } }] });
            Assert.AreEqual(HttpStatusCode.OK, denied.StatusCode, await denied.Content.ReadAsStringAsync());
            Assert.IsFalse((await denied.Content.ReadFromJsonAsync<BootstrapCompositionPreview>())!.CanApply);
            if (host == left)
            {
                var incompatibleBytes = NamedConnectionSeed(bytes, first.Requirement with { Contract = WorkProjectConnectionRules.Forge });
                using var incompatibleIngestion = await host.Http.PostAsJsonAsync("/api/bootstrap/workspace-seeds", new IngestWorkspaceSeedRequest(Guid.NewGuid(), target, Convert.ToBase64String(incompatibleBytes)));
                Assert.AreEqual(HttpStatusCode.Created, incompatibleIngestion.StatusCode, await incompatibleIngestion.Content.ReadAsStringAsync());
                var incompatibleReceipt = (await incompatibleIngestion.Content.ReadFromJsonAsync<WorkspaceSeedReceipt>())!;
                using var incompatiblePreview = await host.Http.PostAsJsonAsync("/api/bootstrap/profiles/preview", request with
                { Seed = new(incompatibleReceipt.Provenance.Id, incompatibleReceipt.Provenance.ArchiveDigest) });
                Assert.AreEqual(HttpStatusCode.OK, incompatiblePreview.StatusCode, await incompatiblePreview.Content.ReadAsStringAsync());
                Assert.IsFalse((await incompatiblePreview.Content.ReadFromJsonAsync<BootstrapCompositionPreview>())!.CanApply);
                using (host.Host.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
                {
                    var store = host.Host.Services.GetRequiredService<IResourceStore>();
                    var service = (await store.GetExactAsync<ConnectedServiceResource>(selection.Assignment.Service.Address, default))!;
                    var changed = (await store.PutExactAsync(ResourceScopeRef.Instance, service.Value with { Generation = service.Value.Generation + 1 }, service.ETag, false, default)).Value;
                    var policies = host.Host.Services.GetRequiredService<IResourceAccessPolicyService>();
                    var prior = await policies.GetAsync(changed.Uid, default);
                    await policies.PutAsync(new(new(changed.Uid, ResourceScopeRef.Instance, changed.Namespace, changed.Kind, changed.Generation),
                        [new(ResourceScopeRef.Workspace(host.Context.WorkspaceId), [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), prior?.ETag, default);
                }
                var stale = await ApplyNamedSeedAsync(host, request, preview.Digest);
                Assert.AreEqual(BootstrapApplicationStatus.Failed, stale.Definition.Status, JsonSerializer.Serialize(stale, Json));
                Assert.HasCount(0, stale.Definition.Resources);
                using (host.Scope()) Assert.IsFalse((await host.Host.Services.GetRequiredService<WorkProjectService>().FindAsync(new(host.Context.WorkspaceId), default, "seed-project", default)).IsSuccess);
                using var fresh = await host.Http.PostAsJsonAsync("/api/bootstrap/profiles/preview", request);
                preview = (await fresh.Content.ReadFromJsonAsync<BootstrapCompositionPreview>())!;
                Assert.IsTrue(preview.CanApply, JsonSerializer.Serialize(preview, Json));
            }
            if (host == left) native.LoseNextWriteResponse = true;
            var application = await ApplyNamedSeedAsync(host, request, preview.Digest);
            if (host == left)
            {
                Assert.AreEqual(BootstrapApplicationStatus.PartiallyApplied, application.Definition.Status, JsonSerializer.Serialize(application, Json));
                var publication = application.Definition.SeedHandover!.RequirementsPublicationId;
                Assert.IsNotNull(publication);
                var writtenRevision = (await native.GitAsync(native.RepositoryPath, "rev-parse", "main")).Trim();
                native.Unavailable = false;
                await host.RestartAsync();
                using (host.Scope()) await RegisterSeedWorkerAsync(host);
                using var resumedPreview = await host.Http.PostAsJsonAsync("/api/bootstrap/profiles/preview", request);
                Assert.AreEqual(HttpStatusCode.OK, resumedPreview.StatusCode, await resumedPreview.Content.ReadAsStringAsync());
                preview = (await resumedPreview.Content.ReadFromJsonAsync<BootstrapCompositionPreview>())!;
                Assert.IsTrue(preview.CanApply, JsonSerializer.Serialize(preview, Json));
                application = await ApplyNamedSeedAsync(host, request, preview.Digest);
                Assert.AreEqual(publication, application.Definition.SeedHandover!.RequirementsPublicationId);
                Assert.AreEqual(writtenRevision, (await native.GitAsync(native.RepositoryPath, "rev-parse", "main")).Trim());
            }
            Assert.AreEqual(BootstrapApplicationStatus.Succeeded, application.Definition.Status, JsonSerializer.Serialize(application, Json));
            Assert.IsNotNull(application.Definition.SeedHandover!.RequirementsDigest);
            using (host.Scope())
            {
                var project = (await host.Host.Services.GetRequiredService<WorkProjectService>().FindAsync(new(host.Context.WorkspaceId), default, "seed-project", default)).Value!;
                Assert.AreEqual(selection.Assignment.Service.Uid, project.Definition.ConnectionAssignments.Single().Service.Uid);
                Assert.HasCount(0, project.Definition.PlanIds);
                initialized.Add(WorkProjectContractMapping.ToProjectResponse(project));
            }
        }
        Assert.AreEqual(initialized[0].Sharing!.ProjectId, initialized[1].Sharing!.ProjectId);
        Assert.AreNotEqual(initialized[0].ConnectionAssignments.Single().Service.Uid, initialized[1].ConnectionAssignments.Single().Service.Uid);
        Assert.AreEqual(0, vendor.IssuePosts); Assert.AreEqual(0, vendor.LabelPosts);
    }

    private static async Task<BootstrapApplicationResource> ApplyNamedSeedAsync(SharingHost host, BootstrapProfilePreviewRequest request, string digest)
    {
        using var response = await host.Http.PostAsJsonAsync("/api/bootstrap/applications", new ApplyBootstrapProfilesRequest([], digest, request.Target,
            Bindings: request.Bindings, RequestId: Guid.NewGuid(), Inputs: request.Inputs)
        { Seed = request.Seed });
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        var application = (await response.Content.ReadFromJsonAsync<BootstrapApplicationResource>())!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (application.Definition.Status == BootstrapApplicationStatus.Running)
        {
            await Task.Delay(50, timeout.Token);
            application = (await host.Http.GetFromJsonAsync<BootstrapApplicationResource>($"/api/bootstrap/applications/{application.Name}", timeout.Token))!;
        }
        return application;
    }

    private static async Task ManageSeedDescribeAsync(SharingHost host)
    {
        using var actor = host.Scope();
        var store = host.Host.Services.GetRequiredService<IResourceStore>();
        var owner = ResourceScopeRef.Workspace(host.Context.WorkspaceId);
        var registration = (await store.GetExactAsync<ExtensionRegistrationResource>(new(owner, ResourceNamespace.Default,
            ExtensionKinds.ExtensionRegistration, "github-fixture"), default))!.Value;
        var authority = ResourceGovernance.Extension($"{owner.Value}/ExtensionRegistration/default/github-fixture", "Agentstration.Extensions.GitHub", "1.0.0", registration.Uid);
        using var governance = host.Host.Services.GetRequiredService<IResourceGovernanceContext>().Push(authority);
        var tools = host.Host.Services.GetRequiredService<ToolManagementService>();
        var sources = await tools.ListVisibleToolsAsync(owner, default);
        foreach (var source in sources.Where(value => value.Value.Definition.ExternalId is "github.tracking.describe" or "github.forge.describe"))
        {
            // Replace the legacy unmanaged fixture projection with the same public contribution's
            // normal enrollment-owned identity, without creating a second mapping for the operation.
            await store.DeleteExactAsync(new(owner, source.Value.Namespace, ToolResourceKinds.Tool, source.Value.Name), source.ETag, default);
            var managed = await store.PutExactAsync(owner, source.Value with { Governance = authority, Generation = 1 }, null, true, default);
            await tools.SetToolEnabledExactAsync(owner, managed.Value.Name, managed.Value.Namespace, true, null, default);
        }
    }

    private static byte[] NamedConnectionSeed(byte[] source, WorkProjectConnectionRequirement requirement, Action<JsonObject>? mutateProject = null)
    {
        using var input = new ZipArchive(new MemoryStream(source), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var entry in input.Entries)
            {
                using var reader = new StreamReader(entry.Open()); var text = reader.ReadToEnd();
                if (entry.FullName is "bootstrap/profile.yaml" or "bootstrap/20-project.yaml" or WorkspaceSeedFormat.ManifestPath)
                {
                    var node = JsonNode.Parse(ResourceManifestSerializer.FromYamlStrict<JsonElement>(text).GetRawText())!;
                    if (entry.FullName == WorkspaceSeedFormat.ManifestPath) node["importPlans"] = new JsonArray();
                    else if (entry.FullName == "bootstrap/profile.yaml")
                        node["definition"]!["bindings"] = JsonSerializer.SerializeToNode(new[] { new { name = "local-tracking", targetKind = "connectedService", displayName = "Local tracking account", required = true } }, Json);
                    else
                    {
                        node["definition"]!["connectionRequirements"] = JsonSerializer.SerializeToNode(new[] { requirement }, Json);
                        node["definition"]!["connectionAssignments"] = JsonSerializer.SerializeToNode(new[] { new
                    {
                        name = requirement.Name, service = new { binding = "local-tracking" },
                        describeTool = new { extensionContribution = new { kind = "Tool", extensionId = "Agentstration.Extensions.GitHub", contributionId = "github.tracking.describe" } },
                        exports = WorkProjectConnectionRules.TargetFields.Select(field => new { field, exportId = field }).ToArray()
                    } }, Json);
                    }
                    if (entry.FullName == "bootstrap/20-project.yaml") mutateProject?.Invoke(node["definition"]!.AsObject());
                    text = ResourceManifestSerializer.ToYaml(JsonSerializer.SerializeToElement(node, Json));
                }
                WriteSeed(archive, entry.FullName, text);
            }
        return output.ToArray();
    }
}
