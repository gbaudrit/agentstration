using System.Text.Json;
using Agentstration.Asp.Abstractions;
using ModelContextProtocol.Client;

namespace Agentstration.Asp.Client;

// Owns no transport: the host supplies an authenticated MCP connection and disposes it.
public sealed class AspClient
{
    private readonly McpClient mcp;
    private readonly string toolPrefix;
    private readonly TimeSpan timeout;
    public AspDescription Description { get; }

    private AspClient(McpClient mcp, string toolPrefix, AspDescription description, TimeSpan timeout)
    { this.mcp = mcp; this.toolPrefix = toolPrefix; Description = description; this.timeout = timeout; }

    public static async Task<AspClient> ConnectAsync(McpClient mcp, string toolPrefix, AspStoreRef expectedStore,
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
        var describeTool = tools.SingleOrDefault(value => value.Name == $"{toolPrefix}_describe")
            ?? throw new AspException("unsupported_operation", "The ASP describe mapping is missing.");
        ValidateSchema(describeTool, "describe");
        var description = await InvokeAsync<AspDescription>(mcp, toolPrefix, "describe", new AspDescribeRequest(Guid.NewGuid().ToString("N")), expectedStore, deadline.Token);
        ValidateDescription(description, expectedStore);
        AspValidation.Require((expectedProviderId is null || description.ProviderId == expectedProviderId)
            && (expectedProviderVersion is null || description.ProviderVersion == expectedProviderVersion), "incompatible_provider", "Provider identity or version differs from the pinned provider.");
        var capabilities = description.Capabilities;
        foreach (var operation in new[] { "list", "stat", "read_blob", "read_text", "close_read", "write_blob", "write_text", "abort_write", "write_status" })
        {
            var supported = operation switch { "list" => capabilities.List, "stat" => capabilities.Stat, "read_blob" or "read_text" or "close_read" => capabilities.Read, _ => capabilities.Write };
            if (!supported) continue;
            var tool = tools.SingleOrDefault(value => value.Name == $"{toolPrefix}_{operation}")
                ?? throw new AspException("unsupported_operation", "An advertised ASP mapping is missing.");
            ValidateSchema(tool, operation);
        }
        return new(mcp, toolPrefix, description, duration);
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

    public Task<AspReadBlobResult> ReadBlobAsync(AspReadBlobRequest request, CancellationToken cancellationToken = default) =>
        ReadTransferAsync<AspReadBlobResult>("read_blob", request, cancellationToken);
    public Task<AspReadTextResult> ReadTextAsync(AspReadTextRequest request, CancellationToken cancellationToken = default) =>
        ReadTransferAsync<AspReadTextResult>("read_text", request, cancellationToken);
    public Task<AspWriteResult> WriteBlobAsync(AspWriteBlobRequest request, CancellationToken cancellationToken = default) =>
        WriteTransferAsync("write_blob", request, request.Sequence, request.Final, AspValidation.Content(request.ContentBase64, Description.Policy.MaxWriteBytes), cancellationToken);
    public Task<AspWriteResult> WriteTextAsync(AspWriteTextRequest request, CancellationToken cancellationToken = default) =>
        WriteTransferAsync("write_text", request, request.Sequence, request.Final, AspValidation.Text(request.Content, Description.Policy.MaxWriteBytes), cancellationToken);
    public Task<AspSessionResult> CloseReadAsync(AspSessionRequest request, CancellationToken cancellationToken = default) => ControlAsync("close_read", request, cancellationToken);
    public Task<AspSessionResult> AbortWriteAsync(AspSessionRequest request, CancellationToken cancellationToken = default) => ControlAsync("abort_write", request, cancellationToken);
    public Task<AspSessionResult> WriteStatusAsync(AspSessionRequest request, CancellationToken cancellationToken = default) => ControlAsync("write_status", request, cancellationToken);

    private async Task<AspSessionResult> ControlAsync(string operation, AspSessionRequest request, CancellationToken ct)
    {
        var result = await CallAsync<AspSessionResult>(operation, request, ct);
        AspValidation.Require(result.SessionRef == request.SessionRef && result.State is "open" or "finalized" or "closed" or "aborted" or "unknown"
            && (result.Receipt is not null) == (result.State == "finalized"), "invalid_result", "Invalid session status.");
        if (result.Receipt is { } receipt)
        {
            ValidateObject(receipt.Item);
            AspValidation.Require(receipt.WrittenBytes >= 0 && receipt.Sha256 is { Length: 64 }
                && receipt.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "invalid_result", "Invalid retained write receipt.");
        }
        return result;
    }

    private async Task<T> ReadTransferAsync<T>(string operation, object request, CancellationToken ct)
    {
        AspValidation.Require(Description.Capabilities.Read, "unsupported_operation", "Read is unsupported.");
        var result = await CallAsync<T>(operation, request, ct);
        AspObject item; long sequence; string token; long offset; long selectedOffset; int count; long length; bool final; byte[] bytes;
        if (result is AspReadBlobResult blob)
        {
            (item, sequence, token, offset, selectedOffset, count, length, final) = (blob.Item, blob.Sequence, blob.SessionRef, blob.Offset, blob.SelectedOffset, blob.ByteLength, blob.SelectedLength, blob.Final);
            bytes = AspValidation.Content(blob.ContentBase64, Description.Policy.MaxReadBytes);
        }
        else if (result is AspReadTextResult text)
        {
            (item, sequence, token, offset, selectedOffset, count, length, final) = (text.Item, text.Sequence, text.SessionRef, text.Offset, text.SelectedOffset, text.ByteLength, text.SelectedLength, text.Final);
            try { bytes = new System.Text.UTF8Encoding(false, true).GetBytes(text.Content); }
            catch (System.Text.EncoderFallbackException) { throw new AspException("invalid_result", "Malformed text response."); }
        }
        else throw new AspException("invalid_result", "Missing transfer response.");
        ValidateObject(item);
        var wire = JsonSerializer.SerializeToElement(request, AspProtocol.JsonOptions);
        var requestedSequence = wire.TryGetProperty("sequence", out var seq) ? seq.GetInt64() : 0;
        AspValidation.Require(sequence == requestedSequence && token is { Length: > 0 and <= 4096 }
            && offset >= 0 && offset <= item.Length && count == bytes.Length && count <= Description.Policy.MaxReadBytes
            && count <= item.Length - offset && length >= count && selectedOffset >= 0 && selectedOffset <= offset
            && length <= item.Length - selectedOffset && offset - selectedOffset <= length - count
            && (count > 0 || final) && final == (offset - selectedOffset + count == length),
            "invalid_result", "Invalid transfer byte positions or content.");
        if (wire.TryGetProperty("sessionRef", out var selected)) AspValidation.Require(token == selected.GetString(), "invalid_result", "The response changed session identity.");
        if (wire.TryGetProperty("selection", out var fixedSelection))
        {
            var selection = AspValidation.Parse<AspReadSelection>(fixedSelection);
            var start = selection.Range?.Offset ?? 0;
            AspValidation.Require(selectedOffset == start && offset == start
                && length == Math.Min(selection.Range?.Length ?? long.MaxValue, item.Length - start),
                "invalid_result", "The response changed the initial selected byte range.");
        }
        if (final && selectedOffset == 0 && offset == 0 && count == item.Length && item.Sha256 is not null)
            AspValidation.Require(AspValidation.Digest(bytes) == item.Sha256, "invalid_result", "The complete response failed integrity validation.");
        return result;
    }
    private async Task<AspWriteResult> WriteTransferAsync(string operation, object request, long sequence, bool final, byte[] bytes, CancellationToken ct)
    {
        AspValidation.Require(Description.Capabilities.Write, "unsupported_operation", "Write is unsupported.");
        AspValidation.Require(bytes.Length <= Description.Policy.MaxWriteBytes, "limit_exceeded", "Chunk exceeds its hard bound.");
        var result = await CallAsync<AspWriteResult>(operation, request, ct);
        AspValidation.Require(result.Sequence == sequence && result.Final == final && result.WrittenBytes >= bytes.Length
            && result.SessionRef is { Length: > 0 and <= 4096 } && (result.Receipt is not null) == final,
            "invalid_result", "Invalid write finalization receipt.");
        var requestWire = JsonSerializer.SerializeToElement(request, AspProtocol.JsonOptions);
        if (requestWire.TryGetProperty("sessionRef", out var selected))
            AspValidation.Require(result.SessionRef == selected.GetString(), "invalid_result", "The response changed session identity.");
        if (result.Receipt is { } receipt)
        {
            ValidateObject(receipt.Item);
            var wire = JsonSerializer.SerializeToElement(request, AspProtocol.JsonOptions);
            if (wire.TryGetProperty("options", out var first))
            {
                var options = AspValidation.Parse<AspWriteOptions>(first);
                AspValidation.Require(receipt.Item.Key == options.Key && (options.Mode == "append" || receipt.Item.Length == bytes.Length)
                    && (options.Mode == "append" || receipt.Item.Sha256 is null || receipt.Item.Sha256 == AspValidation.Digest(bytes)),
                    "invalid_result", "The receipt differs from the selected target or content.");
            }
            AspValidation.Require(receipt.WrittenBytes == result.WrittenBytes && receipt.Sha256 is { Length: 64 }
                && (sequence != 0 || receipt.Sha256 == AspValidation.Digest(bytes)), "invalid_result", "Invalid write integrity evidence.");
        }
        return result;
    }

    private async Task<T> CallAsync<T>(string operation, object args, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        return await InvokeAsync<T>(mcp, toolPrefix, operation, args, Description.Store, deadline.Token);
    }

    private static async Task<T> InvokeAsync<T>(McpClient mcp, string toolPrefix, string operation, object args, AspStoreRef store, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToElement(args, AspProtocol.JsonOptions);
        var requestId = json.GetProperty("requestId").GetString();
        var wire = JsonSerializer.SerializeToElement(new AspInvocation<JsonElement>(store, json), AspProtocol.JsonOptions);
        var arguments = wire.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        var result = await mcp.CallToolAsync($"{toolPrefix}_{operation}", arguments, cancellationToken: ct);
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
                && error.Message is { Length: > 0 and <= 1024 } && (error.SessionRef is null || error.SessionRef.Length <= 4096), "invalid_result", "Invalid error envelope.");
            throw new AspException(error.Code, error.Message, error.SessionRef);
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
        AspValidation.Require(value.Store == expected, "forbidden", "The described target differs from the selected storage.");
        AspValidation.Require(!string.IsNullOrWhiteSpace(value.ProviderId) && value.ProviderId.Length <= 128
            && !string.IsNullOrWhiteSpace(value.ProviderVersion) && value.ProviderVersion.Length <= 128
            && value.ConfigurationSchema.ValueKind == JsonValueKind.Object && value.Capabilities is not null
            && value.Limits is { MaximumSessions: > 0, MaximumReceipts: > 0, MaximumReplayBytes: > 0, ReceiptWindowSeconds: > 0 }
            && value.Policy is not null && value.Transfers is { MaximumObjectBytes: > 0, DefaultChunkSizeBytes: > 0 }, "invalid_result", "Invalid provider description.");
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
        AspValidation.Require(AspValidation.Allows(Description.Policy, item.Key), "invalid_result", "Object is outside the authorized target policy.");
    }
}
