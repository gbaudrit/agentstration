using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class StructuredStorageExecutionTests : ResourceManagementApiTestBase
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TwoAgentRelaysUseLateBoundExportsAndCannotSubstituteTheirCollection(bool postgres)
    {
        await using var product = Factory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" })));
        var context = await GetBootstrapContextAsync(product);
        using var caller = product.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
        using var secretClient = product.CreateClient();
        await using var fixture = await StructuredStorageFixture.StartAsync(product.Services, secretClient, owner, postgres);
        var gateway = product.Services.GetRequiredService<IStorageDocumentGateway>();
        var target = fixture.Target(owner);
        var description = await gateway.DescribeAsync(target, default);
        Assert.AreEqual(0, fixture.Bindings.Configurations, "Capability inspection must not resolve Secrets or connect.");
        var profile = description.QueryProfiles.Single();
        var exposure = product.Services.GetRequiredService<IStorageDocumentToolExposure>();
        var writer = (await exposure.PublishAsync(target, new("1.0.0", ["put"]), default)).Single();
        var reader = (await exposure.PublishAsync(target, new("1.0.0", ["query"], profile), default)).Single();
        var contracts = product.Services.GetRequiredService<IFlowToolContractResolver>();
        foreach (var tool in new[] { writer, reader })
        {
            var pinned = await contracts.ResolveAsync(owner, tool.Namespace ?? ResourceNamespace.Default,
                new(tool.Name, tool.Namespace, tool.ScopeRef), default);
            Assert.AreEqual("object", pinned.OutputSchema!.Value.GetProperty("type").GetString());
        }
        var agents = product.Services.GetRequiredService<AgentManagementService>();
        var runs = new Dictionary<string, string>(StringComparer.Ordinal);
        var runtime = product.Services.GetRequiredService<RuntimeRunService>();
        foreach (var (name, tool) in new[] { ("json-writer", writer), ("json-reader", reader) })
        {
            await agents.PutAgentAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = AgentResourceKinds.Agent,
                ScopeRef = owner,
                Metadata = new() { Name = name },
                Definition = new()
                {
                    DisplayName = name,
                    Instructions = "Use the assigned structured Tool.",
                    ModelProfile = new("reasoning-default"),
                    Tools = [tool]
                }
            }, null, true, default);
            await agents.CreateRevisionAsync(name, new() { Environment = "local", RuntimeProfileName = "maf-builtin", HostingMode = AgentHostingMode.InProcess }, default);
            var run = await runtime.CreateAsync(new(context.TenantId, new(context.WorkspaceId), context.PrincipalId),
                new(name, 1) { ScopeRef = owner }, new() { Messages = [new(RuntimeMessageRole.User, "Structured Tool conformance")] },
                new(), RuntimeRunOrigin.Api, "structured-conformance", default);
            runs.Add(name, run.Value.Id);
        }
        var executor = product.Services.GetRequiredService<IToolDefinitionExecutor>();
        ToolDefinitionInvocation Invocation(ResourceReference tool, string agent, object arguments) => new(context.TenantId, new(context.WorkspaceId),
            context.PrincipalId, tool.Namespace ?? ResourceNamespace.Default, tool.Name["agentstration.".Length..], Guid.NewGuid().ToString("N"), "structured-v2",
            JsonSerializer.SerializeToElement(arguments, JsonSerializerOptions.Web), ToolDefinitionCallerKind.Agent, agent, new(AgentId: agent, RuntimeRunId: runs[agent], AgentVersion: 1))
        { DefinitionScopeRef = owner };
        var write = new StorageDocumentWrite("agent-write", DateTimeOffset.UtcNow.AddMinutes(2), "observation",
            StructuredStorageFixture.Json("""{"status":"degraded","value":42}"""));
        var first = await executor.ExecuteAsync(Invocation(writer, "json-writer", new { request = new { requestId = write.RequestId, notAfter = write.NotAfter, id = write.Id, value = write.Value } }), default);
        Assert.AreEqual("observation", first.Output!.Value.GetProperty("id").GetString());
        var read = Invocation(reader, "json-reader", new { request = new { requestId = "agent-query", query = new Dictionary<string, string> { ["$filter"] = "value gt 10" } } });
        var result = await executor.ExecuteAsync(read, default);
        Assert.AreEqual("observation", result.Output!.Value.GetProperty("documents")[0].GetProperty("id").GetString());
        var privateCollection = fixture.Storage.Definition.Collections.Single(c => c.Name == "private").Uid;
        var attack = read with
        {
            Arguments = JsonSerializer.SerializeToElement(new
            {
                request = new { requestId = "substitute", query = new { } },
                target = target with { CollectionUid = privateCollection }
            })
        };
        await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => executor.ExecuteAsync(attack, default));
        var overrideProfile = read with { Arguments = JsonSerializer.SerializeToElement(new { request = new { requestId = "profile", query = new { }, profile = profile with { Id = "sql" } } }) };
        await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => executor.ExecuteAsync(overrideProfile, default));
        var resources = product.Services.GetRequiredService<IResourceStore>();
        var readerTool = (await resources.GetExactAsync<ToolResource>(new(owner, reader.Namespace ?? ResourceNamespace.Default, ToolResourceKinds.Tool, reader.Name), default))!.Value;
        Assert.IsFalse(readerTool.Definition.Schema!.Input.GetRawText().Contains("modelDigest", StringComparison.Ordinal));
        Assert.IsFalse(readerTool.Definition.Schema.Input.GetRawText().Contains("collectionUid", StringComparison.Ordinal));
        Assert.IsNotNull(fixture.Bindings.Last!.Connection.Single(v => v.RequirementId == "password").SecretGrant);
        Assert.AreEqual(1, fixture.Bindings.Last.DocumentCollections!.Value.GetArrayLength());
        Assert.AreEqual(fixture.Collection.ToString("N"), fixture.Bindings.Last.DocumentCollections.Value[0].GetProperty("id").GetString());
        var foreign = target with { Consumer = target.Consumer with { ConsumerScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) } };
        await Assert.ThrowsAsync<StorageException>(() => gateway.QueryAsync(foreign, new("foreign", profile, StructuredStorageFixture.Json("{}")), default));
        // An exact Storage revision remains pinned in both Tools after a management rebind.
        var stored = await product.Services.GetRequiredService<StorageManagementService>().GetExactAsync(owner, fixture.Storage.Namespace, fixture.Storage.Name, default);
        var management = product.Services.GetRequiredService<StorageManagementService>();
        var dependencies = await management.GetUsagesAsync(owner, fixture.Storage.Namespace, fixture.Storage.Name, default);
        Assert.IsTrue(dependencies.Any(d => d.Kind == ToolResourceKinds.ToolDefinitionVersion));
        Assert.AreEqual("storage_in_use", (await Assert.ThrowsAsync<StorageException>(() => management.DeactivateAsync(owner,
            fixture.Storage.Namespace, fixture.Storage.Name, stored!.ETag, default))).Code);
        await resources.PutExactAsync(owner, stored!.Value with
        {
            Generation = stored.Value.Generation + 1,
            Definition = stored.Value.Definition with { Enabled = false }
        }, stored.ETag, false, default);
        await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => executor.ExecuteAsync(read with { CallId = "disabled" }, default));
    }
    [TestMethod]
    public void StructuredArtifactIdentityIncludesPublicationWhileContentIdentityRemainsStable()
    {
        // The full publication/Artifact registration path is verified by V3; here the wire identity is the invariant.
        var scope = Agentstration.Resources.ResourceScopeRef.Workspace(Guid.NewGuid());
        var address = new Agentstration.Resources.ScopedResourceAddress(scope, default, StorageResourceKinds.Storage, "documents");
        var storage = new StorageRevisionReference(address, Guid.NewGuid(), 1, "binding");
        var consumer = new StorageResolutionContext(scope, address.Address);
        var target = new StorageContentRequest(storage, consumer, new());
        var content = new StoragePublicationReference(target, "doc", "revision", 2, new string('a', 64));
        Assert.AreEqual("publication:" + storage.StorageUid.ToString("N") + ":doc",
            Agentstration.Artifacts.Contracts.ArtifactStorageReferences.PublicationIdentity(content));
        var pin = new StorageQueryProfilePin("fixture", "1", "json", "model");
        var documents = new StorageDocumentTarget(storage, consumer, Guid.NewGuid(), new());
        StoragePublicationReference Publication(string name) => content with
        { Document = new(new(documents with { Publication = name }, new(name, "digest", 1, "model"), pin), "doc", "revision") };
        Assert.AreNotEqual(Agentstration.Artifacts.Contracts.ArtifactStorageReferences.PublicationIdentity(Publication("a")),
            Agentstration.Artifacts.Contracts.ArtifactStorageReferences.PublicationIdentity(Publication("b")));
    }
}
