using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.DataSources;
using Agentstration.Infrastructure.Storage;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    public async Task NamedQueriesEvolveWithoutRepublishingToolsOrFlowsAndPreserveExactVersions()
    {
        await using var factory = Factory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" })));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        using var client = factory.CreateClient(); SelectConsoleContext(client, Owner);
        await using var storage = await StructuredStorageFixture.StartAsync(factory.Services, client, Owner, false,
            StructuredStorageFixture.Json("""{"type":"object","properties":{"dataset":{"type":"string"}}}"""));
        var desired = Source("named-query-source", fixture, JsonSerializer.SerializeToElement(new { dataset = "alpha" }));
        desired = desired with { Definition = desired.Definition with { Dataset = desired.Definition.Dataset! with
        { Storage = storage.Reference, DocumentCollectionUid = storage.Collection, Lifecycle = new() { KeepHistory = true, HistoryRetention = TimeSpan.FromDays(1) } } } };
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(desired, default);
        var acquisition = await AcquireAsync(client, source.Value.Name);
        var selection = (await factory.Services.GetRequiredService<DataSourceDocumentAccessService>().DescribeAsync(default, acquisition.Name, default)).Selection;
        var query = new DataSourceQueryResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = DataSourceResourceKinds.DataSourceQuery, ScopeRef = Owner,
            Metadata = new() { Name = "only-alpha" },
            Definition = new() { DisplayName = "Only alpha", Selection = selection, Query = StructuredStorageFixture.Json("""{"$filter":"dataset eq 'alpha'"}""") }
        };
        using var created = await client.PostAsJsonAsync("/api/datasourcequeries", query);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        query = (await created.Content.ReadFromJsonAsync<DataSourceQueryResource>())!;
        var service = factory.Services.GetRequiredService<DataSourceQueryService>();
        var a = await service.PublishAsync(Owner, query.Namespace, query.Name, "9.0.0", created.Headers.ETag!.ToString(), default);
        var reference = new DataSourceQueryReference { Query = new(query.Name, Owner, query.Namespace), QueryUid = query.Uid };
        var tools = factory.Services.GetRequiredService<DataSourceQueryToolService>();
        var dynamicTool = await tools.PublishAsync(reference, new("alpha-dynamic", "Alpha dynamic", "1.0.0"), default);
        var exactTool = await tools.PublishAsync(reference, new("alpha-exact", "Alpha exact", "1.0.0", "9.0.0"), default);
        var definitions = factory.Services.GetRequiredService<ToolDefinitionService>();
        var publishedBefore = (await definitions.GetInvocationAsync("alpha-dynamic.v1.0.0", default, default, Owner))!;
        var flows = factory.Services.GetRequiredService<FlowService>();
        var flow = await flows.CreateExactAsync(Owner, new CreateFlowCommand("named-query-consumer", null, "1.0.0", true,
            new WorkflowFlowDefinition("input", [new FlowNode("input", FlowNodeKind.Input)], []), Graph: new()
            {
                EntryStep = "input", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                Steps = [new InputFlowStepDefinition { Name = "input" },
                    new ToolFlowStepDefinition { Name = "invoke", Tool = new(dynamicTool.Name, ScopeRef: Owner), ArgumentsMapping = Empty },
                    new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.invoke.output}") }],
                Transitions = [new("input-tool", "input", "completed", "invoke"), new("tool-output", "invoke", "success", "output")]
            }), default, default);
        var flowVersion = await flows.PublishVersionExactAsync(Owner, flow.Value.Id, "1.0.0", true, default);
        var first = await RunNamedQueryToolAsync(client, dynamicTool);
        Assert.AreEqual("9.0.0", first.Query.Version); Assert.HasCount(1, first.Result.Page.Documents);
        var genericName = AgentstrationToolProvider.ToolResourceName(ToolDefinitionService.VersionName(DataSourceQueryToolService.GenericName, DataSourceQueryToolService.GenericVersion));
        using var generic = await client.PostAsJsonAsync($"/api/tools/{genericName}/run?scopeRef=/instance",
            new RunToolRequest(JsonSerializer.SerializeToElement(new { query = reference }, JsonSerializerOptions.Web), ToolRunMode.Execute));
        Assert.AreEqual(HttpStatusCode.OK, generic.StatusCode, await generic.Content.ReadAsStringAsync());
        var draft = await service.UpdateAsync(Owner, query.Namespace, query.Name, query.Definition with
        { Query = StructuredStorageFixture.Json("""{"$filter":"dataset eq 'missing'"}""") }, created.Headers.ETag.ToString(), default);
        Assert.HasCount(1, (await RunNamedQueryToolAsync(client, dynamicTool)).Result.Page.Documents, "Draft edits must have no effect.");
        var b = await service.PublishAsync(Owner, query.Namespace, query.Name, "1.0.0", draft.ETag, default);
        Assert.AreEqual(a.ContractDigest, b.ContractDigest);
        Assert.IsTrue(b.PublishedAt > a.PublishedAt);
        var after = await RunNamedQueryToolAsync(client, dynamicTool);
        Assert.AreEqual("1.0.0", after.Query.Version, "Latest means publication order, not semantic version order.");
        Assert.HasCount(0, after.Result.Page.Documents);
        Assert.HasCount(1, (await RunNamedQueryToolAsync(client, exactTool)).Result.Page.Documents);

        using var runResponse = await client.PostAsJsonAsync("/api/flows/named-query-consumer/runs?scopeRef=/instance",
            new CreateFlowRunRequest(Empty, "1.0.0") { DefinitionScopeRef = Owner, ExecutionScopeRef = Owner });
        Assert.AreEqual(HttpStatusCode.Accepted, runResponse.StatusCode, await runResponse.Content.ReadAsStringAsync());
        var run = (await runResponse.Content.ReadFromJsonAsync<FlowRun>())!;
        for (var i = 0; i < 600 && run.Status is FlowRunStatus.Pending or FlowRunStatus.Running; i++)
        {
            await Task.Delay(20); run = (await client.GetFromJsonAsync<FlowRun>($"/api/flowRuns/{run.Id}"))!;
        }
        Assert.AreEqual(FlowRunStatus.Succeeded, run.Status, JsonSerializer.Serialize(run));
        var output = run.Output!.Value.Deserialize<DataSourceQueryExecutionResult>(JsonSerializerOptions.Web)!;
        Assert.AreEqual("1.0.0", output.Query.Version); Assert.HasCount(0, output.Result.Page.Documents);
        Assert.AreEqual(publishedBefore.Value.Uid, (await definitions.GetInvocationAsync("alpha-dynamic.v1.0.0", default, default, Owner))!.Value.Uid);
        Assert.AreEqual(flowVersion.Value.DefinitionHash, (await flows.GetVersionExactAsync(Owner, flow.Value.Id, "1.0.0", default))!.Value.DefinitionHash);
        var publications = await client.GetFromJsonAsync<DataSourceQueryToolPublication[]>($"/api/datasourcequeries/tools?scopeRef=/instance&sourceUid={source.Value.Uid}");
        Assert.IsNotNull(publications);
        Assert.HasCount(2, publications);
        Assert.IsFalse(publications.Any(p => p.Generic));
        Assert.IsTrue(publications.All(p => p.Definition is { ScopeRef: { } scope } && scope == Owner));
        Assert.IsTrue(publications.Any(p => p.Definition?.Name == "alpha-dynamic"));
        Assert.IsTrue(publications.Any(p => p.Definition?.Name == "alpha-exact"));
        Assert.IsTrue(publications.Any(p => p.SelectedQueryVersion is null));
        Assert.IsTrue(publications.Any(p => p.SelectedQueryVersion == "9.0.0"));
        var editable = publications.Single(p => p.Definition?.Name == "alpha-exact").Definition!;
        var editPath = $"/api/tooldefinitions/{editable.Name}?namespace={editable.Namespace}&scopeRef=/instance";
        using var definitionResponse = await client.GetAsync(editPath);
        definitionResponse.EnsureSuccessStatusCode();
        var beforeEdit = (await definitionResponse.Content.ReadFromJsonAsync<ToolDefinitionResource>())!;
        var changed = (RelayToolDefinitionProperties)beforeEdit.Definition with
        { DisplayName = "Edited alpha query", Version = "2.0.0", Publish = false };
        using var editRequest = new HttpRequestMessage(HttpMethod.Put, editPath)
        { Content = JsonContent.Create(new PutToolDefinitionRequest(changed)) };
        editRequest.Headers.IfMatch.Add(definitionResponse.Headers.ETag!);
        using var editResponse = await client.SendAsync(editRequest);
        Assert.AreEqual(HttpStatusCode.OK, editResponse.StatusCode, await editResponse.Content.ReadAsStringAsync());
        var afterEdit = (await editResponse.Content.ReadFromJsonAsync<ToolDefinitionResource>())!;
        Assert.AreEqual(beforeEdit.Uid, afterEdit.Uid);
        Assert.AreEqual(query.Uid.ToString("D"), afterEdit.Metadata.Annotations["agentstration.io/query-uid"]);
        using var republish = await client.PostAsJsonAsync($"/api/tooldefinitions/{editable.Name}/versions?namespace={editable.Namespace}",
            new PublishToolDefinitionRequest("2.0.0", editResponse.Headers.ETag!.ToString()));
        Assert.AreEqual(HttpStatusCode.Created, republish.StatusCode, await republish.Content.ReadAsStringAsync());
        var reloaded = (await client.GetFromJsonAsync<DataSourceQueryToolPublication[]>($"/api/datasourcequeries/tools?scopeRef=/instance&sourceUid={source.Value.Uid}"))!;
        Assert.HasCount(3, reloaded);
        Assert.IsTrue(reloaded.Any(p => p.Definition?.Name == editable.Name && p.ToolVersion == "2.0.0" && p.DisplayName == changed.DisplayName));
        Assert.IsTrue(reloaded.Any(p => p.Definition?.Name == editable.Name && p.ToolVersion == "1.0.0" && p.DisplayName == beforeEdit.Definition.DisplayName));
        var invalid = await Assert.ThrowsAsync<DataSourceValidationException>(() => service.PublishAsync(Owner, query.Namespace, query.Name, "9.0.0", draft.ETag, default));
        Assert.AreEqual("data_source_query_version_immutable", invalid.Code);
        draft = await service.UpdateAsync(Owner, query.Namespace, query.Name, draft.Value.Definition with
        { Query = StructuredStorageFixture.Json("""{"$select":"dataset"}""") }, draft.ETag, default);
        _ = await service.PublishAsync(Owner, query.Namespace, query.Name, "2.0.0", draft.ETag, default);
        using var incompatible = await client.PostAsJsonAsync($"/api/tools/{dynamicTool.Name}/run?scopeRef=/instance", new RunToolRequest(Empty, ToolRunMode.Execute));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, incompatible.StatusCode);
        Assert.Contains("data_source_query_contract_mismatch", await incompatible.Content.ReadAsStringAsync());
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context))
        {
            var denied = await Assert.ThrowsAsync<DataSourceValidationException>(() => service.ExecuteAsync(reference, new(), null, null, default));
            Assert.AreEqual("data_source_query_not_found", denied.Code);
        }
        var policyStore = factory.Services.GetRequiredService<IResourceAccessPolicyStore>();
        var workspace = ResourceScopeRef.Workspace(fixture.Context.WorkspaceId);
        var policy = await policyStore.PutAsync(new(new(query.Uid, Owner, query.Namespace, query.Kind),
            [new(workspace, [Agentstration.ResourceManagement.Contracts.ResourceAccessAction.Discover])]), null, default);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context))
        {
            var denied = await Assert.ThrowsAsync<DataSourceValidationException>(() => service.ResolveAsync(reference, default));
            Assert.AreEqual("data_source_query_access_denied", denied.Code);
        }
        _ = await policyStore.PutAsync(policy.Policy with { Grants = [new(workspace,
            [Agentstration.ResourceManagement.Contracts.ResourceAccessAction.Discover, Agentstration.ResourceManagement.Contracts.ResourceAccessAction.Use])] }, policy.ETag, default);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context))
        {
            var shared = await service.ResolveAsync(reference with { Version = "9.0.0" }, default);
            Assert.AreEqual(a.Uid, shared.Uid, "Query Use authorizes its own exact versions without granting a separate version resource.");
        }
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context with
        { Restriction = new(Guid.NewGuid(), fixture.Context.WorkspaceId, new HashSet<string> { AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.RunsRead }) }))
        {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ExecuteAsync(reference, new(), null, null, default));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.PublishAsync(Owner, default, query.Name, "blocked", draft.ETag, default));
        }
        draft = await service.UpdateAsync(Owner, query.Namespace, query.Name, draft.Value.Definition with { Enabled = false }, draft.ETag, default);
        using var disabled = await client.PostAsJsonAsync($"/api/tools/{exactTool.Name}/run?scopeRef=/instance", new RunToolRequest(Empty, ToolRunMode.Execute));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, disabled.StatusCode);
        Assert.Contains("data_source_query_unavailable", await disabled.Content.ReadAsStringAsync());
        var store = factory.Services.GetRequiredService<IResourceStore>();
        await store.DeleteExactAsync(new(Owner, query.Namespace, query.Kind, query.Name), draft.ETag, default);
        _ = await service.CreateAsync(query, default);
        var replaced = await Assert.ThrowsAsync<DataSourceValidationException>(() => service.ExecuteAsync(reference, new(), null, null, default));
        Assert.AreEqual("data_source_query_unavailable", replaced.Code);
    }

    [TestMethod]
    public async Task NamedQueryContinuationPinsThePublishedQueryAndAcquisitionAcrossNewPublications()
    {
        await using var factory = Factory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" }))
            .ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<IStorageDocumentGateway>(sp =>
                new PagedAcquisitionGateway(ActivatorUtilities.CreateInstance<StorageDocumentGateway>(sp))))));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        using var client = factory.CreateClient(); SelectConsoleContext(client, Owner);
        await using var storage = await StructuredStorageFixture.StartAsync(factory.Services, client, Owner, false,
            StructuredStorageFixture.Json("""{"type":"object","properties":{"dataset":{"type":"string"}}}"""));
        var desired = Source("query-pagination", fixture, JsonSerializer.SerializeToElement(new { dataset = "alpha" }));
        desired = desired with { Definition = desired.Definition with { Dataset = desired.Definition.Dataset! with
        { Storage = storage.Reference, DocumentCollectionUid = storage.Collection, Lifecycle = new() { KeepHistory = true, HistoryRetention = TimeSpan.FromDays(1) } } } };
        var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
        var source = await sources.CreateAsync(desired, default);
        var a = await AcquireAsync(client, source.Value.Name);
        var description = await factory.Services.GetRequiredService<DataSourceDocumentAccessService>().DescribeAsync(default, a.Name, default);
        var service = factory.Services.GetRequiredService<DataSourceQueryService>();
        var query = await service.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = DataSourceResourceKinds.DataSourceQuery, ScopeRef = Owner,
            Metadata = new() { Name = "paginated-alpha" }, Definition = new()
            { DisplayName = "Paginated alpha", Selection = description.Selection, Query = StructuredStorageFixture.Json("""{"$filter":"dataset eq 'alpha'"}""") }
        }, default);
        _ = await service.PublishAsync(Owner, default, query.Value.Name, "A", query.ETag, default);
        var reference = new DataSourceQueryReference { Query = new(query.Value.Name, Owner, ResourceNamespace.Default), QueryUid = query.Value.Uid };
        ((PagedAcquisitionGateway)factory.Services.GetRequiredService<IStorageDocumentGateway>()).PaginateNext = true;
        var page1 = await service.ExecuteAsync(reference, new(), null, null, default);
        Assert.IsNotNull(page1.NextCursor); Assert.IsNull(page1.Result.NextCursor);
        var changed = await service.UpdateAsync(Owner, default, query.Value.Name, query.Value.Definition with
        { Query = StructuredStorageFixture.Json("""{"$filter":"dataset eq 'beta'"}""") }, query.ETag, default);
        _ = await service.PublishAsync(Owner, default, query.Value.Name, "B", changed.ETag, default);
        _ = await sources.PutAsync(source.Value.Namespace, source.Value.Name, Owner, source.Value.Definition with
        { Dataset = source.Value.Definition.Dataset! with { Acquisition = source.Value.Definition.Dataset.Acquisition with
        { Parameters = JsonSerializer.SerializeToElement(new { dataset = "beta" }) } } }, source.ETag, default);
        var b = await AcquireAsync(client, source.Value.Name);
        var tampered = await Assert.ThrowsAsync<DataSourceValidationException>(() => service.ExecuteAsync(reference, new() { Cursor = "broken" }, null, null, default));
        Assert.AreEqual("data_source_query_cursor_invalid", tampered.Code);
        var parameters = await Assert.ThrowsAsync<DataSourceValidationException>(() => service.ExecuteAsync(reference, new() { Cursor = page1.NextCursor, PageSize = 1 }, null, null, default));
        Assert.AreEqual("data_source_query_cursor_invalid", parameters.Code);
        var page2 = await service.ExecuteAsync(reference, new() { Cursor = page1.NextCursor }, null, null, default);
        Assert.AreEqual("A", page2.Query.Version); Assert.AreEqual(a.Uid, page2.Result.AcquisitionUid);
        Assert.AreEqual("alpha", page2.Result.Page.Documents.Single().Value.GetProperty("dataset").GetString());
        var latest = await service.ExecuteAsync(reference, new(), null, null, default);
        Assert.AreEqual("B", latest.Query.Version); Assert.AreEqual(b.Uid, latest.Result.AcquisitionUid);
    }

    private static async Task<DataSourceQueryExecutionResult> RunNamedQueryToolAsync(HttpClient client, ResourceReference tool)
    {
        using var response = await client.PostAsJsonAsync($"/api/tools/{tool.Name}/run?scopeRef=/instance", new RunToolRequest(Empty, ToolRunMode.Execute));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var run = (await response.Content.ReadFromJsonAsync<RunToolResponse>())!;
        Assert.AreEqual("completed", run.Status, JsonSerializer.Serialize(run));
        return run.Output!.Value.Deserialize<DataSourceQueryExecutionResult>(JsonSerializerOptions.Web)!;
    }
}
