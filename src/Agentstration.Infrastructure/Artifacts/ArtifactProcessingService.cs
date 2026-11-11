using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Infrastructure.Storage;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure.Artifacts;

/// <summary>Execution-owned content capabilities. State contains references only; documents live in Artifact storage.</summary>
public sealed class ArtifactProcessingService(ArtifactManagementService artifacts, IArtifactDurableStore durable,
    WorkspaceArtifactStorage selectableStorage, IRuntimeExecutionStateStore states, IFlowRepository runs,
    TimeProvider clock) : IDisposable
{
    private const string RuntimeType = "artifact-processing-v1";
    private const int MaximumInputBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    // Bounded stripes serialize retrying and concurrent Tools in the standalone authority.
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public void Dispose() { foreach (var gate in gates) gate.Dispose(); }

    private sealed record Input(string Id, string Kind, string Name, string MediaType, long Length, string Sha256, string? Reference = null);
    private sealed record WriteReceipt(string Id, string Digest, int Revision);
    private sealed record Session(Input Input, string OutputId, string OutputName, string MediaType,
        int Revision, int? CompletedRevision, DateTimeOffset ExpiresAt, IReadOnlyList<WriteReceipt> Writes,
        string? DurableReference = null, JsonElement? Result = null);
    private sealed record Cursor(string Session, string Side, int Revision, int Offset);

    public async Task<JsonElement> ExecuteOwnerAsync(FlowRun owner, ArtifactTransformFlowStepDefinition step,
        int index, string action, JsonElement arguments, CancellationToken token)
    {
        if (index < 0 || index >= step.MaximumArtifacts || owner.Status.IsTerminal()) throw Error("artifact_processing_scope_invalid");
        var attempt = Math.Max(1, owner.Steps.Single(value => value.StepName == step.Name).Attempt);
        var key = Key(owner, step.Name, attempt, index);
        var gate = Gate(key);
        await gate.WaitAsync(token);
        try
        {
            var session = await LoadAsync(owner, key, token);
            if (action == "open")
            {
                if (session is null)
                {
                    var input = await ResolveInputAsync(arguments, owner, key, token);
                    if (input.Length > MaximumInputBytes || step.Mode == ArtifactProcessingMode.Content && input.Length > step.MaximumContentBytes)
                        throw Error("artifact_processing_content_too_large");
                    var output = await StageAsync(owner, step, key + ":0", step.OutputFileName ?? input.Name,
                        step.OutputMediaType ?? input.MediaType, ReadOnlyMemory<byte>.Empty, input, token);
                    session = new(input, output.ArtifactId.ToString(), output.FileName, output.MediaType, 0, null,
                        clock.GetUtcNow() + TimeSpan.FromHours(1), []);
                    await SaveAsync(owner, key, session, token);
                }
                else if (session.Input.Id != Text(arguments, "artifactId")) throw Error("artifact_processing_input_changed");
                EnsureLive(session);
                object? content = null;
                if (step.Mode == ArtifactProcessingMode.Content)
                {
                    var text = await InputTextAsync(owner, session.Input, token);
                    if (IsJson(session.Input.MediaType))
                    {
                        try { content = JsonSerializer.Deserialize<JsonElement>(text); }
                        catch (JsonException) { throw Error("artifact_processing_json_invalid"); }
                    }
                    else content = text;
                }
                return Element(new { artifact = new { artifactId = session.Input.Id, name = session.Input.Name,
                    mediaType = session.Input.MediaType, sizeBytes = session.Input.Length, sha256 = session.Input.Sha256 },
                    content, inputAccess = new { reference = key, access = "read" },
                    outputAccess = new { reference = key, access = "readWrite" }, revision = session.Revision });
            }
            if (session is null) throw Error("artifact_processing_context_missing");
            EnsureLive(session);
            if (action == "abort")
            {
                if (session.Result is null) await artifacts.PurgeAsync(StagedArtifactId.Parse(session.OutputId), token);
                await SaveAsync(owner, key, session with { ExpiresAt = clock.GetUtcNow() }, token);
                return Element(new { aborted = true });
            }
            if (action is not ("prepare" or "finish")) throw Error("artifact_processing_action_invalid");
            if (session.Result is { } result) return result.Clone();
            if (step.Mode == ArtifactProcessingMode.Content && session.CompletedRevision is null)
            {
                var outputText = arguments.ValueKind == JsonValueKind.String ? arguments.GetString()! : arguments.GetRawText();
                // A text-producing Agent can return JSON text, which must be valid for JSON output.
                ValidateContent(session.MediaType, outputText);
                session = await WriteAsync(owner, step, key, session, Element(new { content = outputText,
                    expectedRevision = 0, requestId = "capture-result", offset = 0, deleteLength = 0 }), token);
                session = session with { CompletedRevision = session.Revision };
                await SaveAsync(owner, key, session, token);
            }
            if (session.CompletedRevision != session.Revision) throw Error("artifact_processing_output_incomplete");
            if (action == "prepare") return Element(new { prepared = true, revision = session.Revision });
            var outputStage = (await artifacts.GetStagedAsync(StagedArtifactId.Parse(session.OutputId), null,
                ArtifactLeaseOperation.Read, token))?.Value ?? throw Error("artifact_processing_output_unavailable");
            var reference = session.DurableReference;
            if (reference is null)
            {
                reference = await selectableStorage.CreateReferenceAsync(owner.WorkspaceId, Hash(key + ":" + outputStage.Sha256),
                    outputStage.Length, outputStage.Sha256!, token);
                session = session with { DurableReference = reference };
                await SaveAsync(owner, key, session, token);
            }
            ArtifactBackendStat? existing = null;
            try { existing = await durable.StatAsync(owner.WorkspaceId, reference, token); }
            catch (ArtifactValidationException exception) when (exception.Code == "flow_run_artifact_content_not_found") { }
            if (existing is not null && (existing.Length != outputStage.Length || existing.Sha256 != outputStage.Sha256))
                throw Error("artifact_processing_output_changed");
            if (existing is null) await durable.ResetAsync(owner.WorkspaceId, reference, token);
            long offset = existing?.Length ?? 0;
            var maximumWrite = await selectableStorage.MaximumChunkBytesAsync(owner.WorkspaceId, reference, true, token);
            while (offset < outputStage.Length)
            {
                var chunk = await artifacts.ReadAsync(outputStage.ArtifactId, offset,
                    (int)Math.Min(maximumWrite, outputStage.Length - offset), null, token);
                var bytes = Convert.FromBase64String(chunk.ContentBase64);
                if (bytes.Length == 0) throw Error("artifact_processing_output_incomplete");
                offset = await durable.WriteAsync(owner.WorkspaceId, reference, offset, bytes, token);
            }
            var stat = await durable.StatAsync(owner.WorkspaceId, reference, token);
            var materialization = await selectableStorage.DescribeReferenceAsync(owner.WorkspaceId, reference, token);
            var registered = await artifacts.CompleteFlowRunArtifactAsync(outputStage.ArtifactId, new(owner.Id, step.Name,
                new() { StorageFlowRunId = string.Empty, OpaqueReference = reference,
                    Materialization = materialization, MediaType = session.MediaType, Length = stat.Length, Sha256 = stat.Sha256,
                    Provenance = new Dictionary<string, string> { ["fileName"] = session.OutputName,
                        ["inputArtifactId"] = session.Input.Id, ["inputArtifactKind"] = session.Input.Kind, ["inputSha256"] = session.Input.Sha256,
                        ["processingRunId"] = ProcessingRunId(owner, step, attempt, index), ["provider"] = "artifact-processing" } }), token);
            var manifestItem = Element(new { artifactId = registered.Value.ArtifactId.ToString(), kind = "durable",
                disposition = "publishable", name = session.OutputName, mediaType = session.MediaType, digest = "sha256:" + stat.Sha256 });
            await SaveAsync(owner, key, session with { Result = manifestItem }, token);
            return manifestItem;
        }
        catch (ArtifactValidationException exception) { throw Error(exception.Code); }
        finally { gate.Release(); }
    }

    public async Task<JsonElement> ExecuteToolAsync(FlowRun processingRun, string action, JsonElement arguments, CancellationToken token)
    {
        var context = processingRun.ArtifactProcessing ?? throw Error("artifact_processing_scope_invalid");
        var declaration = RuntimeArtifactProcessingTools.Create().SingleOrDefault(tool => tool.ExternalId == action)
            ?? throw Error("artifact_processing_action_invalid");
        var properties = declaration.InputSchema.GetProperty("properties");
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any(property => !properties.TryGetProperty(property.Name, out _)))
            throw Error("artifact_processing_arguments_invalid");
        var owner = (await runs.GetRunExactAsync(processingRun.ExecutionScopeRef, context.OwnerRunId, token))?.Value
            ?? throw Error("artifact_processing_scope_invalid");
        if (owner.Scope != processingRun.Scope || owner.Status.IsTerminal()
            || processingRun.Status.IsTerminal() || processingRun.RootFlowRunId != (owner.RootFlowRunId ?? owner.Id)
            || Math.Max(1, owner.Steps.Single(value => value.StepName == context.OwnerStepName).Attempt) != context.Attempt)
            throw Error("artifact_processing_scope_invalid");
        var step = owner.DefinitionSnapshot.Graph!.Steps.OfType<ArtifactTransformFlowStepDefinition>()
            .Single(value => value.Name == context.OwnerStepName);
        if (step.Mode != ArtifactProcessingMode.Access) throw Error("artifact_processing_access_not_enabled");
        var key = Key(owner, step.Name, context.Attempt, context.Index);
        var gate = Gate(key);
        await gate.WaitAsync(token);
        try
        {
            var session = await LoadAsync(owner, key, token) ?? throw Error("artifact_processing_context_missing");
            EnsureLive(session);
            if (session.Result is not null) throw Error("artifact_processing_closed");
            var side = action.StartsWith("input_", StringComparison.Ordinal) ? "input" : "output";
            if (action is "input_info" or "output_info")
                return Element(new { name = side == "input" ? session.Input.Name : session.OutputName,
                    mediaType = side == "input" ? session.Input.MediaType : session.MediaType,
                    sizeBytes = side == "input" ? session.Input.Length : (await OutputStageAsync(session, token)).Length,
                    revision = side == "input" ? 0 : session.Revision, completed = side == "output" && session.CompletedRevision == session.Revision,
                    rangeUnit = "utf16Characters" });
            if (action == "output_write")
            {
                session = await WriteAsync(owner, step, key, session, arguments, token);
                return Element(new { revision = session.Revision, completed = false });
            }
            if (action == "output_complete")
            {
                if (Integer(arguments, "expectedRevision", -1) != session.Revision) throw Error("artifact_processing_revision_conflict");
                ValidateContent(session.MediaType, await OutputTextAsync(session, token));
                await SaveAsync(owner, key, session with { CompletedRevision = session.Revision }, token);
                return Element(new { revision = session.Revision, completed = true });
            }
            if (action is not ("input_read" or "output_read" or "input_search_regex" or "output_search_regex"))
                throw Error("artifact_processing_action_invalid");
            var text = side == "input" ? await InputTextAsync(owner, session.Input, token) : await OutputTextAsync(session, token);
            var revision = side == "input" ? 0 : session.Revision;
            var offset = Integer(arguments, "offset", 0);
            if (OptionalText(arguments, "cursor") is { } encoded)
            {
                if (encoded.Length > 2048) throw Error("artifact_processing_cursor_invalid");
                Cursor cursor;
                try { cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(encoded), Json) ?? throw Error("artifact_processing_cursor_invalid"); }
                catch (Exception exception) when (exception is FormatException or JsonException) { throw Error("artifact_processing_cursor_invalid"); }
                if (cursor.Session != key || cursor.Side != side || cursor.Revision != revision) throw Error("artifact_processing_cursor_invalid");
                offset = cursor.Offset;
            }
            if (offset < 0 || offset > text.Length || SplitsScalar(text, offset)) throw Error("artifact_processing_range_invalid");
            if (action.EndsWith("_read", StringComparison.Ordinal))
            {
                var length = Integer(arguments, "length", 4096);
                if (length is < 1 or > 16384) throw Error("artifact_processing_range_invalid");
                var next = Math.Min(text.Length, offset + length);
                if (SplitsScalar(text, next)) next--;
                if (next == offset && next < text.Length) next = Math.Min(text.Length, offset + 2);
                return Element(new { content = text[offset..next], offset, nextOffset = next, totalCharacters = text.Length,
                    endOfContent = next == text.Length, revision, cursor = next == text.Length ? null : Encode(new(key, side, revision, next)) });
            }
            var pattern = Text(arguments, "pattern");
            var limit = Integer(arguments, "maxMatches", 20);
            if (pattern.Length is < 1 or > 1024 || limit is < 1 or > 50) throw Error("artifact_processing_regex_invalid");
            try
            {
                var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                var matches = new List<object>();
                var next = offset;
                var ended = false;
                for (var match = regex.Match(text, offset); ; match = match.NextMatch())
                {
                    token.ThrowIfCancellationRequested();
                    if (!match.Success) { ended = true; break; }
                    if (matches.Count == limit) break;
                    var start = SplitsScalar(text, match.Index) ? match.Index - 1 : match.Index;
                    var end = match.Index + match.Length;
                    if (SplitsScalar(text, end)) end++;
                    matches.Add(new { offset = start, length = end - start, excerpt = text.Substring(start, Math.Min(256, end - start)) });
                    next = Math.Min(text.Length, match.Index + Math.Max(1, match.Length));
                    if (SplitsScalar(text, next)) next++;
                    if (match.Index == text.Length) { ended = true; break; }
                }
                return Element(new { matches, revision, endOfContent = ended, cursor = ended ? null : Encode(new(key, side, revision, next)) });
            }
            catch (ArgumentException) { throw Error("artifact_processing_regex_invalid"); }
            catch (RegexMatchTimeoutException) { throw Error("artifact_processing_regex_timeout"); }
        }
        catch (ArtifactValidationException exception) { throw Error(exception.Code); }
        finally { gate.Release(); }
    }

    private async Task<Session> WriteAsync(FlowRun owner, ArtifactTransformFlowStepDefinition step, string key,
        Session session, JsonElement arguments, CancellationToken token)
    {
        var command = Text(arguments, "requestId");
        if (command.Length is < 1 or > 128) throw Error("artifact_processing_request_id_invalid");
        var digest = Hash(arguments.GetRawText());
        if (session.Writes.SingleOrDefault(write => write.Id == command) is { } replay)
        {
            if (replay.Digest != digest) throw Error("artifact_processing_write_replay_conflict");
            return session;
        }
        if (Integer(arguments, "expectedRevision", -1) != session.Revision) throw Error("artifact_processing_revision_conflict");
        if (session.Revision >= 128) throw Error("artifact_processing_write_limit");
        var previous = await OutputTextAsync(session, token);
        var offset = Integer(arguments, "offset", previous.Length);
        var deleted = Integer(arguments, "deleteLength", 0);
        if (offset < 0 || deleted < 0 || offset > previous.Length || deleted > previous.Length - offset
            || SplitsScalar(previous, offset) || SplitsScalar(previous, offset + deleted)) throw Error("artifact_processing_range_invalid");
        byte[] bytes;
        try { bytes = Utf8.GetBytes(previous[..offset] + Text(arguments, "content") + previous[(offset + deleted)..]); }
        catch (EncoderFallbackException) { throw Error("artifact_processing_utf8_required"); }
        if (bytes.Length > step.MaximumOutputBytes) throw Error("artifact_processing_output_too_large");
        var stage = await StageAsync(owner, step, key + ":" + command + ":" + digest,
            session.OutputName, session.MediaType, bytes, session.Input, token);
        var updated = session with { OutputId = stage.ArtifactId.ToString(), Revision = session.Revision + 1, CompletedRevision = null,
            Writes = [.. session.Writes, new(command, digest, session.Revision + 1)] };
        await SaveAsync(owner, key, updated, token);
        return updated;
    }

    private async Task<Input> ResolveInputAsync(JsonElement value, FlowRun owner, string key, CancellationToken token)
    {
        var id = Text(value, "artifactId");
        var kind = OptionalText(value, "kind")?.ToLowerInvariant() ?? "durable";
        if (kind == "inline")
        {
            var encoded = Text(value, "contentBase64");
            if (encoded.Length > 87384) throw Error("artifact_processing_content_too_large");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw Error("artifact_processing_content_invalid"); }
            var input = new Input(id, "inline", OptionalText(value, "name") ?? "input.txt",
                OptionalText(value, "mediaType") ?? "text/plain", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var staged = await StageAsync(owner, new() { Name = "input" }, key + ":input", input.Name, input.MediaType, bytes, input, token);
            return input with { Reference = staged.ArtifactId.ToString() };
        }
        if (!Guid.TryParseExact(id, "N", out _)) throw Error("artifact_processing_input_invalid");
        if (kind == "staged")
        {
            var stage = (await artifacts.GetStagedAsync(StagedArtifactId.Parse(id), null, ArtifactLeaseOperation.Read, token))?.Value
                ?? throw Error("artifact_processing_input_unavailable");
            if (stage.ArtifactStatus is not (StagedArtifactStatus.Sealed or StagedArtifactStatus.Persisted)) throw Error("artifact_processing_input_not_sealed");
            return new(id, kind, stage.FileName, stage.MediaType, stage.Length, stage.Sha256!, id);
        }
        if (kind != "durable") throw Error("artifact_processing_input_invalid");
        var artifact = (await artifacts.GetFlowRunArtifactForContentReadAsync(FlowRunArtifactId.Parse(id), true, token))?.Value
            ?? throw Error("artifact_processing_input_unavailable");
        return new(id, kind, artifact.Receipt.Provenance.GetValueOrDefault("fileName") ?? "input.txt", artifact.Receipt.MediaType,
            artifact.Receipt.Length, artifact.Receipt.Sha256, artifact.Receipt.OpaqueReference);
    }

    private async Task<string> InputTextAsync(FlowRun owner, Input input, CancellationToken token)
    {
        if (!IsText(input.MediaType)) throw Error("artifact_processing_extraction_required");
        using var bytes = new MemoryStream();
        if (input.Kind == "durable")
        {
            var current = (await artifacts.GetFlowRunArtifactForContentReadAsync(FlowRunArtifactId.Parse(input.Id), true, token))?.Value;
            if (current?.Receipt.Sha256 != input.Sha256 || current.Receipt.Length != input.Length) throw Error("artifact_processing_input_changed");
        }
        while (bytes.Length < input.Length)
        {
            token.ThrowIfCancellationRequested();
            var length = (int)Math.Min(65536, input.Length - bytes.Length);
            var chunk = input.Kind == "durable"
                ? await durable.ReadAsync(owner.WorkspaceId, input.Reference!, bytes.Length, length, token)
                : Convert.FromBase64String((await artifacts.ReadAsync(StagedArtifactId.Parse(input.Reference!), bytes.Length, length, null, token)).ContentBase64);
            if (chunk.Length == 0 || chunk.Length > length) throw Error("artifact_processing_input_incomplete");
            await bytes.WriteAsync(chunk, token);
        }
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))), input.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Error("artifact_processing_input_changed");
        try { return Utf8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length); }
        catch (DecoderFallbackException) { throw Error("artifact_processing_utf8_required"); }
    }

    private async Task<StagedArtifactResource> StageAsync(FlowRun owner, ArtifactTransformFlowStepDefinition step,
        string identity, string name, string media, ReadOnlyMemory<byte> bytes, Input input, CancellationToken token)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
        var producerId = "artifact-processing:" + Hash(identity);
        var stored = await artifacts.GetStagedByProducerIdForWriteAsync(producerId, token)
            ?? await artifacts.CreateStagedAsync(new(name, media,
                new() { Kind = ArtifactProducerKind.FlowRun, Id = producerId, FlowRunId = owner.Id, FlowStepId = step.Name,
                    CorrelationId = owner.CorrelationId, Provenance = new Dictionary<string, string> { ["inputArtifactId"] = input.Id, ["inputSha256"] = input.Sha256 } }), token);
        if (stored.Value.ArtifactStatus == StagedArtifactStatus.Open)
        {
            if (stored.Value.Length > bytes.Length) throw Error("artifact_processing_write_replay_conflict");
            long verified = 0;
            // Open staged content is deliberately unreadable; partial writes are never adopted.
            if (stored.Value.Length != 0) throw Error("artifact_processing_partial_write");
            while (verified < bytes.Length)
            {
                var count = (int)Math.Min(65536, bytes.Length - verified);
                stored = await artifacts.WriteAsync(stored.Value.ArtifactId, verified, bytes.Slice((int)verified, count), token);
                verified += count;
            }
            stored = await artifacts.SealAsync(stored.Value.ArtifactId, token);
        }
        if (stored.Value.Sha256 != digest) throw Error("artifact_processing_write_replay_conflict");
        return stored.Value;
    }

    private async Task<StagedArtifactResource> OutputStageAsync(Session session, CancellationToken token) =>
        (await artifacts.GetStagedAsync(StagedArtifactId.Parse(session.OutputId), null, ArtifactLeaseOperation.Read, token))?.Value
        ?? throw Error("artifact_processing_output_unavailable");
    private async Task<string> OutputTextAsync(Session session, CancellationToken token)
    {
        var stage = await OutputStageAsync(session, token);
        using var bytes = new MemoryStream();
        while (bytes.Length < stage.Length)
        {
            var chunk = await artifacts.ReadAsync(stage.ArtifactId, bytes.Length, (int)Math.Min(65536, stage.Length - bytes.Length), null, token);
            var part = Convert.FromBase64String(chunk.ContentBase64);
            if (part.Length == 0) throw Error("artifact_processing_output_incomplete");
            await bytes.WriteAsync(part, token);
        }
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))), stage.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Error("artifact_processing_output_changed");
        try { return Utf8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length); }
        catch (DecoderFallbackException) { throw Error("artifact_processing_utf8_required"); }
    }
    private async Task<Session?> LoadAsync(FlowRun owner, string key, CancellationToken token) =>
        (await states.GetExactAsync(owner.ExecutionScopeRef, owner.Id, RuntimeType, key, token))?.Payload.Deserialize<Session>(Json);
    private Task SaveAsync(FlowRun owner, string key, Session session, CancellationToken token) =>
        states.StoreAsync(new(owner.ExecutionScopeRef, owner.Id, RuntimeType, key, Element(session), clock.GetUtcNow()), token);
    private SemaphoreSlim Gate(string key) => gates[(int)(Convert.ToUInt32(key[..8], 16) % (uint)gates.Length)];
    private void EnsureLive(Session session) { if (session.ExpiresAt <= clock.GetUtcNow()) throw Error("artifact_processing_expired"); }
    private static string Key(FlowRun owner, string step, int attempt, int index) => Hash($"{owner.ExecutionScopeRef.Value}:{owner.Id}:{step}:{attempt}:{index}");
    private static string ProcessingRunId(FlowRun owner, ArtifactTransformFlowStepDefinition step, int attempt, int index) =>
        "flowrun-child-" + Hash($"{owner.Scope.ScopeRef.Value}:{owner.Id}:{step.Name}-artifact:{attempt}:{index}")[..32];
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    private static string Encode(Cursor value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, Json));
    private static string Text(JsonElement value, string name) => OptionalText(value, name) ?? throw Error("artifact_processing_arguments_invalid");
    private static string? OptionalText(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static int Integer(JsonElement value, string name, int fallback) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
        ? field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) ? number : throw Error("artifact_processing_arguments_invalid") : fallback;
    private static bool SplitsScalar(string text, int offset) => offset > 0 && offset < text.Length && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]);
    private static bool IsJson(string media) => media.Equals("application/json", StringComparison.OrdinalIgnoreCase) || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    private static bool IsText(string media) => media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || IsJson(media);
    private static void ValidateContent(string media, string content)
    {
        if (!IsText(media)) throw Error("artifact_processing_extraction_required");
        if (IsJson(media))
        {
            try { using var parsed = JsonDocument.Parse(content); }
            catch (JsonException) { throw Error("artifact_processing_json_invalid"); }
        }
    }
    private static RuntimeGovernedOperationException Error(string code) => new(code, "Artifact processing refused an invalid, unavailable or conflicting content operation.");
}
