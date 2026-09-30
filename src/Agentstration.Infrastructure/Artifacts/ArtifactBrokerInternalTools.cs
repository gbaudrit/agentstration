using System.Text;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Artifacts;

public abstract class ArtifactBrokerInternalTool(ArtifactManagementService artifacts) : IInternalMcpToolHandler
{
    protected ArtifactManagementService Artifacts { get; } = artifacts;
    public abstract InternalMcpToolDefinition Definition { get; }
    public abstract Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken);

    protected static InitialToolCategory Category { get; } = new(
        "staged-artifacts", "Staged artifacts", "Governed temporary Artifact operations brokered by Agentstration.");
    protected static JsonElement Schema(object value) => JsonSerializer.SerializeToElement(value);
    protected static string RequiredString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw Error("staged_artifact_argument_required", $"Argument '{name}' is required.");
    protected static string? OptionalString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    protected static long OptionalLong(JsonElement arguments, string name, long fallback) =>
        arguments.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : fallback;
    protected static DateTimeOffset? OptionalTimestamp(JsonElement arguments, string name) =>
        OptionalString(arguments, name) is { } value ? DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : null;
    protected static ArtifactValidationException Error(string code, string message) => new(code, message);
    protected static JsonElement View(StagedArtifactResource value) => JsonSerializer.SerializeToElement(ArtifactViews.Staged(value));
}

public sealed class StagedArtifactCreateMcpTool(ArtifactManagementService artifacts) : ArtifactBrokerInternalTool(artifacts)
{
    public override InternalMcpToolDefinition Definition { get; } = new("staged-artifact.create", "Create staged artifact",
        "Creates a governed temporary Artifact. The physical backend reference is never returned.",
        Schema(new { type = "object", properties = new { fileName = new { type = "string" }, mediaType = new { type = "string" }, binding = new { type = "string" }, expiresAt = new { type = "string" } }, required = new[] { "fileName", "mediaType" }, additionalProperties = false }), InitialCategory: Category);

    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var binding = OptionalString(invocation.Arguments, "binding");
        var producer = new ArtifactProducer
        {
            Kind = invocation.CallerKind == ToolDefinitionCallerKind.Flow ? ArtifactProducerKind.FlowRun : ArtifactProducerKind.Agent,
            Id = invocation.CallerId ?? invocation.CallId,
            FlowRunId = invocation.RunId,
            FlowStepId = invocation.FlowStepId,
            AgentId = invocation.CallerKind == ToolDefinitionCallerKind.Agent ? invocation.CallerId : null,
            ToolCallId = invocation.CallId,
            CorrelationId = invocation.CorrelationId
        };
        var stored = await Artifacts.CreateStagedAsync(new(
            RequiredString(invocation.Arguments, "fileName"),
            RequiredString(invocation.Arguments, "mediaType"),
            producer,
            binding is null ? null : new ResourceReference(binding),
            OptionalTimestamp(invocation.Arguments, "expiresAt")), cancellationToken);
        return View(stored.Value);
    }
}

public sealed class StagedArtifactWriteMcpTool(ArtifactManagementService artifacts) : ArtifactBrokerInternalTool(artifacts)
{
    public override InternalMcpToolDefinition Definition { get; } = new("staged-artifact.write", "Write staged artifact",
        "Writes one bounded base64 content chunk through the governed staging binding.",
        Schema(new { type = "object", properties = new { artifactId = new { type = "string" }, offset = new { type = "integer" }, contentBase64 = new { type = "string" } }, required = new[] { "artifactId", "offset", "contentBase64" }, additionalProperties = false }), InitialCategory: Category);

    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var bytes = Convert.FromBase64String(RequiredString(invocation.Arguments, "contentBase64"));
        var stored = await Artifacts.WriteAsync(StagedArtifactId.Parse(RequiredString(invocation.Arguments, "artifactId")),
            OptionalLong(invocation.Arguments, "offset", 0), bytes, cancellationToken);
        return View(stored.Value);
    }
}

public sealed class StagedArtifactSealMcpTool(ArtifactManagementService artifacts) : ArtifactBrokerInternalTool(artifacts)
{
    public override InternalMcpToolDefinition Definition { get; } = new("staged-artifact.seal", "Seal staged artifact",
        "Seals staged content after verifying its length and digest.",
        Schema(new { type = "object", properties = new { artifactId = new { type = "string" } }, required = new[] { "artifactId" }, additionalProperties = false }), InitialCategory: Category);

    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken) =>
        View((await Artifacts.SealAsync(StagedArtifactId.Parse(RequiredString(invocation.Arguments, "artifactId")), cancellationToken)).Value);
}

public sealed class StagedArtifactInspectMcpTool(ArtifactManagementService artifacts) : ArtifactBrokerInternalTool(artifacts)
{
    public override InternalMcpToolDefinition Definition { get; } = new("staged-artifact.inspect", "Inspect staged artifact",
        "Returns metadata and provenance for an explicitly delegated staged Artifact.",
        Schema(new { type = "object", properties = new { artifactId = new { type = "string" }, leaseId = new { type = "string" } }, required = new[] { "artifactId", "leaseId" }, additionalProperties = false }), InitialCategory: Category);

    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var stored = await Artifacts.GetStagedAsync(
            StagedArtifactId.Parse(RequiredString(invocation.Arguments, "artifactId")),
            ArtifactLeaseId.Parse(RequiredString(invocation.Arguments, "leaseId")),
            ArtifactLeaseOperation.Inspect,
            cancellationToken) ?? throw Error("staged_artifact_not_found", "The staged Artifact was not found.");
        return View(stored.Value);
    }
}

public sealed class StagedArtifactReadContentMcpTool(ArtifactManagementService artifacts) : ArtifactBrokerInternalTool(artifacts)
{
    private const int MaximumTextBytes = 64 * 1024;
    public override InternalMcpToolDefinition Definition { get; } = new("staged-artifact.read-content", "Read staged artifact content",
        "Reads delegated content as bounded UTF-8 text or a bounded base64 binary chunk.",
        Schema(new { type = "object", properties = new { artifactId = new { type = "string" }, leaseId = new { type = "string" }, offset = new { type = "integer" }, length = new { type = "integer", maximum = ArtifactManagementService.MaximumChunkBytes }, disclosure = new { type = "string", @enum = new[] { "bounded-text", "binary" } } }, required = new[] { "artifactId", "leaseId", "disclosure" }, additionalProperties = false }), InitialCategory: Category);

    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var disclosure = RequiredString(invocation.Arguments, "disclosure");
        var maximum = disclosure == "bounded-text" ? MaximumTextBytes : ArtifactManagementService.MaximumChunkBytes;
        if (disclosure is not ("bounded-text" or "binary")) throw Error("staged_artifact_disclosure_invalid", "Disclosure must be 'bounded-text' or 'binary'.");
        var length = checked((int)OptionalLong(invocation.Arguments, "length", maximum));
        if (length > maximum) throw Error("staged_artifact_disclosure_limit", $"The selected disclosure is limited to {maximum} bytes per call.");
        var chunk = await Artifacts.ReadAsync(
            StagedArtifactId.Parse(RequiredString(invocation.Arguments, "artifactId")),
            OptionalLong(invocation.Arguments, "offset", 0),
            length,
            ArtifactLeaseId.Parse(RequiredString(invocation.Arguments, "leaseId")),
            cancellationToken);
        if (disclosure == "binary") return JsonSerializer.SerializeToElement(chunk);
        var bytes = Convert.FromBase64String(chunk.ContentBase64);
        return JsonSerializer.SerializeToElement(new { chunk.Offset, text = new UTF8Encoding(false, true).GetString(bytes), chunk.EndOfContent });
    }
}

public sealed class StagedArtifactManageRetentionMcpTool(ArtifactManagementService artifacts) : ArtifactBrokerInternalTool(artifacts)
{
    public override InternalMcpToolDefinition Definition { get; } = new("staged-artifact.manage-retention", "Manage staged artifact retention",
        "Extends retention within the selected binding policy.",
        Schema(new { type = "object", properties = new { artifactId = new { type = "string" }, expiresAt = new { type = "string" } }, required = new[] { "artifactId", "expiresAt" }, additionalProperties = false }), RequiresApproval: true, InitialCategory: Category);

    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken) =>
        View((await Artifacts.ExtendRetentionAsync(StagedArtifactId.Parse(RequiredString(invocation.Arguments, "artifactId")),
            OptionalTimestamp(invocation.Arguments, "expiresAt") ?? throw Error("staged_artifact_expiration_required", "Expiration is required."), cancellationToken)).Value);
}

public sealed class StagedArtifactPurgeMcpTool(ArtifactManagementService artifacts) : ArtifactBrokerInternalTool(artifacts)
{
    public override InternalMcpToolDefinition Definition { get; } = new("staged-artifact.purge", "Purge staged artifact",
        "Purges temporary content after lifecycle and active-lease checks.",
        Schema(new { type = "object", properties = new { artifactId = new { type = "string" } }, required = new[] { "artifactId" }, additionalProperties = false }), RequiresApproval: true, InitialCategory: Category);

    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var id = StagedArtifactId.Parse(RequiredString(invocation.Arguments, "artifactId"));
        await Artifacts.PurgeAsync(id, cancellationToken);
        return JsonSerializer.SerializeToElement(new { artifactId = id.ToString(), purged = true });
    }
}

public sealed class StagedArtifactCreateLeaseMcpTool(ArtifactManagementService artifacts) : ArtifactBrokerInternalTool(artifacts)
{
    public override InternalMcpToolDefinition Definition { get; } = new("staged-artifact.create-lease", "Delegate staged artifact",
        "Creates a bounded delegation for an explicit consumer and Artifact.",
        Schema(new { type = "object", properties = new { artifactId = new { type = "string" }, consumerKind = new { type = "string" }, consumerId = new { type = "string" }, operations = new { type = "array", items = new { type = "string" } }, expiresAt = new { type = "string" } }, required = new[] { "artifactId", "consumerKind", "consumerId", "operations", "expiresAt" }, additionalProperties = false }), InitialCategory: Category);

    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (!invocation.Arguments.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
            throw Error("artifact_lease_operations_required", "Lease operations are required.");
        var parsed = operations.EnumerateArray().Select(value => Enum.Parse<ArtifactLeaseOperation>(value.GetString()!, true)).ToArray();
        var stored = await Artifacts.CreateLeaseAsync(StagedArtifactId.Parse(RequiredString(invocation.Arguments, "artifactId")), new(
            RequiredString(invocation.Arguments, "consumerKind"), RequiredString(invocation.Arguments, "consumerId"), parsed,
            OptionalTimestamp(invocation.Arguments, "expiresAt") ?? throw Error("artifact_lease_expiration_required", "Lease expiration is required.")), cancellationToken);
        return View(stored.Value);
    }
}

public sealed class StagedArtifactHandoffMcpTool(ArtifactManagementService artifacts) : ArtifactBrokerInternalTool(artifacts)
{
    public override InternalMcpToolDefinition Definition { get; } = new("staged-artifact.handoff", "Handoff staged artifact",
        "Delegates, copies, or moves a sealed Artifact to an explicit consumer and governed staging binding.",
        Schema(new { type = "object", properties = new { artifactId = new { type = "string" }, operationId = new { type = "string" }, mode = new { type = "string", @enum = new[] { "reuse", "copy", "move", "reuseOrCopy" } }, consumerKind = new { type = "string" }, consumerId = new { type = "string" }, operations = new { type = "array", items = new { type = "string" } }, leaseExpiresAt = new { type = "string" }, targetBinding = new { type = "string" } }, required = new[] { "artifactId", "operationId", "mode", "consumerKind", "consumerId", "operations", "leaseExpiresAt" }, additionalProperties = false }), InitialCategory: Category);

    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (!invocation.Arguments.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
            throw Error("artifact_lease_operations_required", "Handoff lease operations are required.");
        var parsed = operations.EnumerateArray().Select(value => Enum.Parse<ArtifactLeaseOperation>(value.GetString()!, true)).ToArray();
        var binding = OptionalString(invocation.Arguments, "targetBinding");
        var result = await Artifacts.HandoffAsync(
            StagedArtifactId.Parse(RequiredString(invocation.Arguments, "artifactId")),
            new(
                RequiredString(invocation.Arguments, "operationId"),
                Enum.Parse<ArtifactHandoffMode>(RequiredString(invocation.Arguments, "mode"), true),
                RequiredString(invocation.Arguments, "consumerKind"),
                RequiredString(invocation.Arguments, "consumerId"),
                parsed,
                OptionalTimestamp(invocation.Arguments, "leaseExpiresAt") ?? throw Error("artifact_lease_expiration_required", "Lease expiration is required."),
                binding is null ? null : new ResourceReference(binding)),
            cancellationToken);
        return JsonSerializer.SerializeToElement(result);
    }
}
