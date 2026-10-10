using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Artifacts;

public abstract class ArtifactStagingInternalTool(IArtifactContentStore store) : IInternalMcpToolHandler
{
    protected IArtifactContentStore Store { get; } = store;
    public abstract InternalMcpToolDefinition Definition { get; }
    public abstract Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken);

    protected static string RequiredString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new ArtifactValidationException("artifact_tool_argument_required", $"Argument '{name}' is required.");
    protected static long RequiredLong(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.TryGetInt64(out var result)
            ? result
            : throw new ArtifactValidationException("artifact_tool_argument_required", $"Integer argument '{name}' is required.");
    protected static int RequiredInt(JsonElement arguments, string name) => checked((int)RequiredLong(arguments, name));
    protected static JsonElement Schema(object value) => JsonSerializer.SerializeToElement(value);
    protected static InitialToolCategory Category { get; } = new("artifact-staging", "Artifact staging", "Bounded physical storage operations used by governed Artifact staging bindings.");
}

public sealed class ArtifactStagingCreateMcpTool(IArtifactContentStore store) : ArtifactStagingInternalTool(store)
{
    public override InternalMcpToolDefinition Definition { get; } = new(ArtifactCapabilities.Create,
        "Create staged content", "Creates an empty opaque content object in the local staging backend.",
        Schema(new { type = "object", properties = new { artifactId = new { type = "string" }, mediaType = new { type = "string" } }, required = new[] { "artifactId", "mediaType" }, additionalProperties = false }), InitialCategory: Category, ExposeThroughMcp: false);
    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var id = StagedArtifactId.Parse(RequiredString(invocation.Arguments, "artifactId"));
        _ = RequiredString(invocation.Arguments, "mediaType");
        var reference = await Store.CreateAsync(invocation.WorkspaceId, id, cancellationToken);
        return JsonSerializer.SerializeToElement(new { backendReference = reference });
    }
}

public sealed class ArtifactStagingWriteMcpTool(IArtifactContentStore store) : ArtifactStagingInternalTool(store)
{
    public override InternalMcpToolDefinition Definition { get; } = new(ArtifactCapabilities.Write,
        "Write staged content", "Appends one bounded chunk to an opaque staged content object.",
        Schema(new { type = "object", properties = new { backendReference = new { type = "string" }, offset = new { type = "integer" }, contentBase64 = new { type = "string" } }, required = new[] { "backendReference", "offset", "contentBase64" }, additionalProperties = false }), InitialCategory: Category, ExposeThroughMcp: false);
    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var bytes = Convert.FromBase64String(RequiredString(invocation.Arguments, "contentBase64"));
        if (bytes.Length is < 1 or > ArtifactManagementService.MaximumChunkBytes)
            throw new ArtifactValidationException("artifact_staging_chunk_invalid", "The staging chunk exceeds the supported bound.");
        var length = await Store.WriteAsync(invocation.WorkspaceId, RequiredString(invocation.Arguments, "backendReference"),
            RequiredLong(invocation.Arguments, "offset"), bytes, cancellationToken);
        return JsonSerializer.SerializeToElement(new { length });
    }
}

public sealed class ArtifactStagingReadMcpTool(IArtifactContentStore store) : ArtifactStagingInternalTool(store)
{
    public override InternalMcpToolDefinition Definition { get; } = new(ArtifactCapabilities.Read,
        "Read staged content", "Reads one bounded chunk from an opaque staged content object.",
        Schema(new { type = "object", properties = new { backendReference = new { type = "string" }, offset = new { type = "integer" }, length = new { type = "integer" } }, required = new[] { "backendReference", "offset", "length" }, additionalProperties = false }), InitialCategory: Category, ExposeThroughMcp: false);
    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var length = RequiredInt(invocation.Arguments, "length");
        if (length is < 1 or > ArtifactManagementService.MaximumChunkBytes)
            throw new ArtifactValidationException("artifact_staging_read_range_invalid", "The staging read exceeds the supported bound.");
        var offset = RequiredLong(invocation.Arguments, "offset");
        var content = await Store.ReadAsync(invocation.WorkspaceId, RequiredString(invocation.Arguments, "backendReference"), offset, length, cancellationToken);
        var stat = await Store.StatAsync(invocation.WorkspaceId, RequiredString(invocation.Arguments, "backendReference"), cancellationToken);
        return JsonSerializer.SerializeToElement(new { offset, contentBase64 = Convert.ToBase64String(content.Span), endOfContent = offset + content.Length >= stat.Length });
    }
}

public sealed class ArtifactStagingStatMcpTool(IArtifactContentStore store) : ArtifactStagingInternalTool(store)
{
    public override InternalMcpToolDefinition Definition { get; } = new(ArtifactCapabilities.Stat,
        "Inspect staged content", "Returns bounded integrity metadata for an opaque staged content object.",
        Schema(new { type = "object", properties = new { backendReference = new { type = "string" } }, required = new[] { "backendReference" }, additionalProperties = false }), InitialCategory: Category, ExposeThroughMcp: false);
    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var stat = await Store.StatAsync(invocation.WorkspaceId, RequiredString(invocation.Arguments, "backendReference"), cancellationToken);
        return JsonSerializer.SerializeToElement(new { length = stat.Length, sha256 = stat.Sha256 });
    }
}

public sealed class ArtifactStagingDeleteMcpTool(IArtifactContentStore store) : ArtifactStagingInternalTool(store)
{
    public override InternalMcpToolDefinition Definition { get; } = new(ArtifactCapabilities.Delete,
        "Delete staged content", "Deletes an opaque staged content object after Agentstration lifecycle checks.",
        Schema(new { type = "object", properties = new { backendReference = new { type = "string" } }, required = new[] { "backendReference" }, additionalProperties = false }), InitialCategory: Category, ExposeThroughMcp: false);
    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        await Store.DeleteAsync(invocation.WorkspaceId, RequiredString(invocation.Arguments, "backendReference"), cancellationToken);
        return JsonSerializer.SerializeToElement(new { deleted = true });
    }
}
