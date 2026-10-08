using System.Text;
using System.Text.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Knowledge;

public static class DataSourceBuiltinToolNames
{
    public const string ArtifactImport = "artifact.import";
}

public static class KnowledgeBuiltinToolNames
{
    public const string RetrievalSearch = "knowledge.retrieval.search";
    public const string RetrievalQuery = "knowledge.retrieval.query";
    public const string RetrievalRead = "knowledge.retrieval.read";
}

public sealed class DataSourceArtifactImportMcpTool(
    IResourceStore resources,
    IAuthorizationService authorization) : IInternalMcpToolHandler
{
    public InternalMcpToolDefinition Definition { get; } = new(
        DataSourceBuiltinToolNames.ArtifactImport,
        "Import durable Artifacts",
        "Builds a bounded acquisition manifest from existing governed durable Artifacts in the current Workspace.",
        KnowledgeBuiltinSchemas.DataSourceArtifactImportInput,
        KnowledgeBuiltinSchemas.ArtifactManifestOutput,
        InitialCategory: KnowledgeBuiltinSchemas.DataSourceCategory,
        ExposeThroughMcp: false);

    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        await authorization.EnsurePermissionAsync(new(invocation.PrincipalId, invocation.TenantId, invocation.WorkspaceId.Value),
            AuthorizationPermissions.ArtifactsInspect, cancellationToken);
        if (!invocation.Arguments.TryGetProperty("sourceConfiguration", out var configuration)
            || configuration.ValueKind != JsonValueKind.Object)
            throw Error("data_source_artifact_import_configuration_invalid", "The import request requires an object sourceConfiguration value.");
        if (!configuration.TryGetProperty("artifactIds", out var artifactIds))
            return JsonSerializer.SerializeToElement(new { artifacts = Array.Empty<object>() });
        if (artifactIds.ValueKind != JsonValueKind.Array)
            throw Error("data_source_artifact_import_ids_invalid", "sourceConfiguration.artifactIds must be an array.");
        var values = artifactIds.EnumerateArray().ToArray();
        if (values.Length > 100)
            throw Error("data_source_artifact_import_limit_exceeded", "At most 100 durable Artifacts can be imported per acquisition.");

        var artifacts = new List<object>(values.Length);
        foreach (var value in values)
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw Error("data_source_artifact_import_id_invalid", "Every imported Artifact identity must be a non-empty string.");
            FlowRunArtifactId id;
            try { id = FlowRunArtifactId.Parse(value.GetString()!); }
            catch (FormatException exception) { throw Error("data_source_artifact_import_id_invalid", exception.Message, exception); }
            var stored = await resources.GetExactAsync<FlowRunArtifactResource>(ScopedResourceAddress.Create(
                ResourceScopeRef.Workspace(invocation.WorkspaceId.Value), ResourceNamespace.Default,
                ArtifactResourceKinds.FlowRunArtifact, id.ToString()), cancellationToken)
                ?? throw Error("data_source_artifact_import_not_found", $"FlowRunArtifact '{id}' was not found in the current Workspace.");
            artifacts.Add(new
            {
                artifactId = stored.Value.ArtifactId.ToString(), kind = "durable", disposition = "publishable",
                name = stored.Value.Receipt.Provenance.GetValueOrDefault("fileName"),
                mediaType = stored.Value.Receipt.MediaType, digest = stored.Value.Receipt.Sha256
            });
        }
        return JsonSerializer.SerializeToElement(new { artifacts });
    }

    private static ToolDefinitionInvocationException Error(string code, string message, Exception? inner = null) => new(code, message, inner);
}

public abstract class KnowledgeRetrievalBuiltinMcpTool(
    IResourceStore resources,
    IArtifactDurableStore durable,
    IAuthorizationService authorization) : IInternalMcpToolHandler
{
    private const int MaximumScannedBytesPerArtifact = 1024 * 1024;
    private const int MaximumTotalScannedBytes = 8 * 1024 * 1024;
    private const int ExcerptRadius = 160;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected abstract string Operation { get; }
    protected abstract string ToolName { get; }
    protected abstract string DisplayName { get; }
    protected abstract string Description { get; }

    public InternalMcpToolDefinition Definition => new(ToolName, DisplayName, Description,
        KnowledgeBuiltinSchemas.RetrievalInput, KnowledgeBuiltinSchemas.RetrievalOutput,
        InitialCategory: KnowledgeBuiltinSchemas.Category, ExposeThroughMcp: false);

    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        await authorization.EnsurePermissionAsync(new(invocation.PrincipalId, invocation.TenantId, invocation.WorkspaceId.Value),
            AuthorizationPermissions.ArtifactsReadContent, cancellationToken);
        var input = invocation.Arguments.Deserialize<KnowledgeRetrievalInput>(JsonOptions)
            ?? throw Error("knowledge_builtin_input_invalid", "The retrieval input is invalid.");
        if (!string.Equals(input.Operation, Operation, StringComparison.OrdinalIgnoreCase))
            throw Error("knowledge_builtin_operation_mismatch", $"The '{ToolName}' Tool accepts only the '{Operation}' operation.");
        return Operation switch
        {
            "search" => await SearchAsync(invocation, input, cancellationToken),
            "query" => await QueryAsync(invocation, input, cancellationToken),
            "read" => await ReadAsync(invocation, input, cancellationToken),
            _ => throw Error("knowledge_builtin_operation_invalid", $"Operation '{Operation}' is not supported.")
        };
    }

    private async Task<JsonElement> SearchAsync(InternalMcpToolInvocation invocation, KnowledgeRetrievalInput input, CancellationToken cancellationToken)
    {
        var matches = await FindAsync(invocation, input.Snapshot.Artifacts, RequiredString(input.Request, "query"),
            RequiredInt32(input.Request, "limit", 1, 100), cancellationToken);
        return Result(matches, null);
    }

    private async Task<JsonElement> QueryAsync(InternalMcpToolInvocation invocation, KnowledgeRetrievalInput input, CancellationToken cancellationToken)
    {
        var question = RequiredString(input.Request, "question");
        var maximumItems = RequiredInt32(input.Request, "maximumItems", 1, 100);
        var maximumOutput = RequiredInt32(input.Request, "maximumOutputCharacters", 1, 32_768);
        var term = question.Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Where(value => value.Length >= 3).OrderByDescending(value => value.Length).FirstOrDefault();
        var matches = term is null ? [] : await FindAsync(invocation, input.Snapshot.Artifacts, term, maximumItems, cancellationToken);
        var answer = matches.Count == 0 ? null : string.Join(Environment.NewLine, matches.Select(value => value.Excerpt));
        if (answer?.Length > maximumOutput) answer = answer[..maximumOutput];
        return Result(matches, answer);
    }

    private async Task<JsonElement> ReadAsync(InternalMcpToolInvocation invocation, KnowledgeRetrievalInput input, CancellationToken cancellationToken)
    {
        var artifactId = RequiredString(input.Request, "artifactId");
        var offset = RequiredInt64(input.Request, "offset", 0, long.MaxValue);
        var length = RequiredInt32(input.Request, "length", 1, 1024 * 1024);
        var evidence = input.Snapshot.Artifacts.SingleOrDefault(value => string.Equals(value.ArtifactId, artifactId, StringComparison.Ordinal))
            ?? throw Error("knowledge_builtin_artifact_outside_snapshot", $"Artifact '{artifactId}' is not present in the selected Snapshot.");
        var artifact = await RequireArtifactAsync(invocation.WorkspaceId, evidence, cancellationToken);
        var bytes = await durable.ReadAsync(invocation.WorkspaceId, artifact.Receipt.OpaqueReference, offset, length, cancellationToken);
        var content = Decode(bytes.Span, artifact.Receipt.MediaType);
        var excerpt = content.Length <= KnowledgeRetrievalService.MaximumCitationExcerptCharacters
            ? content
            : content[..KnowledgeRetrievalService.MaximumCitationExcerptCharacters];
        var end = checked(offset + bytes.Length);
        return JsonSerializer.SerializeToElement(new
        {
            items = new[] { new { id = $"{artifactId}:{offset}", artifactId, content, mediaType = artifact.Receipt.MediaType,
                score = (double?)null, metadata = new Dictionary<string, string>() } },
            citations = new[] { new { artifactId, locator = $"bytes={offset}-{end}", start = offset, end, excerpt } },
            answer = (string?)null, continuationToken = (string?)null
        });
    }

    private async Task<List<Match>> FindAsync(InternalMcpToolInvocation invocation,
        IReadOnlyList<KnowledgeSnapshotArtifact> evidence, string query, int limit, CancellationToken cancellationToken)
    {
        var result = new List<Match>();
        var remaining = MaximumTotalScannedBytes;
        foreach (var item in evidence)
        {
            if (result.Count >= limit || remaining <= 0) break;
            var artifact = await RequireArtifactAsync(invocation.WorkspaceId, item, cancellationToken);
            if (!IsText(artifact.Receipt.MediaType)) continue;
            var length = checked((int)Math.Min(Math.Min(artifact.Receipt.Length, MaximumScannedBytesPerArtifact), remaining));
            if (length == 0) continue;
            var bytes = await durable.ReadAsync(invocation.WorkspaceId, artifact.Receipt.OpaqueReference, 0, length, cancellationToken);
            remaining -= bytes.Length;
            var content = Decode(bytes.Span, artifact.Receipt.MediaType);
            var searchOffset = 0;
            while (result.Count < limit && searchOffset < content.Length)
            {
                var index = content.IndexOf(query, searchOffset, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                var start = Math.Max(0, index - ExcerptRadius);
                var end = Math.Min(content.Length, index + query.Length + ExcerptRadius);
                result.Add(new(item.ArtifactId, artifact.Receipt.MediaType, content[start..end], start, end));
                searchOffset = index + query.Length;
            }
        }
        return result;
    }

    private async Task<FlowRunArtifactResource> RequireArtifactAsync(WorkspaceId workspaceId,
        KnowledgeSnapshotArtifact evidence, CancellationToken cancellationToken)
    {
        FlowRunArtifactId id;
        try { id = FlowRunArtifactId.Parse(evidence.ArtifactId); }
        catch (FormatException exception) { throw Error("knowledge_builtin_artifact_id_invalid", exception.Message, exception); }
        var stored = await resources.GetExactAsync<FlowRunArtifactResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Workspace(workspaceId.Value), ResourceNamespace.Default,
            ArtifactResourceKinds.FlowRunArtifact, id.ToString()), cancellationToken)
            ?? throw Error("knowledge_builtin_artifact_not_found", $"FlowRunArtifact '{id}' was not found in the current Workspace.");
        if (stored.Value.Receipt.Length != evidence.Length
            || !string.Equals(stored.Value.Receipt.Sha256, evidence.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Error("knowledge_builtin_artifact_evidence_mismatch", $"FlowRunArtifact '{id}' no longer matches the selected Snapshot evidence.");
        if (!string.Equals(stored.Value.Receipt.Provenance.GetValueOrDefault("provider"), "filesystem", StringComparison.Ordinal))
            throw Error("knowledge_builtin_storage_unsupported", "The built-in retrieval route supports only the built-in filesystem storage provider.");
        return stored.Value;
    }

    private static JsonElement Result(IReadOnlyList<Match> matches, string? answer) => JsonSerializer.SerializeToElement(new
    {
        items = matches.Select((value, index) => new { id = $"{value.ArtifactId}:{index}", artifactId = value.ArtifactId,
            content = value.Excerpt, mediaType = value.MediaType, score = 1d, metadata = new Dictionary<string, string>() }),
        citations = matches.Select(value => new { artifactId = value.ArtifactId,
            locator = $"characters={value.Start}-{value.End}", start = (long)value.Start, end = (long)value.End, excerpt = value.Excerpt }),
        answer, continuationToken = (string?)null
    });

    private static bool IsText(string mediaType) => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("markdown", StringComparison.OrdinalIgnoreCase);

    private static string Decode(ReadOnlySpan<byte> bytes, string mediaType) => IsText(mediaType)
        ? Encoding.UTF8.GetString(bytes)
        : throw Error("knowledge_builtin_media_type_unsupported", $"Media type '{mediaType}' is not supported by the built-in text retrieval route.");

    private static string RequiredString(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
        ? property.GetString()! : throw Error("knowledge_builtin_request_invalid", $"Request property '{name}' is required.");
    private static int RequiredInt32(JsonElement value, string name, int minimum, int maximum) => value.TryGetProperty(name, out var property)
        && property.TryGetInt32(out var result) && result >= minimum && result <= maximum
        ? result : throw Error("knowledge_builtin_request_invalid", $"Request property '{name}' must be between {minimum} and {maximum}.");
    private static long RequiredInt64(JsonElement value, string name, long minimum, long maximum) => value.TryGetProperty(name, out var property)
        && property.TryGetInt64(out var result) && result >= minimum && result <= maximum
        ? result : throw Error("knowledge_builtin_request_invalid", $"Request property '{name}' must be between {minimum} and {maximum}.");
    private static ToolDefinitionInvocationException Error(string code, string message, Exception? inner = null) => new(code, message, inner);
    private sealed record Match(string ArtifactId, string MediaType, string Excerpt, int Start, int End);
}

public sealed class KnowledgeRetrievalSearchMcpTool(IResourceStore resources, IArtifactDurableStore durable, IAuthorizationService authorization)
    : KnowledgeRetrievalBuiltinMcpTool(resources, durable, authorization)
{
    protected override string Operation => "search";
    protected override string ToolName => KnowledgeBuiltinToolNames.RetrievalSearch;
    protected override string DisplayName => "Search built-in Knowledge storage";
    protected override string Description => "Performs bounded deterministic text search over the selected Knowledge Snapshot.";
}

public sealed class KnowledgeRetrievalQueryMcpTool(IResourceStore resources, IArtifactDurableStore durable, IAuthorizationService authorization)
    : KnowledgeRetrievalBuiltinMcpTool(resources, durable, authorization)
{
    protected override string Operation => "query";
    protected override string ToolName => KnowledgeBuiltinToolNames.RetrievalQuery;
    protected override string DisplayName => "Query built-in Knowledge storage";
    protected override string Description => "Builds a deterministic bounded answer from matching Snapshot excerpts without a remote model.";
}

public sealed class KnowledgeRetrievalReadMcpTool(IResourceStore resources, IArtifactDurableStore durable, IAuthorizationService authorization)
    : KnowledgeRetrievalBuiltinMcpTool(resources, durable, authorization)
{
    protected override string Operation => "read";
    protected override string ToolName => KnowledgeBuiltinToolNames.RetrievalRead;
    protected override string DisplayName => "Read built-in Knowledge storage";
    protected override string Description => "Reads a bounded UTF-8 range from one Artifact in the selected Knowledge Snapshot.";
}

internal static class KnowledgeBuiltinSchemas
{
    public static InitialToolCategory DataSourceCategory { get; } = new("data-source", "Data source",
        "Built-in Data Source acquisition implementation Tools.");
    public static InitialToolCategory Category { get; } = new("knowledge-source", "Knowledge source",
        "Built-in Knowledge retrieval implementation Tools.");
    public static JsonElement DataSourceArtifactImportInput { get; } = JsonSerializer.SerializeToElement(new { type = "object",
        properties = new { sourceConfiguration = new { type = "object" } },
        required = new[] { "sourceConfiguration" }, additionalProperties = false });
    public static JsonElement DataSourceAcquisitionFlowInput { get; } = JsonSerializer.SerializeToElement(new { type = "object",
        properties = new { dataSourceId = new { type = "string" }, dataSourceUid = new { type = "string" },
            dataSourceGeneration = new { type = "integer" }, profile = new { type = "object" }, sourceConfiguration = new { type = "object" },
            parameters = new { type = "object" }, caller = new { type = "object" },
            correlationId = new { type = "string" }, acquisitionId = new { type = "string" } },
        required = new[] { "dataSourceId", "dataSourceUid", "dataSourceGeneration", "profile", "sourceConfiguration",
            "parameters", "caller", "correlationId", "acquisitionId" }, additionalProperties = false });
    public static JsonElement ArtifactManifestOutput { get; } = JsonSerializer.SerializeToElement(new { type = "object",
        properties = new { artifacts = new { type = "array" } }, required = new[] { "artifacts" }, additionalProperties = false });
    public static JsonElement ProjectionInput { get; } = JsonSerializer.SerializeToElement(new { type = "object",
        properties = new { knowledgeSourceId = new { type = "string" }, knowledgeSourceUid = new { type = "string" },
            knowledgeSourceGeneration = new { type = "integer" }, inputs = new { type = "array" }, artifacts = new { type = "array" },
            parameters = new { type = "object" }, caller = new { type = "object" }, correlationId = new { type = "string" },
            projectionId = new { type = "string" } },
        required = new[] { "knowledgeSourceId", "knowledgeSourceUid", "knowledgeSourceGeneration", "inputs", "artifacts",
            "parameters", "caller", "correlationId", "projectionId" }, additionalProperties = false });
    public static JsonElement RetrievalInput { get; } = JsonSerializer.SerializeToElement(new { type = "object",
        properties = new { knowledgeSourceId = new { type = "string" }, knowledgeSourceUid = new { type = "string" },
            knowledgeSourceGeneration = new { type = "integer" }, operation = new { type = "string" }, snapshot = new { type = "object" },
            request = new { type = "object" }, caller = new { type = "object" }, correlationId = new { type = "string" }, retrievalId = new { type = "string" } },
        required = new[] { "knowledgeSourceId", "knowledgeSourceUid", "knowledgeSourceGeneration", "operation", "snapshot", "request", "caller", "correlationId", "retrievalId" },
        additionalProperties = false });
    public static JsonElement RetrievalOutput { get; } = JsonSerializer.SerializeToElement(new { type = "object",
        properties = new { items = new { type = "array" }, citations = new { type = "array" }, answer = new { type = "string" }, continuationToken = new { type = "string" } },
        required = new[] { "items", "citations" }, additionalProperties = false });
}
