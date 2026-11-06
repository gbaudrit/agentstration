using System.Text.Json;
using Agentstration.Asp.Abstractions;
using ModelContextProtocol.Client;

namespace Agentstration.Asp.Client;

// Owns no transport: the host supplies an authenticated MCP connection and disposes it.
public sealed class AspClient
{
    private readonly McpClient mcp;
    private readonly string binding;
    private readonly TimeSpan timeout;
    public AspDescription Description { get; }

    private AspClient(McpClient mcp, string binding, AspDescription description, TimeSpan timeout)
    { this.mcp = mcp; this.binding = binding; Description = description; this.timeout = timeout; }

    public static async Task<AspClient> ConnectAsync(McpClient mcp, string binding, AspStoreRef expectedStore,
        TimeSpan? timeout = null, string? expectedProviderId = null, string? expectedProviderVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mcp);
        ArgumentNullException.ThrowIfNull(expectedStore);
        var duration = timeout ?? TimeSpan.FromSeconds(30);
        AspValidation.Require(duration > TimeSpan.Zero && duration <= TimeSpan.FromSeconds(30), "invalid_request", "Timeout must be between zero and 30 seconds.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        var tools = await mcp.ListToolsAsync(cancellationToken: deadline.Token);
        var describeTool = tools.SingleOrDefault(value => value.Name == $"{binding}_describe")
            ?? throw new AspException("unsupported_operation", "The ASP describe mapping is missing.");
        ValidateSchema(describeTool, "describe");
        var description = await InvokeAsync<AspDescription>(mcp, binding, "describe", new AspDescribeRequest(Guid.NewGuid().ToString("N")), deadline.Token);
        ValidateDescription(description, expectedStore);
        AspValidation.Require((expectedProviderId is null || description.ProviderId == expectedProviderId)
            && (expectedProviderVersion is null || description.ProviderVersion == expectedProviderVersion), "incompatible_provider", "Provider identity or version differs from the pinned provider.");
        var capabilities = description.Capabilities;
        foreach (var operation in new[] { "list", "stat", "read", "write" })
        {
            var supported = operation switch { "list" => capabilities.List, "stat" => capabilities.Stat, "read" => capabilities.Read, _ => capabilities.Write };
            if (!supported) continue;
            var tool = tools.SingleOrDefault(value => value.Name == $"{binding}_{operation}")
                ?? throw new AspException("unsupported_operation", "An advertised ASP mapping is missing.");
            ValidateSchema(tool, operation);
        }
        return new(mcp, binding, description, duration);
    }

    public async Task<AspListResult> ListAsync(AspListRequest request, CancellationToken cancellationToken = default)
    {
        AspValidation.Require(Description.Capabilities.List, "unsupported_operation", "List is unsupported.");
        var result = await CallAsync<AspListResult>("list", request, cancellationToken);
        AspValidation.Require(result.Objects is not null && result.Objects.Count <= request.Limit, "invalid_result", "Invalid list result.");
        string? previous = null;
        foreach (var item in result.Objects)
        {
            ValidateObject(item);
            AspValidation.Require(item.Key.StartsWith(request.Prefix, StringComparison.Ordinal)
                && (previous is null || string.CompareOrdinal(item.Key, previous) > 0), "invalid_result", "List keys do not match the request.");
            previous = item.Key;
        }
        AspValidation.Require(result.Cursor is null || result.Cursor.Length is > 0 and <= 4096 && result.Objects.Count > 0,
            "invalid_result", "Invalid continuation.");
        return result;
    }

    public async Task<AspObject> StatAsync(AspReferenceRequest request, CancellationToken cancellationToken = default)
    {
        AspValidation.Require(Description.Capabilities.Stat, "unsupported_operation", "Stat is unsupported.");
        var result = await CallAsync<AspObject>("stat", request, cancellationToken);
        ValidateObject(result);
        return result;
    }

    public async Task<AspReadResult> ReadAsync(AspReadRequest request, CancellationToken cancellationToken = default)
    {
        AspValidation.Require(Description.Capabilities.Read, "unsupported_operation", "Read is unsupported.");
        var result = await CallAsync<AspReadResult>("read", request, cancellationToken);
        ValidateObject(result.Item);
        var bytes = AspValidation.Content(result.ContentBase64, Description.Policy.MaxReadBytes);
        AspValidation.Require(result.Offset == request.Offset && bytes.Length <= request.MaxBytes && result.Offset >= 0
            && result.Offset <= result.Item.Length && bytes.Length == Math.Min(request.MaxBytes, result.Item.Length - result.Offset)
            && result.EndOfObject == (result.Offset + bytes.Length == result.Item.Length), "invalid_result", "Invalid content range.");
        if (result.Offset == 0 && result.EndOfObject)
            AspValidation.Require(AspValidation.Digest(bytes) == result.Item.Sha256, "invalid_result", "Content digest mismatch.");
        return result;
    }

    public async Task<AspWriteResult> WriteAsync(AspWriteRequest request, CancellationToken cancellationToken = default)
    {
        AspValidation.Require(Description.Capabilities.Write, "unsupported_operation", "Write is unsupported.");
        var bytes = AspValidation.Content(request.ContentBase64, Description.Policy.MaxWriteBytes);
        var result = await CallAsync<AspWriteResult>("write", request, cancellationToken);
        ValidateObject(result.Item);
        AspValidation.Require(result.Item.Key == request.Key && result.Item.Length == bytes.Length
            && result.Item.Sha256 == AspValidation.Digest(bytes), "invalid_result", "Invalid write receipt.");
        return result;
    }

    private async Task<T> CallAsync<T>(string operation, object args, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        return await InvokeAsync<T>(mcp, binding, operation, args, deadline.Token);
    }

    private static async Task<T> InvokeAsync<T>(McpClient mcp, string binding, string operation, object args, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToElement(args, AspProtocol.JsonOptions);
        var requestId = json.GetProperty("requestId").GetString();
        var arguments = json.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        var result = await mcp.CallToolAsync($"{binding}_{operation}", arguments, cancellationToken: ct);
        AspValidation.Require(result.StructuredContent is { ValueKind: JsonValueKind.Object }, "invalid_result", "Structured ASP content is required.");
        AspValidation.Require(result.StructuredContent.Value.GetRawText().Length <= 200_000, "invalid_result", "ASP result exceeds the wire limit.");
        AspReply<T> reply;
        try { reply = AspValidation.Parse<AspReply<T>>(result.StructuredContent.Value); }
        catch (AspException) { throw new AspException("invalid_result", "Result does not match the ASP schema."); }
        AspValidation.Require(reply.AspVersion == AspProtocol.Version, "incompatible_version", "Unsupported ASP response version.");
        AspValidation.Require(reply.RequestId == requestId, "invalid_result", "Response request identity does not match.");
        AspValidation.Require((reply.Error is not null) == (result.IsError == true)
            && (reply.Error is null ? reply.Data is not null : reply.Data is null), "invalid_result", "Invalid result envelope.");
        if (reply.Error is { } error)
        {
            AspValidation.Require(!string.IsNullOrWhiteSpace(error.Code) && error.Code.Length <= 128
                && error.Message is { Length: > 0 and <= 1024 }, "invalid_result", "Invalid error envelope.");
            throw new AspException(error.Code, error.Message);
        }
        return reply.Data!;
    }

    private static void ValidateSchema(McpClientTool tool, string operation)
    {
        AspValidation.Require(JsonElement.DeepEquals(tool.ProtocolTool.InputSchema, AspSchemas.Input(operation))
            && tool.ProtocolTool.OutputSchema is { } schema && JsonElement.DeepEquals(schema, AspSchemas.Output(operation)),
            "incompatible_schema", "The MCP Tool schema does not match the ASP profile.");
    }

    private static void ValidateDescription(AspDescription value, AspStoreRef expected)
    {
        AspValidation.Require(value.AspVersion == AspProtocol.Version && value.Profile == AspProtocol.Profile,
            "incompatible_version", "Unsupported ASP profile or version.");
        AspValidation.Require(value.Store == expected, "forbidden", "The effective store differs from the pinned binding.");
        AspValidation.Require(!string.IsNullOrWhiteSpace(value.ProviderId) && value.ProviderId.Length <= 128
            && !string.IsNullOrWhiteSpace(value.ProviderVersion) && value.ProviderVersion.Length <= 128
            && value.ConfigurationSchema.ValueKind == JsonValueKind.Object && value.Capabilities is not null
            && value.Policy is not null, "invalid_result", "Invalid provider description.");
        AspValidation.Policy(value.Policy);
        var c = value.Capabilities;
        AspValidation.Require(!c.Delete && !c.StableVersions && !c.Retention, "unsupported_capability", "This profile implementation offers no delete, stable-version or retention operation.");
        AspValidation.Require(c.List == value.Policy.CanList && c.Stat == value.Policy.CanStat
            && c.Read == value.Policy.CanRead && c.Write == value.Policy.CanWrite, "invalid_result", "Capabilities and effective policy disagree.");
    }

    private void ValidateObject(AspObject item)
    {
        AspValidation.Require(item is not null && item.Reference is { Length: > 0 and <= 4096 }, "invalid_result", "Invalid object reference.");
        AspValidation.Item(new(item.Key, item.Version, item.Length, item.Sha256));
        AspValidation.Require(AspValidation.Allows(Description.Policy, item.Key), "invalid_result", "Object is outside the fixed policy.");
    }
}
