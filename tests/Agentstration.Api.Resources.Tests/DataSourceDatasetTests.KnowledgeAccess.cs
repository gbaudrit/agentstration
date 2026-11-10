using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeKnowledgePreparesFromReadOrRegexWithoutRetrievalAndPreservesSnapshotContinuations(bool searchFirst)
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var origin = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
            Source("native-access-input", fixture, JsonSerializer.SerializeToElement(new { dataset = "native-access" })), default);
        const string text = "Début 😀 Nantes\nNantes fin";
        var transform = await PublishAsync(factory.Services, "native-access-unicode", KnowledgeFlowContracts.ArtifactTransformation,
            JsonSerializer.SerializeToElement(new
            {
                artifacts = new[] { new { artifactId = "unicode-document", kind = "inline",
                disposition = "publishable", mediaType = "text/plain", contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) } }
            }));
        var management = factory.Services.GetRequiredService<KnowledgeSourceManagementService>();
        var source = await management.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "native-access" },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = "Native access",
                Output = new(fixture.Storage, new(Write: true)),
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                DataSources = [new() { Name = "input", DataSource = new(origin.Value.Name, Owner), RefreshIfNecessary = true,
                    MaximumAge = TimeSpan.FromHours(1), TransformationFlow = new() { Name = transform.Flow.Name,
                        ScopeRef = Owner, Version = transform.Version, UseActiveVersion = false } }]
            }
        }, default);
        Assert.IsNull(source.Value.Composition!.RetrievalFlow);
        var id = new KnowledgeSourceId(source.Value.Name, ScopeRef: Owner);
        var access = factory.Services.GetRequiredService<KnowledgeAccessService>();
        var snapshots = factory.Services.GetRequiredService<KnowledgeSnapshotService>();
        var projections = factory.Services.GetRequiredService<KnowledgeProjectionService>();
        var status = await access.GetStatusAsync(id, new(), default);
        Assert.AreEqual("preparation_required", status.State);
        Assert.IsNull(status.ActiveSnapshotId);
        Assert.IsEmpty(await projections.ListAsync(id, default));
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        Assert.IsEmpty(await acquisitions.ListAsync(origin.Value.Namespace, origin.Value.Name, Owner, default));

        var initial = searchFirst
            ? await access.SearchRegexAsync(id, new() { Pattern = "Nantes", Limit = 1, ContextCharacters = 0 }, default)
            : await access.ReadAsync(id, new() { MaxCharacters = 7 }, default);
        var parallel = searchFirst
            ? await access.ReadAsync(id, new() { MaxCharacters = 7 }, default)
            : await access.SearchRegexAsync(id, new() { Pattern = "Nantes", Limit = 1, ContextCharacters = 0 }, default);
        var ready = await AwaitNativeAsync(initial, pending => searchFirst
            ? access.SearchRegexAsync(id, new() { Pattern = "Nantes", Limit = 1, ContextCharacters = 0, PreparationId = pending.PreparationId }, default)
            : access.ReadAsync(id, new() { MaxCharacters = 7, PreparationId = pending.PreparationId }, default));
        _ = await AwaitNativeAsync(parallel, pending => searchFirst
            ? access.ReadAsync(id, new() { MaxCharacters = 7, PreparationId = pending.PreparationId }, default)
            : access.SearchRegexAsync(id, new() { Pattern = "Nantes", Limit = 1, ContextCharacters = 0, PreparationId = pending.PreparationId }, default));
        Assert.HasCount(1, await projections.ListAsync(id, default));
        Assert.HasCount(1, await acquisitions.ListAsync(origin.Value.Namespace, origin.Value.Name, Owner, default));
        var first = ready.SnapshotId!;
        var page = await access.ReadAsync(id, new() { SnapshotId = first, MaxCharacters = 7 }, default);
        Assert.AreEqual("Début 😀", page.Chunks.Single().Content);
        Assert.AreEqual(new KnowledgeCharacterRange(0, 7), page.Chunks.Single().Range);
        Assert.IsNotNull(page.NextCursor);
        var found = await access.SearchRegexAsync(id, new() { SnapshotId = first, Pattern = "Nantes", Limit = 1, ContextCharacters = 0 }, default);
        Assert.AreEqual(new KnowledgeCharacterRange(8, 14), found.Matches.Single().MatchRange);
        Assert.AreEqual("Nantes", found.Matches.Single().Excerpt);
        var spaces = await access.SearchRegexAsync(id, new() { SnapshotId = first, Pattern = " " }, default);
        Assert.HasCount(3, spaces.Matches);
        Assert.IsTrue(spaces.Matches.All(value => value.MatchRange.End - value.MatchRange.Start == 1));
        var passage = await access.ReadAsync(id, new()
        {
            SnapshotId = first,
            DocumentId = found.Matches.Single().DocumentId,
            Range = found.Matches.Single().ReadRange,
            MaxCharacters = 3
        }, default);
        Assert.AreEqual("Nan", passage.Chunks.Single().Content);
        Assert.IsNotNull(passage.NextCursor);
        var restOfPassage = await access.ReadAsync(id, new() { Cursor = passage.NextCursor }, default);
        Assert.AreEqual("tes", restOfPassage.Chunks.Single().Content);
        Assert.IsTrue(restOfPassage.EndOfSelection);

        var second = (await projections.StartAsync(id, new() { IdempotencyKey = "native-access-second" }, default)).Value.SnapshotName!;
        Assert.AreNotEqual(first, second);
        var continued = await access.ReadAsync(id, new() { Cursor = page.NextCursor }, default);
        Assert.AreEqual(first, continued.SnapshotId);
        Assert.AreEqual(text, page.Chunks.Single().Content + continued.Chunks.Single().Content);
        var nextMatches = await access.SearchRegexAsync(id, new() { Cursor = found.NextCursor }, default);
        Assert.AreEqual(first, nextMatches.SnapshotId);
        Assert.AreEqual(new KnowledgeCharacterRange(15, 21), nextMatches.Matches.Single().MatchRange);
        status = await access.GetStatusAsync(id, new(first), default);
        Assert.AreEqual(second, status.ActiveSnapshotId);
        Assert.IsFalse(status.IsLatestPublished);
        Assert.IsFalse(status.IsActive);
        Assert.IsFalse(status.PreparationRequired);
        Assert.IsTrue(status.Documents.Single().TextReadable);
        await snapshots.SelectActiveAsync(id, first, default);
        status = await access.GetStatusAsync(id, new(first), default);
        Assert.IsTrue(status.IsActive);
        Assert.IsFalse(status.IsLatestPublished);
        Assert.AreEqual(second, status.LatestPublishedSnapshotId);

        var invalid = await Assert.ThrowsAsync<KnowledgeAccessException>(() => access.ReadAsync(id,
            new() { Cursor = page.NextCursor + "tampered" }, default));
        Assert.AreEqual("knowledge_access_cursor_invalid", invalid.Code);
        var mixed = await Assert.ThrowsAsync<KnowledgeAccessException>(() => access.ReadAsync(id,
            new() { Cursor = page.NextCursor, SnapshotId = second }, default));
        Assert.AreEqual("knowledge_access_cursor_parameters", mixed.Code);
        var unsupported = await Assert.ThrowsAsync<KnowledgeAccessException>(() => access.SearchRegexAsync(id,
            new() { SnapshotId = first, Pattern = "(?=Nantes)" }, default));
        Assert.AreEqual("knowledge_access_pattern_invalid", unsupported.Code);
        var empty = await access.SearchRegexAsync(id, new() { SnapshotId = first, Pattern = "^|$", Limit = 1 }, default);
        var finalEmpty = await access.SearchRegexAsync(id, new() { Cursor = empty.NextCursor, Limit = 10 }, default);
        Assert.HasCount(1, finalEmpty.Matches);
        Assert.IsTrue(finalEmpty.EndOfSelection);
        var other = await management.CreateAsync(source.Value with { Metadata = new() { Name = "another-native-source" }, Uid = Guid.Empty }, default);
        var wrongSource = await Assert.ThrowsAsync<KnowledgeAccessException>(() => access.ReadAsync(new(other.Value.Name, ScopeRef: Owner),
            new() { Cursor = page.NextCursor }, default));
        Assert.AreEqual("knowledge_access_cursor_invalid", wrongSource.Code);

        var missing = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => factory.Services.GetRequiredService<KnowledgeContentReadMcpTool>()
            .ExecuteAsync(new(fixture.Context.TenantId, new(fixture.Context.WorkspaceId), fixture.Context.PrincipalId,
                "missing-source-call", null, JsonSerializer.SerializeToElement(new
                { source = new { name = "missing-native-source", @namespace = "default", scopeRef = Owner.Value, uid = source.Value.Uid }, request = new { } }),
                ToolDefinitionCallerKind.Console), default));
        Assert.AreEqual("knowledge_access_source_not_found", missing.Code);

        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        var sourcePolicy = await policies.PutAsync(new(new(source.Value.Uid, Owner, source.Value.Namespace, source.Value.Kind),
            [new(ResourceScopeRef.Tenant(fixture.Context.TenantId), [ResourceAccessAction.Discover, ResourceAccessAction.Use], true)]), null, default);
        using (scopes.Push(fixture.Context))
        {
            var wrongConsumer = await Assert.ThrowsAsync<KnowledgeAccessException>(() => access.ReadAsync(id,
                new() { Cursor = page.NextCursor }, default));
            Assert.AreEqual("knowledge_access_cursor_invalid", wrongConsumer.Code);
        }
        _ = await policies.PutAsync(new(new(source.Value.Uid, Owner, source.Value.Namespace, source.Value.Kind), []), sourcePolicy.ETag, default);
        using (scopes.Push(fixture.Context))
        {
            var revoked = await Assert.ThrowsAsync<KnowledgeAccessException>(() => access.ReadAsync(id,
                new() { Cursor = page.NextCursor }, default));
            Assert.AreEqual("knowledge_access_source_not_found", revoked.Code);
        }
        var disabled = await management.PutExactAsync(Owner, id, source.Value.Definition with { Enabled = false }, source.ETag, default);
        Assert.AreEqual("disabled", (await access.GetStatusAsync(id, new(), default)).State);
        var disabledRead = await Assert.ThrowsAsync<KnowledgeAccessException>(() => access.ReadAsync(id, new() { Cursor = page.NextCursor }, default));
        Assert.AreEqual("knowledge_access_source_disabled", disabledRead.Code);
        _ = await management.PutExactAsync(Owner, id, source.Value.Definition, disabled.ETag, default);

        var clock = new NativeAccessClock(TimeProvider.System.GetUtcNow());
        var expiring = ActivatorUtilities.CreateInstance<KnowledgeAccessService>(factory.Services, clock);
        var shortPage = await expiring.ReadAsync(id, new() { SnapshotId = first, MaxCharacters = 1 }, default);
        clock.Now += KnowledgeAccessService.SessionLifetime + TimeSpan.FromSeconds(1);
        var expired = await Assert.ThrowsAsync<KnowledgeAccessException>(() => expiring.ReadAsync(id, new() { Cursor = shortPage.NextCursor }, default));
        Assert.AreEqual("knowledge_access_cursor_expired", expired.Code);

        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        using var inspected = await client.PostAsJsonAsync($"/api/knowledgesources/{id.Value}/content/status?scopeRef=/instance", new KnowledgeStatusRequest(first));
        Assert.AreEqual(HttpStatusCode.OK, inspected.StatusCode, await inspected.Content.ReadAsStringAsync());
        using var exposed = await client.PostAsJsonAsync($"/api/knowledgesources/{id.Value}/tool-exposure?scopeRef=/instance", new PublishKnowledgeSourceToolExposureRequest());
        Assert.AreEqual(HttpStatusCode.Created, exposed.StatusCode, await exposed.Content.ReadAsStringAsync());
        var exposure = (await exposed.Content.ReadFromJsonAsync<KnowledgeSourceToolExposureResource>())!;
        CollectionAssert.AreEquivalent(new[] { KnowledgeSourceOperation.Read, KnowledgeSourceOperation.SearchRegex, KnowledgeSourceOperation.GetStatus }, exposure.Operations.Select(value => value.Operation).ToArray());
        foreach (var operation in exposure.Operations)
        {
            var definitionName = $"{id.Value}.access.{operation.Route.Replace('_', '-')}";
            var definition = await factory.Services.GetRequiredService<ToolDefinitionService>().GetExactAsync(definitionName, ResourceNamespace.Default, Owner, default);
            Assert.IsInstanceOfType<RelayToolDefinitionProperties>(definition!.Value.Definition);
        }
    }

    private static async Task<KnowledgeAccessResult> AwaitNativeAsync(KnowledgeAccessResult result,
        Func<KnowledgeAccessResult, Task<KnowledgeAccessResult>> retry)
    {
        for (var attempt = 0; result.State == "preparing" && attempt < 100; attempt++)
        {
            await Task.Delay(50);
            result = await retry(result);
        }
        Assert.AreEqual("ready", result.State, result.ErrorCode);
        Assert.IsNotNull(result.SnapshotId);
        return result;
    }
    private sealed class NativeAccessClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
