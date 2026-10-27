using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Artifacts;

public sealed class ArtifactPlatformResourceProvisioner(
    IResourceStore store,
    FlowService flows,
    TimeProvider timeProvider,
    IResourceGovernanceContext governance)
{
    public const string DefaultBindingName = "artifact-staging-filesystem-builtin";
    public const string DefaultToolSetName = "artifact-staging-filesystem-builtin";
    public const string DefaultToolSetVersion = "1.0.0";
    public const string BrokerToolSetName = "staged-artifacts-builtin";
    public const string StorageWriteFlowName = "artifact-storage-filesystem-write-builtin";
    public const string StorageReadFlowName = "artifact-storage-filesystem-read-builtin";

    private const string LegacyDefaultBindingName = "filesystem-default";

    public async Task EnsureAsync(ResourceScopeRef scope, CancellationToken cancellationToken)
    {
        if (scope.Kind is not ResourceScopeKind.Instance and not ResourceScopeKind.Workspace)
            throw new InvalidOperationException("Built-in Artifact resources require an Instance or Workspace scope.");
        using var governanceScope = governance.Push(ResourceGovernance.Core("artifacts.platform"));
        if (scope.Kind == ResourceScopeKind.Instance)
        {
            var toolSet = await EnsureToolSetAsync(scope, cancellationToken);
            await EnsureVersionAsync(scope, toolSet, cancellationToken);
            var broker = await EnsureBrokerToolSetAsync(scope, cancellationToken);
            await EnsureBrokerVersionAsync(scope, broker, cancellationToken);
            await EnsureStorageFlowsAsync(scope, cancellationToken);
            return;
        }

        await DemoteLegacyDefaultBindingAsync(scope, cancellationToken);
        await EnsureBindingAsync(scope, cancellationToken);
    }

    private async Task<ToolSetResource> EnsureToolSetAsync(ResourceScopeRef scope, CancellationToken cancellationToken)
    {
        var address = ScopedResourceAddress.Create(scope, ResourceNamespace.Default, ToolResourceKinds.ToolSet, DefaultToolSetName);
        if (await store.GetExactAsync<ToolSetResource>(address, cancellationToken) is { } existing)
            return existing.Value;

        var members = ArtifactCapabilities.All.Select(capability => new ToolSetMember
        {
            Tool = new ResourceReference(AgentstrationToolProvider.ToolResourceName(capability), scope, ResourceNamespace.Default),
            Capability = capability,
            Route = "default"
        }).ToArray();
        var resource = new ToolSetResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolSet,
            Metadata = new ResourceMetadata
            {
                Name = DefaultToolSetName,
                Annotations = new Dictionary<string, string> { [ResourceProvenanceAnnotations.BuiltIn] = "true" }
            },
            ScopeRef = scope,
            Generation = 1,
            Status = Succeeded(),
            Definition = new ToolSetProperties
            {
                DisplayName = "Filesystem artifact staging",
                Description = "Built-in local-first staging backend. Its low-level Tools are callable only by the Artifact service.",
                Version = DefaultToolSetVersion,
                Publish = true,
                Members = members
            }
        };
        return (await CreateOrReadAsync(resource, scope, cancellationToken)).Value;
    }

    private async Task EnsureVersionAsync(ResourceScopeRef scope, ToolSetResource toolSet, CancellationToken cancellationToken)
    {
        var versionName = VersionResourceName(DefaultToolSetName, DefaultToolSetVersion);
        var address = ScopedResourceAddress.Create(scope, ResourceNamespace.Default, ToolResourceKinds.ToolSetVersion, versionName);
        if (await store.GetExactAsync<ToolSetVersionResource>(address, cancellationToken) is not null) return;

        var members = new List<PublishedToolSetMember>();
        foreach (var capability in ArtifactCapabilities.All)
        {
            var toolName = AgentstrationToolProvider.ToolResourceName(capability);
            var tool = await store.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(
                scope, ResourceNamespace.Default, ToolResourceKinds.Tool, toolName), cancellationToken)
                ?? throw new InvalidOperationException($"Built-in Artifact staging Tool '{toolName}' was not projected.");
            var provider = tool.Value.Definition.Provider?.Resolve(ResourceNamespace.Default, ToolResourceKinds.ToolProvider)
                ?? throw new InvalidOperationException($"Built-in Artifact staging Tool '{toolName}' has no provider.");
            var storedProvider = await store.GetExactAsync<ToolProviderResource>(ScopedResourceAddress.Create(
                scope, provider.Namespace, ToolResourceKinds.ToolProvider, provider.Name), cancellationToken)
                ?? throw new InvalidOperationException($"Built-in Artifact staging Tool '{toolName}' references missing provider '{provider}'.");
            members.Add(new PublishedToolSetMember
            {
                Capability = capability,
                Route = "default",
                ToolName = tool.Value.Name,
                ToolNamespace = tool.Value.Namespace,
                ToolScopeRef = scope,
                ToolUid = tool.Value.Uid,
                ToolGeneration = tool.Value.Generation,
                ProviderName = provider.Name,
                ProviderNamespace = provider.Namespace,
                ProviderScopeRef = scope,
                ProviderUid = storedProvider.Value.Uid,
                ProviderGeneration = storedProvider.Value.Generation,
                ExternalToolId = tool.Value.Definition.ExternalId!,
                InputSchema = tool.Value.Definition.Schema!.Input.Clone(),
                OutputSchema = tool.Value.Definition.Schema.Output?.Clone(),
                RequiresApproval = tool.Value.Definition.RequiresApproval
            });
        }
        var version = new ToolSetVersionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolSetVersion,
            Metadata = new ResourceMetadata
            {
                Name = versionName,
                Annotations = new Dictionary<string, string> { [ResourceProvenanceAnnotations.BuiltIn] = "true" }
            },
            ScopeRef = scope,
            Generation = 1,
            Status = Succeeded(),
            ToolSetUid = toolSet.Uid,
            ToolSetName = DefaultToolSetName,
            ToolSetGeneration = toolSet.Generation,
            Version = DefaultToolSetVersion,
            DefinitionHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(members))),
            PublishedAt = timeProvider.GetUtcNow(),
            Members = members
        };
        _ = await CreateOrReadAsync(version, scope, cancellationToken);
    }

    private async Task EnsureBindingAsync(ResourceScopeRef scope, CancellationToken cancellationToken)
    {
        var address = ScopedResourceAddress.Create(scope, ResourceNamespace.Default,
            ArtifactResourceKinds.ArtifactStagingBinding, DefaultBindingName);
        var existing = await store.GetExactAsync<ArtifactStagingBindingResource>(address, cancellationToken);
        var bindings = await store.ListExactAsync<ArtifactStagingBindingResource>(
            scope, ArtifactResourceKinds.ArtifactStagingBinding, 0, 1_000, cancellationToken);
        var otherDefault = bindings.Any(value => !string.Equals(value.Value.Name, DefaultBindingName, StringComparison.Ordinal)
            && value.Value.Definition.IsDefault);
        if (existing is not null)
        {
            var shouldBeDefault = !otherDefault;
            var desiredDefinition = existing.Value.Definition with
            {
                IsDefault = shouldBeDefault,
                ToolSet = new ResourceReference(DefaultToolSetName, ResourceScopeRef.Instance, ResourceNamespace.Default),
                ToolSetVersion = DefaultToolSetVersion
            };
            if (existing.Value.Definition == desiredDefinition) return;
            _ = await store.PutExactAsync(scope, existing.Value with
            {
                Generation = checked(existing.Value.Generation + 1),
                Definition = desiredDefinition
            }, existing.ETag, false, cancellationToken);
            return;
        }
        _ = await CreateOrReadAsync(new ArtifactStagingBindingResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ArtifactResourceKinds.ArtifactStagingBinding,
            Metadata = new ResourceMetadata
            {
                Name = DefaultBindingName,
                Annotations = new Dictionary<string, string> { [ResourceProvenanceAnnotations.BuiltIn] = "true" }
            },
            ScopeRef = scope,
            Generation = 1,
            Status = Succeeded(),
            Definition = new ArtifactStagingBindingProperties
            {
                DisplayName = "Local filesystem",
                Description = "Default local-first temporary artifact staging.",
                IsDefault = !otherDefault,
                ToolSet = new ResourceReference(DefaultToolSetName, ResourceScopeRef.Instance, ResourceNamespace.Default),
                ToolSetVersion = DefaultToolSetVersion
            }
        }, scope, cancellationToken);
    }

    private async Task DemoteLegacyDefaultBindingAsync(ResourceScopeRef scope, CancellationToken cancellationToken)
    {
        var legacy = await store.GetExactAsync<ArtifactStagingBindingResource>(ScopedResourceAddress.Create(
            scope, ResourceNamespace.Default, ArtifactResourceKinds.ArtifactStagingBinding,
            LegacyDefaultBindingName), cancellationToken);
        if (legacy is null || !legacy.Value.Definition.IsDefault
            || !legacy.Value.Metadata.Annotations.TryGetValue(ResourceProvenanceAnnotations.BuiltIn, out var builtIn)
            || !bool.TryParse(builtIn, out var isBuiltIn) || !isBuiltIn)
            return;
        _ = await store.PutExactAsync(scope, legacy.Value with
        {
            Generation = checked(legacy.Value.Generation + 1),
            Definition = legacy.Value.Definition with { IsDefault = false }
        }, legacy.ETag, false, cancellationToken);
    }

    private async Task<ToolSetResource> EnsureBrokerToolSetAsync(ResourceScopeRef scope, CancellationToken cancellationToken)
    {
        var address = ScopedResourceAddress.Create(scope, ResourceNamespace.Default, ToolResourceKinds.ToolSet, BrokerToolSetName);
        if (await store.GetExactAsync<ToolSetResource>(address, cancellationToken) is { } existing) return existing.Value;
        var members = BrokerTools.Select(name => new ToolSetMember
        {
            Tool = new ResourceReference(AgentstrationToolProvider.ToolResourceName(name), scope, ResourceNamespace.Default),
            Capability = name,
            Route = "default"
        }).ToArray();
        return (await CreateOrReadAsync(new ToolSetResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolSet,
            Metadata = new ResourceMetadata
            {
                Name = BrokerToolSetName,
                Annotations = new Dictionary<string, string> { [ResourceProvenanceAnnotations.BuiltIn] = "true" }
            },
            ScopeRef = scope,
            Generation = 1,
            Status = Succeeded(),
            Definition = new ToolSetProperties
            {
                DisplayName = "Staged artifacts",
                Description = "Governed temporary Artifact operations for Flows and Agents.",
                Version = DefaultToolSetVersion,
                Publish = true,
                Members = members
            }
        }, scope, cancellationToken)).Value;
    }

    private Task EnsureBrokerVersionAsync(ResourceScopeRef scope, ToolSetResource toolSet, CancellationToken cancellationToken) =>
        EnsurePublishedVersionAsync(scope, toolSet, BrokerToolSetName, BrokerTools, cancellationToken);

    private async Task EnsurePublishedVersionAsync(ResourceScopeRef scope, ToolSetResource toolSet, string toolSetName,
        IReadOnlyList<string> capabilities, CancellationToken cancellationToken)
    {
        var versionName = VersionResourceName(toolSetName, DefaultToolSetVersion);
        var address = ScopedResourceAddress.Create(scope, ResourceNamespace.Default, ToolResourceKinds.ToolSetVersion, versionName);
        if (await store.GetExactAsync<ToolSetVersionResource>(address, cancellationToken) is not null) return;
        var members = new List<PublishedToolSetMember>();
        foreach (var capability in capabilities)
        {
            var toolName = AgentstrationToolProvider.ToolResourceName(capability);
            var tool = await store.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(
                scope, ResourceNamespace.Default, ToolResourceKinds.Tool, toolName), cancellationToken)
                ?? throw new InvalidOperationException($"Built-in Tool '{toolName}' was not projected.");
            var provider = tool.Value.Definition.Provider?.Resolve(ResourceNamespace.Default, ToolResourceKinds.ToolProvider)
                ?? throw new InvalidOperationException($"Built-in Tool '{toolName}' has no provider.");
            var storedProvider = await store.GetExactAsync<ToolProviderResource>(ScopedResourceAddress.Create(
                scope, provider.Namespace, ToolResourceKinds.ToolProvider, provider.Name), cancellationToken)
                ?? throw new InvalidOperationException($"Built-in Tool '{toolName}' references missing provider '{provider}'.");
            members.Add(new PublishedToolSetMember
            {
                Capability = capability,
                Route = "default",
                ToolName = tool.Value.Name,
                ToolNamespace = tool.Value.Namespace,
                ToolScopeRef = scope,
                ToolUid = tool.Value.Uid,
                ToolGeneration = tool.Value.Generation,
                ProviderName = provider.Name,
                ProviderNamespace = provider.Namespace,
                ProviderScopeRef = scope,
                ProviderUid = storedProvider.Value.Uid,
                ProviderGeneration = storedProvider.Value.Generation,
                ExternalToolId = tool.Value.Definition.ExternalId!,
                InputSchema = tool.Value.Definition.Schema!.Input.Clone(),
                OutputSchema = tool.Value.Definition.Schema.Output?.Clone(),
                RequiresApproval = tool.Value.Definition.RequiresApproval
            });
        }
        _ = await CreateOrReadAsync(new ToolSetVersionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolSetVersion,
            Metadata = new ResourceMetadata
            {
                Name = versionName,
                Annotations = new Dictionary<string, string> { [ResourceProvenanceAnnotations.BuiltIn] = "true" }
            },
            ScopeRef = scope,
            Generation = 1,
            Status = Succeeded(),
            ToolSetUid = toolSet.Uid,
            ToolSetName = toolSetName,
            ToolSetGeneration = toolSet.Generation,
            Version = DefaultToolSetVersion,
            DefinitionHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(members))),
            PublishedAt = timeProvider.GetUtcNow(),
            Members = members
        }, scope, cancellationToken);
    }

    private async Task<StoredResource<T>> CreateOrReadAsync<T>(T resource, ResourceScopeRef scope, CancellationToken cancellationToken)
        where T : Resource
    {
        try { return await store.PutExactAsync(scope, resource, null, true, cancellationToken); }
        catch (ResourceConcurrencyException)
        {
            var existing = await store.GetExactAsync<T>(ScopedResourceAddress.Create(
                scope, resource.Namespace, resource.Kind, resource.Name), cancellationToken);
            if (existing is null) throw;
            return existing;
        }
    }

    private static string VersionResourceName(string name, string version)
    {
        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(version)))[..16];
        return $"{name}--{suffix}";
    }

    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };

    private async Task EnsureStorageFlowsAsync(ResourceScopeRef scope, CancellationToken cancellationToken)
    {
        await EnsureStorageFlowAsync(scope, StorageWriteFlowName, "Filesystem Artifact storage write · Built-in",
            ArtifactFlowContracts.StorageWrite, ArtifactStorageWriteMcpTool.Name, WriteInputSchema(),
            JsonSerializer.SerializeToElement(new
            {
                stagedArtifactId = "${input.stagedArtifactId}",
                producerFlowRunId = "${input.producerFlowRunId}",
                producerFlowStepId = "${input.producerFlowStepId}"
            }), cancellationToken);
        await EnsureStorageFlowAsync(scope, StorageReadFlowName, "Filesystem Artifact storage read · Built-in",
            ArtifactFlowContracts.StorageRead, ArtifactStorageReadMcpTool.Name, ReadInputSchema(),
            JsonSerializer.SerializeToElement(new { flowRunArtifactId = "${input.flowRunArtifactId}" }), cancellationToken);
    }

    private async Task EnsureStorageFlowAsync(ResourceScopeRef scope, string name, string displayName, string contract,
        string externalToolId, JsonElement inputSchema, JsonElement arguments, CancellationToken cancellationToken)
    {
        var id = new FlowId(name);
        if (await flows.GetExactAsync(scope, id, cancellationToken) is not null) return;
        var toolStep = "storage";
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            InputSchema = inputSchema,
            OutputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            Steps =
            [
                new InputFlowStepDefinition { Name = "input", DisplayName = "Storage request", Schema = inputSchema },
                new ToolFlowStepDefinition
                {
                    Name = toolStep,
                    DisplayName = "Local filesystem storage",
                    Tool = new(AgentstrationToolProvider.ToolResourceName(externalToolId)),
                    ArgumentsMapping = arguments
                },
                new OutputFlowStepDefinition { Name = "output", DisplayName = "Storage result", OutputMapping = JsonSerializer.SerializeToElement("${steps.storage.output}") },
                new OutputFlowStepDefinition { Name = "failure", Outcome = FlowOutputOutcome.Error, Code = "artifact_storage_failed", Message = "Artifact storage operation failed.", DetailsExpression = "${transition.output}" }
            ],
            Transitions =
            [
                new("input-storage", "input", "completed", toolStep),
                new("storage-output", toolStep, "success", "output"),
                new("storage-failure", toolStep, "error", "failure")
            ]
        };
        var legacy = new WorkflowFlowDefinition("input",
        [
            new("input", FlowNodeKind.Input),
            new(toolStep, FlowNodeKind.Function),
            new("output", FlowNodeKind.Output)
        ], [new("input", toolStep), new(toolStep, "output")], ["output"]);
        try
        {
            _ = await flows.CreateExactAsync(scope, new CreateFlowCommand(name,
                $"Built-in implementation of {contract}.", DefaultToolSetVersion, true, legacy,
                new Dictionary<string, string>
                {
                    ["systemManaged"] = "true",
                    ["systemKind"] = "ArtifactStorageFlow",
                    [ResourceProvenanceAnnotations.BuiltIn] = "true",
                    [FlowMetadataKeys.Contract] = contract
                }, graph, displayName), ResourceNamespace.Default, cancellationToken);
            _ = await flows.PublishVersionExactAsync(scope, id, DefaultToolSetVersion, true, cancellationToken,
                "Built-in local-first Artifact storage contract implementation.");
        }
        catch (FlowConcurrencyException)
        {
            if (await flows.GetExactAsync(scope, id, cancellationToken) is null) throw;
        }
    }

    private static JsonElement WriteInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            stagedArtifactId = new { type = "string" },
            producerFlowRunId = new { type = "string" },
            producerFlowStepId = new { type = "string" }
        },
        required = new[] { "stagedArtifactId", "producerFlowRunId", "producerFlowStepId" }
    });

    private static JsonElement ReadInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { flowRunArtifactId = new { type = "string" } },
        required = new[] { "flowRunArtifactId" }
    });

    private static readonly string[] BrokerTools =
    [
        "staged-artifact.create", "staged-artifact.write", "staged-artifact.seal", "staged-artifact.inspect",
        "staged-artifact.read-content", "staged-artifact.create-lease", "staged-artifact.handoff",
        "staged-artifact.manage-retention", "staged-artifact.purge"
    ];
}
