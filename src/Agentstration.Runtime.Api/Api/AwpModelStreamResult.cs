using System.Text;
using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;

namespace Agentstration.Runtime.Api.Api;

internal sealed class AwpModelStreamResult(
    Guid messageId,
    IAsyncEnumerable<RuntimeGovernedModelUpdate> updates,
    TimeProvider timeProvider) : IResult
{
    private static readonly byte[] DataPrefix = "data: "u8.ToArray();
    private static readonly byte[] EventSuffix = "\n\n"u8.ToArray();
    private const int MaxEvents = 4096;
    private const int MaxEventBytes = 64 * 1024;
    private const int MaxTotalBytes = 8 * 1024 * 1024;
    private const int MaxToolArguments = 64 * 1024;
    private const int DeltaLength = 8 * 1024;

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Append("X-Accel-Buffering", "no");

        var streamId = Guid.NewGuid();
        var writer = new EventWriter(response, messageId, MaxEvents, MaxEventBytes, MaxTotalBytes);
        string? modelId = null;
        string? providerType = null;
        string? finishReason = null;

        try
        {
            await writer.WriteAsync(new AwpModelStreamStarted(
                streamId, writer.NextSequence, timeProvider.GetUtcNow()), terminal: false,
                httpContext.RequestAborted);

            await foreach (var update in updates.WithCancellation(httpContext.RequestAborted))
            {
                modelId = update.ModelId ?? modelId;
                providerType = update.ProviderType ?? providerType;
                finishReason = update.FinishReason ?? finishReason;

                foreach (var content in update.Contents)
                    await WriteContentAsync(content, streamId, writer, httpContext.RequestAborted);

                if (update.InputTokens is not null || update.OutputTokens is not null)
                    await writer.WriteAsync(new AwpModelUsage(
                        streamId, writer.NextSequence, timeProvider.GetUtcNow(),
                        update.InputTokens, update.OutputTokens), terminal: false,
                        httpContext.RequestAborted);
            }

            await writer.WriteAsync(new AwpModelStreamCompleted(
                streamId, writer.NextSequence, timeProvider.GetUtcNow(), modelId, providerType, finishReason),
                terminal: true, httpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            // The Worker disconnected; there is no recipient for a terminal event.
        }
        catch (Exception exception)
        {
            if (httpContext.RequestAborted.IsCancellationRequested) return;
            var (code, message) = Error(exception);
            try
            {
                await writer.WriteAsync(new AwpModelStreamFailed(
                    streamId, writer.NextSequence, timeProvider.GetUtcNow(), code, message),
                    terminal: true, httpContext.RequestAborted);
            }
            catch (Exception terminalException) when (terminalException is IOException
                or OperationCanceledException or AwpModelStreamLimitException)
            {
                // The transport can no longer carry the explicit terminal failure.
            }
        }
    }

    private async Task WriteContentAsync(
        RuntimeGovernedModelContent content,
        Guid streamId,
        EventWriter writer,
        CancellationToken cancellationToken)
    {
        switch (content)
        {
            case RuntimeGovernedModelText text:
                foreach (var delta in Chunks(text.Text, DeltaLength))
                    await writer.WriteAsync(new AwpModelTextDelta(
                        streamId, writer.NextSequence, timeProvider.GetUtcNow(), delta),
                        terminal: false, cancellationToken);
                break;
            case RuntimeGovernedModelToolCall call:
                var arguments = call.Arguments.GetRawText();
                if (arguments.Length > MaxToolArguments)
                    throw new AwpModelStreamLimitException("model_tool_arguments_too_large",
                        "Streamed Tool arguments exceed the AWP limit.");
                await writer.WriteAsync(new AwpModelToolCallStarted(
                    streamId, writer.NextSequence, timeProvider.GetUtcNow(), call.CallId, call.Name),
                    terminal: false, cancellationToken);
                foreach (var delta in Chunks(arguments, DeltaLength))
                    await writer.WriteAsync(new AwpModelToolCallArgumentsDelta(
                        streamId, writer.NextSequence, timeProvider.GetUtcNow(), call.CallId, delta),
                        terminal: false, cancellationToken);
                await writer.WriteAsync(new AwpModelToolCallDone(
                    streamId, writer.NextSequence, timeProvider.GetUtcNow(), call.CallId),
                    terminal: false, cancellationToken);
                break;
            case RuntimeGovernedModelToolResult result:
                await writer.WriteAsync(new AwpModelToolResult(
                    streamId, writer.NextSequence, timeProvider.GetUtcNow(), result.CallId, result.Result),
                    terminal: false, cancellationToken);
                break;
        }
    }

    private static IEnumerable<string> Chunks(string value, int length)
    {
        for (var offset = 0; offset < value.Length;)
        {
            var count = Math.Min(length, value.Length - offset);
            if (count < value.Length - offset && char.IsHighSurrogate(value[offset + count - 1])) count--;
            yield return value.Substring(offset, count);
            offset += count;
        }
    }

    private static (string Code, string Message) Error(Exception exception) => exception switch
    {
        AwpModelStreamLimitException limit => (limit.Code, limit.Message),
        RuntimeExecutionMaterialException material => (material.Code, material.Message),
        RuntimeAssignmentException assignment => (assignment.Code, assignment.Message),
        OperationCanceledException => ("model_stream_cancelled", "The governed model stream was cancelled."),
        _ => ("model_stream_failed", "The governed model stream failed.")
    };

    private sealed class EventWriter(
        HttpResponse response,
        Guid messageId,
        int maxEvents,
        int maxEventBytes,
        int maxTotalBytes)
    {
        private int eventCount;
        private int totalBytes;

        public long NextSequence => eventCount;

        public async Task WriteAsync(
            AwpModelStreamEvent streamEvent,
            bool terminal,
            CancellationToken cancellationToken)
        {
            if (eventCount >= maxEvents || !terminal && eventCount >= maxEvents - 1)
                throw new AwpModelStreamLimitException("model_stream_event_limit",
                    "The governed model stream exceeded its event limit.");
            var envelope = new AwpEnvelope<AwpModelStreamEvent>(
                AwpProtocol.Version, messageId, streamEvent.SentAt, streamEvent);
            var json = JsonSerializer.SerializeToUtf8Bytes(envelope, AwpProtocol.JsonOptions);
            var size = 6 + json.Length + 2;
            if (size > maxEventBytes)
                throw new AwpModelStreamLimitException("model_stream_event_too_large",
                    "An AWP model stream event exceeded its size limit.");
            if (!terminal && totalBytes + size > maxTotalBytes - maxEventBytes)
                throw new AwpModelStreamLimitException("model_stream_too_large",
                    "The governed model stream exceeded its total size limit.");
            if (totalBytes + size > maxTotalBytes)
                throw new AwpModelStreamLimitException("model_stream_too_large",
                    "The governed model stream exceeded its total size limit.");

            await response.Body.WriteAsync(DataPrefix, cancellationToken);
            await response.Body.WriteAsync(json, cancellationToken);
            await response.Body.WriteAsync(EventSuffix, cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
            eventCount++;
            totalBytes += size;
        }
    }

    private sealed class AwpModelStreamLimitException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
