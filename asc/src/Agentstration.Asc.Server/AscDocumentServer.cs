using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Asc.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Asc.Server;

public delegate ValueTask<AscDocumentContext> AscDocumentAuthorize(RequestContext<CallToolRequestParams> request, CancellationToken ct);
public sealed record AscDocumentBinding(AscStoreRef Store, AscDocumentPolicy Policy, IAscDocumentProvider Provider, AscDocumentAuthorize Authorize);

/// <summary>Separate operation group preserves the exact closed asc.content/1 contract.</summary>
public sealed class AscDocumentServer(
    Func<RequestContext<CallToolRequestParams>, AscStoreRef, AscDocumentSelection, CancellationToken, ValueTask<AscDocumentBinding>> resolve,
    TimeProvider clock, string prefix = "ascdoc")
{
    private readonly byte[] cursorKey = RandomNumberGenerator.GetBytes(32);
    private sealed record Cursor(string Authority, string Position, DateTimeOffset Expires);

    public ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ListToolsResult
        {
            Tools = AscDocumentSchemas.Operations.Select(op => new Tool
            {
                Name = prefix + "_" + op,
                Description = "Governed ASC document " + op + "; select an authorized logical collection.",
                InputSchema = AscDocumentSchemas.Input(op),
                OutputSchema = AscDocumentSchemas.Output(op)
            }).ToList()
        });
    }

    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var id = "unknown";
        try
        {
            var parameters = request.Params ?? throw new AscException("invalid_request", "Missing request.");
            var wire = JsonSerializer.SerializeToElement(parameters.Arguments);
            AscValidation.Require(wire.GetRawText().Length <= 100_000, "limit_exceeded", "Request exceeds its wire bound.");
            var call = AscValidation.Parse<AscDocumentInvocation<JsonElement>>(wire);
            AscValidation.Store(call.Store);
            AscDocumentValidation.Identity(call.Selection.Collection);
            if (call.Selection.Publication is { } publication) AscDocumentValidation.Identity(publication);
            id = call.Arguments.GetProperty("requestId").GetString()!;
            AscValidation.RequestId(id);
            AscDocumentValidation.ValidateJson(wire);
            AscValidation.Require(parameters.Name.StartsWith(prefix + "_", StringComparison.Ordinal), "unsupported_operation", "Unknown mapping.");
            var operation = parameters.Name[(prefix.Length + 1)..];
            AscValidation.Require(AscDocumentSchemas.Operations.Contains(operation, StringComparer.Ordinal), "unsupported_operation", "Unknown mapping.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var binding = await resolve(request, call.Store, call.Selection, timeout.Token);
            var grant = await binding.Authorize(request, timeout.Token);
            AscValidation.Require(binding.Store == call.Store && grant.Store == call.Store && grant.Selection == call.Selection,
                "forbidden", "The authorized target differs from the request.");
            var context = grant with { Policy = AscDocumentValidation.Intersect(binding.Policy, grant.Policy) };
            AscDocumentValidation.Demand(context, operation);
            AscValidation.Require(context.Policy.DocumentIds is null || binding.Provider is IAscDocumentSubsetProvider,
                "unsupported_capability", "The provider does not implement governed document subsets.");
            var result = await ExecuteAsync(binding.Provider, context, operation, call.Arguments, timeout.Token);
            var reply = Reply(id, result);
            AscValidation.Require(JsonSerializer.SerializeToUtf8Bytes(reply.StructuredContent).Length <= context.Policy.MaximumResultBytes,
                "limit_exceeded", "Result exceeds its byte bound; request a smaller page.");
            return reply;
        }
        catch (AscException ex) { return Reply(id, error: new(ex.Code, ex.Message)); }
        catch (OverflowException) { return Reply(id, error: new("numeric_overflow", "The numeric result exceeds the supported decimal domain.")); }
        catch (OperationCanceledException) { return Reply(id, error: new("cancelled", "The operation was cancelled; mutations require exact original replay.")); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return Reply(id, error: new("invalid_request", "The document request or result is invalid.")); }
        catch (Exception)
        { return Reply(id, error: new("provider_unavailable", "The document provider could not complete the operation.")); }
    }

    private async ValueTask<object> ExecuteAsync(IAscDocumentProvider provider, AscDocumentContext context, string operation, JsonElement args, CancellationToken ct)
    {
        switch (operation)
        {
            case "describe":
                _ = AscValidation.Parse<AscDescribeRequest>(args);
                return new AscDocumentDescription(AscContract.Version, AscDocuments.Profile, context.Store, context.Selection,
                    provider.ProviderId, provider.ProviderVersion, context.Policy,
                    provider.QueryProfiles.Where(p => context.Policy.QueryProfiles!.Contains(p.Reference.Id, StringComparer.Ordinal)).ToArray(),
                    provider.DocumentSchema, AscDocumentValidation.Digest(provider.DocumentSchema), provider is IAscDocumentPublisher,
                    provider.ConfigurationSchema);
            case "get":
                var get = AscValidation.Parse<AscDocumentGet>(args);
                AscValidation.Require(context.Policy.DocumentIds is null || context.Policy.DocumentIds.Contains(get.Id, StringComparer.Ordinal), "forbidden", "Document is outside the authorized selection."); AscDocumentValidation.Identity(get.Id);
                return Check(await provider.GetAsync(context, get, ct), context, get.Id, get.Revision);
            case "put":
                var put = AscValidation.Parse<AscDocumentPut>(args); AscDocumentValidation.Identity(put.Id);
                AscValidation.Deadline(put.NotAfter, clock); AscDocumentValidation.Document(put.Value, context.Policy.MaximumDocumentBytes);
                return Check(await provider.PutAsync(context, put, ct), context, put.Id);
            case "delete":
                var delete = AscValidation.Parse<AscDocumentDelete>(args); AscDocumentValidation.Identity(delete.Id);
                AscDocumentValidation.Identity(delete.ExpectedRevision); AscValidation.Deadline(delete.NotAfter, clock);
                var deleted = await provider.DeleteAsync(context, delete, ct);
                AscValidation.Require(deleted.Id == delete.Id && deleted.Revision == delete.ExpectedRevision, "invalid_result", "Invalid deletion receipt.");
                return deleted;
            case "query":
                var query = AscValidation.Parse<AscDocumentQuery>(args);
                AscValidation.Require(context.Policy.QueryProfiles!.Contains(query.Profile.Id, StringComparer.Ordinal)
                    && provider.QueryProfiles.Any(p => p.Reference == query.Profile), "unsupported_capability", "This exact query profile/model is not available.");
                AscValidation.Require(query.PageSize > 0 && query.PageSize <= context.Policy.MaximumPageSize,
                    "limit_exceeded", "The page size exceeds its effective bound.");
                var authority = AscDocumentValidation.Digest(JsonSerializer.SerializeToElement(new { context, query.Profile, query.Query, query.PageSize }, AscContract.JsonOptions));
                var position = query.Cursor is null ? null : Open(query.Cursor, authority);
                var page = await provider.QueryAsync(context, query, position, ct);
                AscValidation.Require(page.Documents.Count + page.Groups.Count <= query.PageSize
                    && (page.Position is null || page.Documents.Count + page.Groups.Count > 0), "invalid_result", "Invalid query page.");
                foreach (var document in page.Documents) Check(document, context);
                return new AscDocumentPage(page.Documents, page.Groups, page.Position is null ? null : Seal(authority, page.Position));
            case "prepare":
                var prepare = AscValidation.Parse<AscDocumentPrepare>(args);
                AscDocumentValidation.Identity(prepare.PublicationId); AscValidation.Deadline(prepare.NotAfter, clock);
                AscValidation.Require(prepare.Documents.Count is > 0 and <= 100, "limit_exceeded", "Publication document count exceeds its bound.");
                foreach (var document in prepare.Documents)
                { AscDocumentValidation.Identity(document.Id); AscDocumentValidation.Document(document.Value, context.Policy.MaximumDocumentBytes); }
                return await Publisher(provider).PrepareAsync(context, prepare, ct);
            default:
                var publication = AscValidation.Parse<AscDocumentPublicationRequest>(args);
                AscDocumentValidation.Identity(publication.PublicationId);
                AscDocumentValidation.Publication(context, publication.PublicationId);
                if (operation == "seal") return await Publisher(provider).SealAsync(context, publication, ct);
                if (operation == "inspect") return await Publisher(provider).InspectAsync(context, publication, ct);
                await Publisher(provider).RetireAsync(context, publication, ct);
                return new AscDocumentPublication(publication.PublicationId, publication.Digest, 0, AscDocumentValidation.Digest(provider.DocumentSchema));
        }
    }

    private static IAscDocumentPublisher Publisher(IAscDocumentProvider provider) => provider as IAscDocumentPublisher
        ?? throw new AscException("unsupported_capability", "This provider does not implement immutable publications.");
    private static AscDocument Check(AscDocument value, AscDocumentContext context, string? id = null, string? revision = null)
    {
        AscDocumentValidation.Identity(value.Id); AscDocumentValidation.Identity(value.Revision);
        AscDocumentValidation.Document(value.Value, context.Policy.MaximumDocumentBytes);
        AscValidation.Require((id is null || value.Id == id) && (revision is null || value.Revision == revision), "invalid_result", "The provider changed document identity.");
        return value;
    }
    private string Seal(string authority, string position)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Cursor(authority, position, clock.GetUtcNow().AddMinutes(5)), AscContract.JsonOptions);
        var result = Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(HMACSHA256.HashData(cursorKey, bytes));
        AscValidation.Require(result.Length <= 4096, "limit_exceeded", "The continuation exceeds its bound.");
        return result;
    }
    private string Open(string token, string authority)
    {
        try
        {
            AscValidation.Require(token.Length <= 4096, "invalid_reference", "Invalid continuation.");
            var parts = token.Split('.');
            AscValidation.Require(parts.Length == 2, "invalid_reference", "Invalid continuation.");
            var bytes = Convert.FromBase64String(parts[0]);
            AscValidation.Require(CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(parts[1]), HMACSHA256.HashData(cursorKey, bytes)),
                "invalid_reference", "Invalid continuation.");
            var cursor = JsonSerializer.Deserialize<Cursor>(bytes, AscContract.JsonOptions)!;
            AscValidation.Require(cursor.Authority == authority && cursor.Expires > clock.GetUtcNow(), "invalid_reference", "Continuation context or lifetime differs.");
            return cursor.Position;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NullReferenceException)
        { throw new AscException("invalid_reference", "Invalid continuation."); }
    }
    private static CallToolResult Reply(string requestId, object? data = null, AscError? error = null)
    {
        var json = JsonSerializer.SerializeToElement(new AscReply<object>(AscContract.Version, requestId, data, error), AscContract.JsonOptions);
        return new() { IsError = error is not null, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
