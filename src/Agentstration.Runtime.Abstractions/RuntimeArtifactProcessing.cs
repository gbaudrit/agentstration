using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Runtime.Abstractions;

public static class RuntimeArtifactProcessingTools
{
    public const string Provider = "artifact-processing";
    public const string Prefix = "artifact_processing.";
    public static IReadOnlyList<RuntimeExecutionToolMaterial> Create() =>
    [
        Tool("input_info", "Inspect the exact read-only input Artifact.", "{}"),
        Tool("input_read", "Read UTF-8 input as bounded text. Offsets and ranges use UTF-16 characters; continue using the returned cursor.", ReadProperties),
        Tool("input_search_regex", "Search the exact input. Matches contain UTF-16 ranges accepted by input_read.", SearchProperties, "pattern"),
        Tool("output_info", "Inspect the isolated output draft and its current revision.", "{}"),
        Tool("output_read", "Read back a bounded part of the current output revision.", ReadProperties),
        Tool("output_search_regex", "Search the output draft to verify or locate corrections.", SearchProperties, "pattern"),
        Tool("output_write", "Append text, or replace offset/deleteLength UTF-16 characters. Supply the current expectedRevision and a unique requestId; retry the same write with the same requestId.",
            "{\"content\":{\"type\":\"string\"},\"expectedRevision\":{\"type\":\"integer\"},\"requestId\":{\"type\":\"string\"},\"offset\":{\"type\":\"integer\"},\"deleteLength\":{\"type\":\"integer\"}}", "content", "expectedRevision", "requestId"),
        Tool("output_complete", "Declare the selected output revision complete after verification. Publication remains owned by the Artifact transformation step.",
            "{\"expectedRevision\":{\"type\":\"integer\"}}", "expectedRevision")
    ];
    private const string ReadProperties = "{\"cursor\":{\"type\":\"string\"},\"offset\":{\"type\":\"integer\"},\"length\":{\"type\":\"integer\"}}";
    private const string SearchProperties = "{\"pattern\":{\"type\":\"string\"},\"cursor\":{\"type\":\"string\"},\"maxMatches\":{\"type\":\"integer\"}}";
    private static RuntimeExecutionToolMaterial Tool(string name, string description, string properties, params string[] required)
    {
        using var parsed = JsonDocument.Parse(properties);
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = parsed.RootElement.Clone(), required, additionalProperties = false });
        return new(Prefix + name, ResourceNamespace.Default, name, description, schema, null, false, Provider, null, name);
    }
}
