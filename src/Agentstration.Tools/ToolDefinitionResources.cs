using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Schemas.Contracts;
using Agentstration.Parameters;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Tools;

public static class AgentstrationToolProvider
{
    public const string Name = "agentstration";
    public static string ToolResourceName(string definitionName) => $"{Name}.{definitionName}";
}

public sealed record ToolDefinitionFlowTarget
{
    public required string Name { get; init; }
    public ResourceNamespace? Namespace { get; init; }
    public ResourceScopeRef? ScopeRef { get; init; }
    public string? Version { get; init; }
    public bool UseActiveVersion { get; init; } = true;
}

public sealed record ToolDefinitionAcquisitionTarget(
    string TemplateId,
    ResourceNamespace TemplateNamespace,
    string? ArtifactName,
    string ArtifactKind)
{
    public static JsonElement InputSchema { get; } = JsonSerializer.Deserialize<JsonElement>(
        """{"type":"object","properties":{},"additionalProperties":false}""");

    public static JsonElement OutputSchema { get; } = JsonSerializer.Deserialize<JsonElement>(
        """
        {
          "type": "object",
          "properties": {
            "artifact": {
              "type": "object",
              "properties": {
                "artifactId": { "type": "string" },
                "kind": { "type": "string", "enum": ["staged", "durable"] },
                "name": { "type": "string" },
                "mediaType": { "type": "string" }
              },
              "required": ["artifactId", "kind"],
              "additionalProperties": false
            },
            "acquisitionId": { "type": "string" }
          },
          "required": ["artifact", "acquisitionId"],
          "additionalProperties": false
        }
        """);
}

public interface IToolDefinitionAcquisitionFactory
{
    Task<StoredResource<ToolDefinitionResource>> CreateAsync(
        ResourceNamespace templateNamespace,
        string templateId,
        string name,
        string displayName,
        string artifactId,
        CancellationToken cancellationToken);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "toolKind")]
[JsonDerivedType(typeof(FlowToolDefinitionProperties), "flow")]
[JsonDerivedType(typeof(RelayToolDefinitionProperties), "relay")]
[JsonDerivedType(typeof(AcquisitionToolDefinitionProperties), "acquisition")]
public abstract record ToolDefinitionProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public bool RequiresApproval { get; init; }
    public JsonElement InputSchema { get; init; } = JsonSerializer.Deserialize<JsonElement>("{}");
    public SchemaBinding? InputSchemaBinding { get; init; }
    public JsonElement? FixedArguments { get; init; }
    public JsonElement? OutputSchema { get; init; }
    public SchemaBinding? OutputSchemaBinding { get; init; }
    public int InvocationTimeoutSeconds { get; init; } = 90;
    public string Version { get; init; } = "1.0.0";
    public bool Publish { get; init; }
    public IReadOnlyList<ToolArgumentBinding> Bindings { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FlowToolDefinitionProperties : ToolDefinitionProperties
{
    public required ToolDefinitionFlowTarget Flow { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcquisitionToolDefinitionProperties : ToolDefinitionProperties
{
    public required ToolDefinitionAcquisitionTarget Acquisition { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RelayToolDefinitionProperties : ToolDefinitionProperties
{
    public required ToolRelayTarget Target { get; init; }
}

public sealed record ToolRelayTarget
{
    public required string Name { get; init; }
    public required ResourceScopeRef ScopeRef { get; init; }
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
    public required Guid Uid { get; init; }
    public required long Generation { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LiteralToolArgumentBinding), "literal")]
[JsonDerivedType(typeof(ParameterToolArgumentBinding), "parameter")]
[JsonDerivedType(typeof(SecretToolArgumentBinding), "secret")]
public abstract record ToolArgumentBinding
{
    public required string Path { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LiteralToolArgumentBinding : ToolArgumentBinding
{
    public required JsonElement Value { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ParameterToolArgumentBinding : ToolArgumentBinding
{
    public required ParameterReference Parameter { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SecretToolArgumentBinding : ToolArgumentBinding
{
    public required SecretReference Secret { get; init; }
}

public sealed record ToolBindingIdentity(string Path, Guid Uid);

public sealed record ToolDefinitionUsage(string Kind, string Name, ResourceNamespace Namespace,
    ResourceScopeRef ScopeRef, string Version, string RelayVersion);

public interface IToolDefinitionUsageReader
{
    Task<IReadOnlyList<ToolDefinitionUsage>> ReadAsync(ToolDefinitionResource definition,
        IReadOnlyList<ToolDefinitionVersionResource> versions, CancellationToken cancellationToken);
}

public sealed record ToolRelaySource(ToolResource Tool, ToolProviderResource Provider);

public sealed record ToolDefinitionVersionResource : Resource, IImmutableResource
{
    public required string DefinitionName { get; init; }
    public required Guid DefinitionUid { get; init; }
    public required ToolDefinitionProperties Definition { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
    public ToolRelaySource? Source { get; init; }
    public IReadOnlyList<ToolBindingIdentity> BindingIdentities { get; init; } = [];
    public int RelayDepth { get; init; }

    public ToolDefinitionResource ToDefinition() => new()
    {
        ApiVersion = ApiVersion, Kind = ToolResourceKinds.ToolDefinition,
        Metadata = Metadata, ScopeRef = ScopeRef, Uid = DefinitionUid,
        Generation = Generation, Definition = Definition
    };
}

public sealed record ToolDefinitionResource : Resource
{
    public ToolDefinitionProperties Definition { get; init; } = null!;
    public IReadOnlyDictionary<string, SchemaBindingSnapshot> SchemaBindings { get; init; }
        = new Dictionary<string, SchemaBindingSnapshot>(StringComparer.Ordinal);
}

public sealed record ResolvedToolDefinitionFlowContract(
    string Name,
    ResourceNamespace Namespace,
    string Version,
    JsonElement? InputSchema,
    JsonElement? OutputSchema)
{
    public ResourceScopeRef? ScopeRef { get; init; }
}

public interface IToolDefinitionFlowResolver
{
    Task<ResolvedToolDefinitionFlowContract> ResolveAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace ownerNamespace,
        ToolDefinitionFlowTarget target,
        CancellationToken cancellationToken);
}

public enum ToolDefinitionCallerKind { Mcp, Agent, Flow, Console }

public sealed record ToolDefinitionInvocationContext(
    string? AgentId = null,
    string? AgentRevisionId = null,
    string? RuntimeRunId = null,
    string? FlowRunId = null,
    string? FlowStepId = null,
    string? InvocationId = null,
    long? AgentVersion = null,
    long? AgentGeneration = null,
    bool HasSensitiveArguments = false);

public sealed record ToolDefinitionInvocation(
    Guid TenantId,
    WorkspaceId WorkspaceId,
    Guid PrincipalId,
    ResourceNamespace Namespace,
    string ToolName,
    string CallId,
    string? CorrelationId,
    JsonElement Arguments,
    ToolDefinitionCallerKind CallerKind,
    string? CallerId = null,
    ToolDefinitionInvocationContext? ExecutionContext = null)
{
    public ResourceScopeRef? DefinitionScopeRef { get; init; }
}

public sealed record ToolDefinitionKnowledgeReceipt(
    string KnowledgeSourceId,
    Guid KnowledgeSourceUid,
    long KnowledgeSourceGeneration,
    string SnapshotName,
    Guid SnapshotUid,
    IReadOnlyList<string> ArtifactIds);

public sealed record ToolDefinitionOperationReceipt(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WorkItemId,
    string? FlowRunId,
    string? FlowName,
    ResourceNamespace FlowNamespace,
    string? FlowVersion,
    string CorrelationId,
    bool Recovered,
    ToolDefinitionKnowledgeReceipt? Knowledge = null,
    ToolDefinitionRelayReceipt? Relay = null);

public sealed record ToolDefinitionRelayReceipt(Guid PublicationUid, string RelayVersion,
    string SourceToolName, ResourceScopeRef SourceScopeRef, Guid SourceToolUid, long SourceGeneration,
    string? SourceVersion);

public sealed record ToolDefinitionInvocationResult(JsonElement? Output, ToolDefinitionOperationReceipt Receipt);

public interface IToolDefinitionExecutor
{
    Task<ToolDefinitionInvocationResult> ExecuteAsync(ToolDefinitionInvocation invocation, CancellationToken cancellationToken);
    Task<JsonElement?> SimulateAsync(ToolDefinitionInvocation invocation, CancellationToken cancellationToken);
}

public sealed class ToolDefinitionInvocationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed record InternalMcpToolDefinition(
    string Name,
    string DisplayName,
    string? Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema = null,
    bool RequiresApproval = false,
    InitialToolCategory? InitialCategory = null,
    bool ExposeThroughMcp = true);

public sealed record InitialToolCategory(
    string Name,
    string DisplayName,
    string? Description = null);

public sealed record InternalMcpToolInvocation(
    Guid TenantId,
    WorkspaceId WorkspaceId,
    Guid PrincipalId,
    string CallId,
    string? CorrelationId,
    JsonElement Arguments,
    ToolDefinitionCallerKind CallerKind,
    string? CallerId = null,
    string? RunId = null,
    string? FlowStepId = null);

public interface IInternalMcpToolDefinitionProvider
{
    InternalMcpToolDefinition Definition { get; }
}

public interface IInternalMcpToolHandler : IInternalMcpToolDefinitionProvider
{
    Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken);
}

public static class AgentstrationInternalTools
{
    public const string AssistantDocumentationSearch = "assistant.documentation.search";
    public const string AssistantDiagnosticsInspect = "assistant.diagnostics.inspect";
    public const string DateTimeGet = "datetime.get";
    public const string NotificationCreate = "work.notification.create";
    public const string ResourcePlanCreate = "resource-planning.plan.create";
    public const string ResourcePlanGet = "resource-planning.plan.get";
    public const string ResourcePlanRefine = "resource-planning.plan.refine";
    public const string ResourcePlanSubmit = "resource-planning.plan.submit";
    public const string ResourcePlanMaterialize = "resource-planning.plan.materialize";
    public const string ResourceChangeSetCreate = "resource-planning.change-set.create";
    public const string ResourceChangeSetValidate = "resource-planning.change-set.validate";
}
