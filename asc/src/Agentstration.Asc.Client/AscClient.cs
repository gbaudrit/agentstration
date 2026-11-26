using System.Text.Json;
using Agentstration.Asc.Abstractions;
using ModelContextProtocol.Client;

namespace Agentstration.Asc.Client;

// Owns no transport: the host supplies an authenticated MCP connection and disposes it.
public sealed class AscClient
{
    private readonly McpClient mcp;
    private readonly string toolPrefix;
    private readonly TimeSpan timeout;
    public AscDescription Description { get; }

    private AscClient(McpClient mcp, string toolPrefix, AscDescription description, TimeSpan timeout)
    { this.mcp = mcp; this.toolPrefix = toolPrefix; Description = description; this.timeout = timeout; }

    public static async Task<AscClient> ConnectAsync(McpClient mcp, string toolPrefix, AscStoreRef expectedStore,
        TimeSpan? timeout = null, string? expectedProviderId = null, string? expectedProviderVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mcp);
        AscValidation.Require(mcp.NegotiatedProtocolVersion == AscContract.McpProtocolVersion,
            "incompatible_version", "Unsupported ASC MCP protocol version.");
        ArgumentNullException.ThrowIfNull(expectedStore);
        var duration = timeout ?? TimeSpan.FromSeconds(30);
        AscValidation.Require(duration > TimeSpan.Zero && duration <= TimeSpan.FromSeconds(30), "invalid_request", "Timeout must be between zero and 30 seconds.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        var tools = await mcp.ListToolsAsync(cancellationToken: deadline.Token);
        var describeTool = tools.SingleOrDefault(value => value.Name == $"{toolPrefix}_describe")
            ?? throw new AscException("unsupported_operation", "The ASC describe mapping is missing.");
        ValidateSchema(describeTool, "describe");
        var description = await InvokeAsync<AscDescription>(mcp, toolPrefix, "describe", new AscDescribeRequest(Guid.NewGuid().ToString("N")), expectedStore, deadline.Token);
        ValidateDescription(description, expectedStore);
        AscValidation.Require((expectedProviderId is null || description.ProviderId == expectedProviderId)
            && (expectedProviderVersion is null || description.ProviderVersion == expectedProviderVersion), "incompatible_provider", "Provider identity or version differs from the pinned provider.");
        var capabilities = description.Capabilities;
        foreach (var operation in new[] { "list", "stat", "read_blob", "read_text", "close_read", "write_blob", "write_text", "abort_write", "write_status" })
        {
            var supported = operation switch { "list" => capabilities.List, "stat" => capabilities.Stat, "read_blob" or "read_text" or "close_read" => capabilities.Read, _ => capabilities.Write };
            if (!supported) continue;
            var tool = tools.SingleOrDefault(value => value.Name == $"{toolPrefix}_{operation}")
                ?? throw new AscException("unsupported_operation", "An advertised ASC mapping is missing.");
            ValidateSchema(tool, operation);
        }
        return new(mcp, toolPrefix, description, duration);
    }

    public async Task<AscListResult> ListAsync(AscListRequest request, CancellationToken cancellationToken = default)
    {
        AscValidation.Require(Description.Capabilities.List, "unsupported_operation", "List is unsupported.");
        var result = await CallAsync<AscListResult>("list", request, cancellationToken);
        AscValidation.Require(result.Objects is not null && result.Objects.Count <= request.Limit, "invalid_result", "Invalid list result.");
        string? previous = null;
        foreach (var item in result.Objects)
        {
            ValidateObject(item);
            AscValidation.Require(item.Key.StartsWith(request.Prefix, StringComparison.Ordinal)
                && (previous is null || string.CompareOrdinal(item.Key, previous) > 0), "invalid_result", "List keys do not match the request.");
            previous = item.Key;
        }
        AscValidation.Require(result.Cursor is null || result.Cursor.Length is > 0 and <= 4096 && result.Objects.Count > 0,
            "invalid_result", "Invalid continuation.");
        return result;
    }

    public async Task<AscObject> StatAsync(AscReferenceRequest request, CancellationToken cancellationToken = default)
    {
        AscValidation.Require(Description.Capabilities.Stat, "unsupported_operation", "Stat is unsupported.");
        var result = await CallAsync<AscObject>("stat", request, cancellationToken);
        ValidateObject(result);
        return result;
    }

    public Task<AscReadBlobResult> ReadBlobAsync(AscReadBlobRequest request, CancellationToken cancellationToken = default) =>
        ReadTransferAsync<AscReadBlobResult>("read_blob", request, cancellationToken);
    public Task<AscReadTextResult> ReadTextAsync(AscReadTextRequest request, CancellationToken cancellationToken = default) =>
        ReadTransferAsync<AscReadTextResult>("read_text", request, cancellationToken);
    public Task<AscWriteResult> WriteBlobAsync(AscWriteBlobRequest request, CancellationToken cancellationToken = default) =>
        WriteTransferAsync("write_blob", request, request.Sequence, request.Final, AscValidation.Content(request.ContentBase64, Description.Policy.MaxWriteBytes), cancellationToken);
    public Task<AscWriteResult> WriteTextAsync(AscWriteTextRequest request, CancellationToken cancellationToken = default) =>
        WriteTransferAsync("write_text", request, request.Sequence, request.Final, AscValidation.Text(request.Content, Description.Policy.MaxWriteBytes), cancellationToken);
    public Task<AscSessionResult> CloseReadAsync(AscSessionRequest request, CancellationToken cancellationToken = default) => ControlAsync("close_read", request, cancellationToken);
    public Task<AscSessionResult> AbortWriteAsync(AscSessionRequest request, CancellationToken cancellationToken = default) => ControlAsync("abort_write", request, cancellationToken);
    public Task<AscSessionResult> WriteStatusAsync(AscSessionRequest request, CancellationToken cancellationToken = default) => ControlAsync("write_status", request, cancellationToken);

    private async Task<AscSessionResult> ControlAsync(string operation, AscSessionRequest request, CancellationToken ct)
    {
        var result = await CallAsync<AscSessionResult>(operation, request, ct);
        AscValidation.Require(result.SessionRef == request.SessionRef && result.State is "open" or "finalized" or "closed" or "aborted" or "unknown"
            && (result.Receipt is not null) == (result.State == "finalized"), "invalid_result", "Invalid session status.");
        if (result.Receipt is { } receipt)
        {
            ValidateObject(receipt.Item);
            AscValidation.Require(receipt.WrittenBytes >= 0 && receipt.Sha256 is { Length: 64 }
                && receipt.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "invalid_result", "Invalid retained write receipt.");
        }
        return result;
    }

    private async Task<T> ReadTransferAsync<T>(string operation, object request, CancellationToken ct)
    {
        AscValidation.Require(Description.Capabilities.Read, "unsupported_operation", "Read is unsupported.");
        var result = await CallAsync<T>(operation, request, ct);
        AscObject item; long sequence; string token; long offset; long selectedOffset; int count; long length; bool final; byte[] bytes;
        if (result is AscReadBlobResult blob)
        {
            (item, sequence, token, offset, selectedOffset, count, length, final) = (blob.Item, blob.Sequence, blob.SessionRef, blob.Offset, blob.SelectedOffset, blob.ByteLength, blob.SelectedLength, blob.Final);
            bytes = AscValidation.Content(blob.ContentBase64, Description.Policy.MaxReadBytes);
        }
        else if (result is AscReadTextResult text)
        {
            (item, sequence, token, offset, selectedOffset, count, length, final) = (text.Item, text.Sequence, text.SessionRef, text.Offset, text.SelectedOffset, text.ByteLength, text.SelectedLength, text.Final);
            try { bytes = new System.Text.UTF8Encoding(false, true).GetBytes(text.Content); }
            catch (System.Text.EncoderFallbackException) { throw new AscException("invalid_result", "Malformed text response."); }
        }
        else throw new AscException("invalid_result", "Missing transfer response.");
        ValidateObject(item);
        var wire = JsonSerializer.SerializeToElement(request, AscContract.JsonOptions);
        var requestedSequence = wire.TryGetProperty("sequence", out var seq) ? seq.GetInt64() : 0;
        AscValidation.Require(sequence == requestedSequence && token is { Length: > 0 and <= 4096 }
            && offset >= 0 && offset <= item.Length && count == bytes.Length && count <= Description.Policy.MaxReadBytes
            && count <= item.Length - offset && length >= count && selectedOffset >= 0 && selectedOffset <= offset
            && length <= item.Length - selectedOffset && offset - selectedOffset <= length - count
            && (count > 0 || final) && final == (offset - selectedOffset + count == length),
            "invalid_result", "Invalid transfer byte positions or content.");
        if (wire.TryGetProperty("sessionRef", out var selected)) AscValidation.Require(token == selected.GetString(), "invalid_result", "The response changed session identity.");
        if (wire.TryGetProperty("selection", out var fixedSelection))
        {
            var selection = AscValidation.Parse<AscReadSelection>(fixedSelection);
            var start = selection.Range?.Offset ?? 0;
            AscValidation.Require(selectedOffset == start && offset == start
                && length == Math.Min(selection.Range?.Length ?? long.MaxValue, item.Length - start),
                "invalid_result", "The response changed the initial selected byte range.");
        }
        if (final && selectedOffset == 0 && offset == 0 && count == item.Length && item.Sha256 is not null)
            AscValidation.Require(AscValidation.Digest(bytes) == item.Sha256, "invalid_result", "The complete response failed integrity validation.");
        return result;
    }
    private async Task<AscWriteResult> WriteTransferAsync(string operation, object request, long sequence, bool final, byte[] bytes, CancellationToken ct)
    {
        AscValidation.Require(Description.Capabilities.Write, "unsupported_operation", "Write is unsupported.");
        AscValidation.Require(bytes.Length <= Description.Policy.MaxWriteBytes, "limit_exceeded", "Chunk exceeds its hard bound.");
        var result = await CallAsync<AscWriteResult>(operation, request, ct);
        AscValidation.Require(result.Sequence == sequence && result.Final == final && result.WrittenBytes >= bytes.Length
            && result.SessionRef is { Length: > 0 and <= 4096 } && (result.Receipt is not null) == final,
            "invalid_result", "Invalid write finalization receipt.");
        var requestWire = JsonSerializer.SerializeToElement(request, AscContract.JsonOptions);
        if (requestWire.TryGetProperty("sessionRef", out var selected))
            AscValidation.Require(result.SessionRef == selected.GetString(), "invalid_result", "The response changed session identity.");
        if (result.Receipt is { } receipt)
        {
            ValidateObject(receipt.Item);
            var wire = JsonSerializer.SerializeToElement(request, AscContract.JsonOptions);
            if (wire.TryGetProperty("options", out var first))
            {
                var options = AscValidation.Parse<AscWriteOptions>(first);
                AscValidation.Require(receipt.Item.Key == options.Key && (options.Mode == "append" || receipt.Item.Length == bytes.Length)
                    && (options.Mode == "append" || receipt.Item.Sha256 is null || receipt.Item.Sha256 == AscValidation.Digest(bytes)),
                    "invalid_result", "The receipt differs from the selected target or content.");
            }
            AscValidation.Require(receipt.WrittenBytes == result.WrittenBytes && receipt.Sha256 is { Length: 64 }
                && (sequence != 0 || receipt.Sha256 == AscValidation.Digest(bytes)), "invalid_result", "Invalid write integrity evidence.");
        }
        return result;
    }

    private async Task<T> CallAsync<T>(string operation, object args, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        return await InvokeAsync<T>(mcp, toolPrefix, operation, args, Description.Store, deadline.Token);
    }

    private static async Task<T> InvokeAsync<T>(McpClient mcp, string toolPrefix, string operation, object args, AscStoreRef store, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToElement(args, AscContract.JsonOptions);
        var requestId = json.GetProperty("requestId").GetString();
        var wire = JsonSerializer.SerializeToElement(new AscInvocation<JsonElement>(store, json), AscContract.JsonOptions);
        var arguments = wire.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        var result = await mcp.CallToolAsync($"{toolPrefix}_{operation}", arguments, cancellationToken: ct);
        AscValidation.Require(result.StructuredContent is { ValueKind: JsonValueKind.Object }, "invalid_result", "Structured ASC content is required.");
        AscValidation.Require(result.StructuredContent.Value.GetRawText().Length <= 200_000, "invalid_result", "ASC result exceeds the wire limit.");
        AscReply<T> reply;
        try { reply = AscValidation.Parse<AscReply<T>>(result.StructuredContent.Value); }
        catch (AscException) { throw new AscException("invalid_result", "Result does not match the ASC schema."); }
        AscValidation.Require(reply.AscVersion == AscContract.Version, "incompatible_version", "Unsupported ASC response version.");
        AscValidation.Require(reply.RequestId == requestId, "invalid_result", "Response request identity does not match.");
        AscValidation.Require((reply.Error is not null) == (result.IsError == true)
            && (reply.Error is null ? reply.Data is not null : reply.Data is null), "invalid_result", "Invalid result envelope.");
        if (reply.Error is { } error)
        {
            AscValidation.Require(!string.IsNullOrWhiteSpace(error.Code) && error.Code.Length <= 128
                && error.Message is { Length: > 0 and <= 1024 } && (error.SessionRef is null || error.SessionRef.Length <= 4096), "invalid_result", "Invalid error envelope.");
            throw new AscException(error.Code, error.Message, error.SessionRef);
        }
        return reply.Data!;
    }

    private static void ValidateSchema(McpClientTool tool, string operation)
    {
        AscValidation.Require(JsonElement.DeepEquals(tool.ProtocolTool.InputSchema, AscSchemas.Input(operation))
            && tool.ProtocolTool.OutputSchema is { } schema && JsonElement.DeepEquals(schema, AscSchemas.Output(operation)),
            "incompatible_schema", "The MCP Tool schema does not match the ASC profile.");
    }

    private static void ValidateDescription(AscDescription value, AscStoreRef expected)
    {
        AscValidation.Require(value.AscVersion == AscContract.Version && value.Profile == AscContract.Profile,
            "incompatible_version", "Unsupported ASC profile or version.");
        AscValidation.Require(value.Store == expected, "forbidden", "The described target differs from the selected storage.");
        AscValidation.Require(!string.IsNullOrWhiteSpace(value.ProviderId) && value.ProviderId.Length <= 128
            && !string.IsNullOrWhiteSpace(value.ProviderVersion) && value.ProviderVersion.Length <= 128
            && value.ConfigurationSchema.ValueKind == JsonValueKind.Object && value.Capabilities is not null
            && value.Limits is { MaximumSessions: > 0, MaximumReceipts: > 0, MaximumReplayBytes: > 0, ReceiptWindowSeconds: > 0 }
            && value.Policy is not null && value.Transfers is { MaximumObjectBytes: > 0, DefaultChunkSizeBytes: > 0 }, "invalid_result", "Invalid provider description.");
        AscValidation.Policy(value.Policy);
        var c = value.Capabilities;
        AscValidation.Require(!c.Delete && !c.StableVersions && !c.Retention, "unsupported_capability", "This profile implementation offers no delete, stable-version or retention operation.");
        AscValidation.Require(c.List == value.Policy.CanList && c.Stat == value.Policy.CanStat
            && c.Read == value.Policy.CanRead && c.Write == value.Policy.CanWrite, "invalid_result", "Capabilities and effective policy disagree.");
    }

    private void ValidateObject(AscObject item)
    {
        AscValidation.Require(item is not null && item.Reference is { Length: > 0 and <= 4096 }, "invalid_result", "Invalid object reference.");
        AscValidation.Item(new(item.Key, item.Version, item.Length, item.Sha256));
        AscValidation.Require(AscValidation.Allows(Description.Policy, item.Key), "invalid_result", "Object is outside the authorized target policy.");
    }
}
