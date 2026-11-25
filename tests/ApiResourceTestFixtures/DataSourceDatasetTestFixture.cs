using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Infrastructure.Storage;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Agentstration.Tools.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed partial class DataSourceDatasetTests : ResourceManagementApiTestBase
{
    private static readonly ResourceScopeRef Owner = ResourceScopeRef.Instance;
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    private sealed record Fixture(RequestContext Context, ConnectedServiceOperationResource Operation,
        ConnectedServiceOperationRevisionReference OperationReference, StorageRevisionReference Storage);

    private sealed class SealedOutputInterruption(KnowledgeOutputPublisher inner) : IKnowledgeOutputPublisher
    {
        public IReadOnlyList<KnowledgeSnapshotArtifact>? SealedContent { get; private set; }
        public async Task<IReadOnlyList<KnowledgeSnapshotArtifact>> PublishAsync(KnowledgeSourceResource source,
            KnowledgeProjectionResource projection, KnowledgeProjectionManifest manifest, CancellationToken cancellationToken)
        {
            var result = await inner.PublishAsync(source, projection, manifest, cancellationToken);
            if (SealedContent is null)
            {
                SealedContent = result;
                throw new KnowledgeProjectionException("knowledge_projection_publication_pending", "Injected interruption after actual output sealing.");
            }
            return result;
        }
    }

    private static async Task<Fixture> PrepareAsync(IServiceProvider services, bool emptyInput = false, JsonElement? operationResult = null, bool declareAcquisitionOutput = true, JsonElement? operationOutputSchema = null)
    {
        var bootstrap = await services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await services.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
        var workspace = await services.GetRequiredService<IOperationalTopologyProvisioner>().GetInstanceOperationsWorkspaceAsync(default);
        var context = new RequestContext(bootstrap.PrincipalId, workspace.TenantId, workspace.Id);
        using var scope = services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(context);
        var input = emptyInput ? JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false })
            : JsonSerializer.SerializeToElement(new { type = "object", properties = new { dataset = new { type = "string", minLength = 1 } }, required = new[] { "dataset" }, additionalProperties = false });
        var flow = await PublishAsync(services, "fixture-acquire", DataSourceDatasetCompositionService.FlowContract,
            operationResult ?? (emptyInput ? Empty : JsonSerializer.SerializeToElement(new { dataset = "${input.parameters.dataset}" })), declareAcquisitionOutput);
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
                Definition = JsonSerializer.SerializeToElement(new { readOnly = true, inputSchema = input, outputSchema = operationOutputSchema ?? (operationResult is null ? input : JsonSerializer.SerializeToElement(new { type = "object" })) }) }]
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

    private static async Task<ConnectedServiceDiscoveryFlowReference> PublishAsync(IServiceProvider services, string name, string contract, JsonElement output, bool declareOutput = true)
    {
        var schema = contract == KnowledgeFlowContracts.ArtifactTransformation
            ? JsonSerializer.SerializeToElement(new { type = "object", properties = new { bindingName = new { type = "string" }, dataSource = new { type = "object" }, artifacts = new { type = "array" }, configuration = new { type = "object" }, caller = new { type = "object" }, correlationId = new { type = "string" }, transformationId = new { type = "string" } }, required = new[] { "bindingName", "dataSource", "artifacts", "configuration", "caller", "correlationId", "transformationId" } })
            : JsonSerializer.SerializeToElement(new { type = "object" });
        if (contract == KnowledgeFlowContracts.Projection)
            schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { knowledgeSourceId = new { type = "string" }, knowledgeSourceUid = new { type = "string" }, knowledgeSourceGeneration = new { type = "integer" }, inputs = new { type = "array" }, artifacts = new { type = "array" }, parameters = new { type = "object" }, caller = new { type = "object" }, correlationId = new { type = "string" }, projectionId = new { type = "string" } }, required = new[] { "knowledgeSourceId", "knowledgeSourceUid", "knowledgeSourceGeneration", "inputs", "artifacts", "parameters", "caller", "correlationId", "projectionId" } });
        var outputSchema = contract is KnowledgeFlowContracts.ArtifactTransformation or KnowledgeFlowContracts.Projection
            ? JsonSerializer.SerializeToElement(new { type = "object", properties = new { artifacts = new { type = "array" } }, required = new[] { "artifacts" } }) : schema;
        var flows = services.GetRequiredService<FlowService>();
        var flow = await flows.CreateExactAsync(Owner, new CreateFlowCommand(name, null, "1.0.0", true,
            new WorkflowFlowDefinition("input", [new FlowNode("input", FlowNodeKind.Input)], []),
            new Dictionary<string, string> { [FlowMetadataKeys.Contract] = contract }, new FlowGraphDefinition
            {
                EntryStep = "input",
                InputSchema = schema,
                OutputSchema = declareOutput ? outputSchema : null,
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
        return json.RootElement[0].GetProperty("dataset").GetString();
    }
}
