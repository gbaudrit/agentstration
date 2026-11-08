using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Storage;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class DataSourceDatasetTests : ResourceManagementApiTestBase
{
    private static readonly ResourceScopeRef Owner = ResourceScopeRef.Instance;
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    [TestMethod]
    public async Task RealOperationAcquiresTwoIsolatedDatasetsIntoOneLocalStorageAndRetainsHistory()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var management = factory.Services.GetRequiredService<DataSourceManagementService>();
        var first = await management.CreateAsync(Source("first", fixture, JsonSerializer.SerializeToElement(new { dataset = "alpha" })), default);
        var second = await management.CreateAsync(Source("second", fixture, JsonSerializer.SerializeToElement(new { dataset = "beta" })), default);
        Assert.IsTrue((await management.GetReadinessAsync(first.Value.Namespace, first.Value.Name, Owner, default)).CanAcquire);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        var one = await AcquireAsync(client, first.Value.Name);
        var two = await AcquireAsync(client, second.Value.Name);
        Assert.AreNotEqual(one.Content!.Key, two.Content!.Key);
        Assert.AreEqual(one.Dataset!.Definition.Storage, two.Dataset!.Definition.Storage);
        Assert.AreEqual(one.Dataset.Definition.Acquisition.Operation, two.Dataset.Definition.Acquisition.Operation);
        Assert.AreNotEqual(one.Dataset.Partition, two.Dataset.Partition);
        var nested = Source("nested", fixture, JsonSerializer.SerializeToElement(new { dataset = "nested" }));
        await Assert.ThrowsAsync<DataSourceValidationException>(() => management.CreateAsync(nested with
        { Definition = nested.Definition with { Dataset = nested.Definition.Dataset! with { Rule = nested.Definition.Dataset.Rule with { Prefix = one.Dataset.Partition } } } }, default));
        Assert.IsNull(one.Composition);
        Assert.AreEqual(fixture.Operation.Uid, one.Dataset.Operation.Uid);
        Assert.AreEqual("alpha", await ReadDatasetAsync(client, one.Name));
        Assert.AreEqual("beta", await ReadDatasetAsync(client, two.Name));
        var history = await client.GetFromJsonAsync<DataSourceAcquisitionResource[]>("/api/datasources/first/acquisitions?scopeRef=/instance");
        Assert.HasCount(1, history!);
        Assert.AreEqual(one.Content.Version, history![0].Content!.Version);

        // Shared placement does not confer the other dataset's content authority.
        var gateway = factory.Services.GetRequiredService<IStorageContentGateway>();
        var target = DataSourceDatasetCompositionService.Target(one, Owner);
        await Assert.ThrowsAsync<Agentstration.Storage.StorageException>(() => gateway.ReadAsync(
            target, two.Content.Reference, 0, 100, default));
        using var overrideCall = await client.PostAsJsonAsync("/api/datasources/first/acquisitions?scopeRef=/instance",
            new StartDataSourceAcquisitionRequest { Parameters = JsonSerializer.SerializeToElement(new { dataset = "beta", store = "other" }) });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, overrideCall.StatusCode);

        var disabled = await factory.Services.GetRequiredService<ConnectedServiceOperationService>().SetEnabledAsync(
            fixture.OperationReference.Operation, false, fixture.Operation.ETag, default);
        using var refused = await client.PostAsJsonAsync("/api/datasources/first/acquisitions?scopeRef=/instance", new StartDataSourceAcquisitionRequest());
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.IsFalse(disabled.Value.Enabled);
        // Disabling acquisition never deletes an already acquired dataset or requires executing the origin to read it.
        Assert.AreEqual("alpha", await ReadDatasetAsync(client, one.Name));
        Assert.AreEqual(DataSourceAcquisitionState.Succeeded,
            (await factory.Services.GetRequiredService<DataSourceAcquisitionService>().GetAsync(ResourceNamespace.Default, one.Name, default)).Value.State);
        var bootstrap = await GetBootstrapContextAsync(factory);
        SelectConsoleContext(client, ResourceScopeRef.Workspace(bootstrap.WorkspaceId));
        using var foreign = await client.GetAsync($"/api/datasourceacquisitions/{one.Name}/content");
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [TestMethod]
    public async Task InvalidCompositionCannotInventParametersOverrideConnectionOrEscapeTheStorageEnvelope()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var management = factory.Services.GetRequiredService<DataSourceManagementService>();
        var source = Source("invalid", fixture, Empty);
        await Assert.ThrowsAsync<DataSourceValidationException>(() => management.CreateAsync(source, default));
        var valid = Source("valid", fixture, JsonSerializer.SerializeToElement(new { dataset = "alpha" }));
        foreach (var prefix in new[] { "../", "/", "foreign", "a/../../", "datasets/other/" })
            await Assert.ThrowsAsync<DataSourceValidationException>(() => management.CreateAsync(valid with
            {
                Definition = valid.Definition with
                {
                    Dataset = valid.Definition.Dataset! with
                    { Rule = valid.Definition.Dataset.Rule with { Prefix = prefix } }
                }
            }, default));
        await Assert.ThrowsAsync<DataSourceValidationException>(() => management.CreateAsync(valid with
        { Definition = valid.Definition with { Configuration = JsonSerializer.SerializeToElement(new { credential = "override" }) } }, default));
        await Assert.ThrowsAsync<DataSourceValidationException>(() => management.CreateAsync(valid with
        { Definition = valid.Definition with { Profile = new("legacy") } }, default));
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var originalStorage = (await store.GetExactAsync<StorageResource>(fixture.Storage.Storage, default))!.Value;
        var bounded = await factory.Services.GetRequiredService<Agentstration.Storage.StorageManagementService>().CreateAsync(originalStorage with
        {
            Uid = Guid.Empty,
            Governance = ResourceGovernance.User,
            Metadata = new() { Name = "bounded" },
            Definition = originalStorage.Definition with { Envelope = JsonSerializer.SerializeToElement(new { list = true, stat = true, read = true, write = true, prefix = "permitted/" }) }
        }, default);
        var selected = new StorageRevisionReference(new(Owner, bounded.Value.Namespace, bounded.Value.Kind, bounded.Value.Name),
            bounded.Value.Uid, bounded.Value.Generation, bounded.Value.ObservedState!.BindingDigest!);
        await Assert.ThrowsAsync<Agentstration.Storage.StorageException>(() => management.CreateAsync(valid with
        { Definition = valid.Definition with { Dataset = valid.Definition.Dataset! with { Storage = selected } } }, default));
    }

    [TestMethod]
    public async Task EmptyInputIsValidAndConcurrentIdempotentStartsHaveOneDurableIdentity()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services, emptyInput: true);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(Source("empty", fixture, Empty), default);
        var service = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var starts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.StartAsync(
            source.Value.Namespace, source.Value.Name, Owner, Empty, null, "same-request", default)));
        Assert.AreEqual(1, starts.Select(item => item.Value.Uid).Distinct().Count());
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        var completed = await WaitAsync(client, starts[0].Value.Name);
        Assert.AreEqual(DataSourceAcquisitionState.Succeeded, completed.State, completed.ErrorMessage);
        using var content = await client.GetAsync($"/api/datasourceacquisitions/{completed.Name}/content");
        Assert.AreEqual(HttpStatusCode.OK, content.StatusCode, await content.Content.ReadAsStringAsync());
    }

    private sealed record Fixture(RequestContext Context, ConnectedServiceOperationResource Operation,
        ConnectedServiceOperationRevisionReference OperationReference, StorageRevisionReference Storage);

    [TestMethod]
    public async Task PendingAcquisitionRejectsConcurrentStartAndCancellationPublishesNoContent()
    {
        await using var factory = Factory();
        Fixture fixture;
        await using (var worker = await AwpTestWorker.StartAsync(factory)) fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var source = (await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
            Source("cancel", fixture, JsonSerializer.SerializeToElement(new { dataset = "cancelled" })), default)).Value;
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var accepted = await acquisitions.StartAsync(source.Namespace, source.Name, Owner, Empty, null, null, default);
        var concurrent = await Assert.ThrowsAsync<DataSourceAcquisitionException>(() => acquisitions.StartAsync(
            source.Namespace, source.Name, Owner, Empty, null, null, default));
        Assert.AreEqual("data_source_acquisition_already_running", concurrent.Code);
        var cancelled = await acquisitions.CancelAsync(accepted.Value.Namespace, accepted.Value.Name, default);
        Assert.AreEqual(DataSourceAcquisitionState.Cancelled, cancelled.Value.State);
        Assert.IsNull(cancelled.Value.Content);
        await Assert.ThrowsAsync<DataSourceAcquisitionException>(() => factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>()
            .ReadAsync(cancelled.Value, 0, 100, default));
    }

    [TestMethod]
    public async Task ActualHostRestartRecoversAcceptedAcquisitionAndPreservesExactDatasetContentAndIdentities()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-dataset-restart-" + Guid.NewGuid().ToString("N"));
        Fixture fixture;
        DataSourceResource source;
        DataSourceAcquisitionResource accepted;
        try
        {
            await using (var first = Factory().WithWebHostBuilder(builder => builder.UseEnvironment("Development").UseSetting("Agentstration:Authentication:Mode", "Development")
                .UseSetting("Data:Directory", directory).UseSetting("Data:ControlPlanePath", Path.Combine(directory, "control-plane.db"))
                .UseSetting("Data:WorkPlanePath", Path.Combine(directory, "work-plane.db")).UseSetting("Data:FlowPath", Path.Combine(directory, "flow-plane.db"))
                .UseSetting("Data:RuntimePath", Path.Combine(directory, "runtime-plane.db"))))
            {
                await using (var worker = await AwpTestWorker.StartAsync(first)) fixture = await PrepareAsync(first.Services);
                using var scope = first.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
                source = (await first.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
                    Source("restart", fixture, JsonSerializer.SerializeToElement(new { dataset = "retained" })), default)).Value;
                accepted = (await first.Services.GetRequiredService<DataSourceAcquisitionService>().StartAsync(
                    source.Namespace, source.Name, Owner, Empty, null, "restart-request", default)).Value;
                Assert.AreEqual(DataSourceAcquisitionState.Pending, accepted.State);
            }
            await using (var second = Factory().WithWebHostBuilder(builder => builder.UseEnvironment("Development").UseSetting("Agentstration:Authentication:Mode", "Development")
                .UseSetting("Data:Directory", directory).UseSetting("Data:ControlPlanePath", Path.Combine(directory, "control-plane.db"))
                .UseSetting("Data:WorkPlanePath", Path.Combine(directory, "work-plane.db")).UseSetting("Data:FlowPath", Path.Combine(directory, "flow-plane.db"))
                .UseSetting("Data:RuntimePath", Path.Combine(directory, "runtime-plane.db"))))
            {
                await using var worker = await AwpTestWorker.StartAsync(second);
                var scopes = second.Services.GetRequiredService<IRequestContextScopeFactory>();
                using (scopes.PushSystem()) await second.Services.GetRequiredService<DataSourceAcquisitionService>().RecoverPendingAsync(default);
                using var caller = scopes.PushInstance(fixture.Context);
                DataSourceAcquisitionResource completed = accepted;
                for (var count = 0; count < 300; count++)
                {
                    completed = (await second.Services.GetRequiredService<DataSourceAcquisitionService>().GetAsync(source.Namespace, accepted.Name, default)).Value;
                    if (completed.State == DataSourceAcquisitionState.Succeeded) break;
                    Assert.IsNull(completed.ErrorCode, completed.ErrorMessage);
                    await Task.Delay(20);
                }
                Assert.AreEqual(DataSourceAcquisitionState.Succeeded, completed.State);
                Assert.AreEqual(accepted.Uid, completed.Uid);
                Assert.AreEqual(accepted.FlowRunId, completed.FlowRunId);
                Assert.AreEqual(source.Uid, completed.DataSourceUid);
                Assert.AreEqual(fixture.Storage.StorageUid, completed.Dataset!.Definition.Storage.StorageUid);
                Assert.AreEqual(fixture.Operation.Uid, completed.Dataset.Operation.Uid);
                var content = await second.Services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReadAsync(completed, 0, 65536, default);
                Assert.Contains("retained", Encoding.UTF8.GetString(Convert.FromBase64String(content.ContentBase64)));

            }
            // A second restart reads the retained bytes and receipts without a worker or another origin execution.
            await using (var third = Factory().WithWebHostBuilder(builder => builder.UseEnvironment("Development").UseSetting("Agentstration:Authentication:Mode", "Development")
                .UseSetting("Data:Directory", directory).UseSetting("Data:ControlPlanePath", Path.Combine(directory, "control-plane.db"))
                .UseSetting("Data:WorkPlanePath", Path.Combine(directory, "work-plane.db")).UseSetting("Data:FlowPath", Path.Combine(directory, "flow-plane.db"))
                .UseSetting("Data:RuntimePath", Path.Combine(directory, "runtime-plane.db"))))
            {
                _ = await GetBootstrapContextAsync(third);
                using var caller = third.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
                var acquisitions = third.Services.GetRequiredService<DataSourceAcquisitionService>();
                var history = await acquisitions.ListAsync(source.Namespace, source.Name, Owner, default);
                Assert.HasCount(1, history);
                Assert.AreEqual(accepted.Uid, history[0].Uid);
                var content = await third.Services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReadAsync(history[0], 0, 65536, default);
                Assert.Contains("retained", Encoding.UTF8.GetString(Convert.FromBase64String(content.ContentBase64)));

            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<Fixture> PrepareAsync(IServiceProvider services, bool emptyInput = false)
    {
        var bootstrap = await services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await services.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
        var workspace = await services.GetRequiredService<IOperationalTopologyProvisioner>().GetInstanceOperationsWorkspaceAsync(default);
        var context = new RequestContext(bootstrap.PrincipalId, workspace.TenantId, workspace.Id);
        using var scope = services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(context);
        var input = emptyInput ? JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false })
            : JsonSerializer.SerializeToElement(new { type = "object", properties = new { dataset = new { type = "string", minLength = 1 } }, required = new[] { "dataset" }, additionalProperties = false });
        var flow = await PublishAsync(services, "fixture-acquire", DataSourceDatasetCompositionService.FlowContract,
            emptyInput ? Empty : JsonSerializer.SerializeToElement(new { dataset = "${input.parameters.dataset}" }));
        var definitionSchema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = true });
        var type = await services.GetRequiredService<ConnectedServiceOperationTypeManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedServiceOperationType,
            Metadata = new() { Name = "fixture-operation" },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = "Fixture operation",
                PreferredVersion = "1.0.0",
                Versions =
            [new("1.0.0", AepSchemaDigest.Compute(definitionSchema), definitionSchema,
                ConnectedServiceOperationInteraction.RequestResponse, new("fixture.read", "1.0.0")) { Acquisition = new(flow) }]
            }
        }, default);
        var typeRef = new ConnectedServiceOperationTypeReference
        {
            Type = new(type.Value.Name, Owner),
            TypeUid = type.Value.Uid,
            Version = "1.0.0",
            SchemaDigest = type.Value.Definition.Versions[0].SchemaDigest
        };
        var discoveryFlow = await PublishAsync(services, "fixture-discover", ConnectedServiceOperationDiscoveryContracts.V1,
            JsonSerializer.SerializeToElement(new ConnectedServiceOperationDiscoveryManifest
            {
                Operations =
            [new() { ExternalId = "fixture.read", DisplayName = "Read fixture", Type = typeRef,
                Definition = JsonSerializer.SerializeToElement(new { readOnly = true, inputSchema = input, outputSchema = input }) }]
            }));
        var serviceTypes = services.GetRequiredService<ConnectedServiceTypeManagementService>();
        var version = new ConnectedServiceTypeVersionDescriptor("1.0.0", AepSchemaDigest.Compute(definitionSchema), definitionSchema, [], [])
        { OperationDiscovery = new() { Flow = discoveryFlow, ProducedTypes = [typeRef] } };
        var serviceType = await serviceTypes.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
            Metadata = new() { Name = "fixture-service" },
            ScopeRef = Owner,
            Definition = new() { DisplayName = "Fixture service", PreferredVersion = "1.0.0", Versions = [version] }
        }, default);
        var service = await services.GetRequiredService<ConnectedServiceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedService,
            Metadata = new() { Name = "fixture" },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = "Offline fixture",
                Type = new()
                {
                    Type = new(serviceType.Value.Name, Owner),
                    TypeUid = serviceType.Value.Uid,
                    Version = "1.0.0",
                    SchemaDigest = version.SchemaDigest
                },
                Options = Empty
            }
        }, default);
        var discoveries = services.GetRequiredService<ConnectedServiceOperationDiscoveryService>();
        var discovery = await discoveries.StartAsync(Owner, service.Value.Name, null, null, default);
        for (var count = 0; count < 300; count++)
        {
            discovery = await discoveries.GetAsync(ResourceNamespace.Default, discovery.Value.Name, default);
            if (discovery.Value.State == ConnectedServiceOperationDiscoveryState.Succeeded) break;
            if (discovery.Value.ErrorCode is not null) Assert.Fail(discovery.Value.ErrorMessage ?? discovery.Value.ErrorCode);
            await Task.Delay(20);
        }
        Assert.AreEqual(ConnectedServiceOperationDiscoveryState.Succeeded, discovery.Value.State, discovery.Value.ErrorMessage);
        var operation = (await discoveries.ListOperationsAsync(Owner, service.Value.Name, default)).Single();
        Assert.IsFalse(operation.Enabled);
        var address = new ScopedResourceAddress(Owner, operation.Namespace, operation.Kind, operation.Name);
        var stored = (await discoveries.GetOperationAsync(Owner, operation.Namespace, operation.Name, default))!;
        var enabled = await services.GetRequiredService<ConnectedServiceOperationService>().SetEnabledAsync(address, true, stored.ETag, default);
        var storage = (await services.GetRequiredService<IResourceStore>().GetExactAsync<StorageResource>(
            new(Owner, ResourceNamespace.Default, StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName), default))!.Value;
        return new(context, enabled.Value, new(address, enabled.Value.Uid, enabled.Value.Generation, enabled.Value.Definition.DefinitionDigest),
            new(new(Owner, storage.Namespace, storage.Kind, storage.Name), storage.Uid, storage.Generation, storage.ObservedState!.BindingDigest!));
    }

    private static DataSourceResource Source(string name, Fixture fixture, JsonElement parameters) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = DataSourceResourceKinds.DataSource,
        Metadata = new() { Name = name },
        ScopeRef = Owner,
        Definition = new() { DisplayName = name, Dataset = new(fixture.Storage, new(Write: true), new(fixture.OperationReference, parameters)) }
    };

    private static async Task<ConnectedServiceDiscoveryFlowReference> PublishAsync(IServiceProvider services, string name, string contract, JsonElement output)
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var flows = services.GetRequiredService<FlowService>();
        var flow = await flows.CreateExactAsync(Owner, new CreateFlowCommand(name, null, "1.0.0", true,
            new WorkflowFlowDefinition("input", [new FlowNode("input", FlowNodeKind.Input)], []),
            new Dictionary<string, string> { [FlowMetadataKeys.Contract] = contract }, new FlowGraphDefinition
            {
                EntryStep = "input",
                InputSchema = schema,
                OutputSchema = schema,
                Steps = [new InputFlowStepDefinition { Name = "input", Schema = schema },
                    new OutputFlowStepDefinition { Name = "output", OutputMapping = output }],
                Transitions = [new("input-output", "input", "completed", "output")]
            }), ResourceNamespace.Default, default);
        var version = await flows.PublishVersionExactAsync(Owner, new(name), "1.0.0", true, default);
        return new()
        {
            Flow = new(name, Owner),
            FlowUid = flow.Value.Uid,
            Version = "1.0.0",
            DefinitionHash = version.Value.DefinitionHash!,
            Contract = contract
        };
    }

    private static async Task<DataSourceAcquisitionResource> AcquireAsync(HttpClient client, string source)
    {
        using var response = await client.PostAsJsonAsync($"/api/datasources/{source}/acquisitions?scopeRef=/instance", new StartDataSourceAcquisitionRequest());
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await WaitAsync(client, (await response.Content.ReadFromJsonAsync<DataSourceAcquisitionResource>())!.Name);
    }

    private static async Task<DataSourceAcquisitionResource> WaitAsync(HttpClient client, string name)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var value = (await client.GetFromJsonAsync<DataSourceAcquisitionResource>($"/api/datasourceacquisitions/{name}"))!;
            if (value.State == DataSourceAcquisitionState.Succeeded) return value;
            Assert.IsNull(value.ErrorCode, value.ErrorMessage);
            await Task.Delay(20);
        }
        throw new AssertFailedException("The offline acquisition did not complete.");
    }

    private static async Task<string?> ReadDatasetAsync(HttpClient client, string name)
    {
        var content = (await client.GetFromJsonAsync<StorageObjectContent>($"/api/datasourceacquisitions/{name}/content"))!;
        using var json = JsonDocument.Parse(Convert.FromBase64String(content.ContentBase64));
        return json.RootElement.GetProperty("dataset").GetString();
    }
}
