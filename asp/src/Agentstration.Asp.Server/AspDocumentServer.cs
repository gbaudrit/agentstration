using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Asp.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Asp.Server;

public delegate ValueTask<AspDocumentContext> AspDocumentAuthorize(RequestContext<CallToolRequestParams> request, CancellationToken ct);
public sealed record AspDocumentBinding(AspStoreRef Store, AspDocumentPolicy Policy, IAspDocumentProvider Provider, AspDocumentAuthorize Authorize);

/// <summary>Separate operation group preserves the exact closed asp.content/1 contract.</summary>
public sealed class AspDocumentServer(
    Func<RequestContext<CallToolRequestParams>, AspStoreRef, AspDocumentSelection, CancellationToken, ValueTask<AspDocumentBinding>> resolve,
    TimeProvider clock, string prefix = "aspdoc")
{
    private readonly byte[] cursorKey = RandomNumberGenerator.GetBytes(32);
    private sealed record Cursor(string Authority, string Position, DateTimeOffset Expires);

    public ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ListToolsResult
        {
            Tools = AspDocumentSchemas.Operations.Select(op => new Tool
            {
                Name = prefix + "_" + op,
                Description = "Governed ASP document " + op + "; select an authorized logical collection.",
                InputSchema = AspDocumentSchemas.Input(op),
                OutputSchema = AspDocumentSchemas.Output(op)
            }).ToList()
        });
    }

    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var id = "unknown";
        try
        {
            var parameters = request.Params ?? throw new AspException("invalid_request", "Missing request.");
            var wire = JsonSerializer.SerializeToElement(parameters.Arguments);
            AspValidation.Require(wire.GetRawText().Length <= 100_000, "limit_exceeded", "Request exceeds its wire bound.");
            var call = AspValidation.Parse<AspDocumentInvocation<JsonElement>>(wire);
            AspValidation.Store(call.Store);
            AspDocumentValidation.Identity(call.Selection.Collection);
            if (call.Selection.Publication is { } publication) AspDocumentValidation.Identity(publication);
            id = call.Arguments.GetProperty("requestId").GetString()!;
            AspValidation.RequestId(id);
            AspDocumentValidation.ValidateJson(wire);
            AspValidation.Require(parameters.Name.StartsWith(prefix + "_", StringComparison.Ordinal), "unsupported_operation", "Unknown mapping.");
            var operation = parameters.Name[(prefix.Length + 1)..];
            AspValidation.Require(AspDocumentSchemas.Operations.Contains(operation, StringComparer.Ordinal), "unsupported_operation", "Unknown mapping.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var binding = await resolve(request, call.Store, call.Selection, timeout.Token);
            var grant = await binding.Authorize(request, timeout.Token);
            AspValidation.Require(binding.Store == call.Store && grant.Store == call.Store && grant.Selection == call.Selection,
                "forbidden", "The authorized target differs from the request.");
            var context = grant with { Policy = AspDocumentValidation.Intersect(binding.Policy, grant.Policy) };
            AspDocumentValidation.Demand(context, operation);
            var result = await ExecuteAsync(binding.Provider, context, operation, call.Arguments, timeout.Token);
            var reply = Reply(id, result);
            AspValidation.Require(JsonSerializer.SerializeToUtf8Bytes(reply.StructuredContent).Length <= context.Policy.MaximumResultBytes,
                "limit_exceeded", "Result exceeds its byte bound; request a smaller page.");
            return reply;
        }
        catch (AspException ex) { return Reply(id, error: new(ex.Code, ex.Message)); }
        catch (OverflowException) { return Reply(id, error: new("numeric_overflow", "The numeric result exceeds the supported decimal domain.")); }
        catch (OperationCanceledException) { return Reply(id, error: new("cancelled", "The operation was cancelled; mutations require exact original replay.")); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return Reply(id, error: new("invalid_request", "The document request or result is invalid.")); }
        catch (Exception)
        { return Reply(id, error: new("provider_unavailable", "The document provider could not complete the operation.")); }
    }

    private async ValueTask<object> ExecuteAsync(IAspDocumentProvider provider, AspDocumentContext context, string operation, JsonElement args, CancellationToken ct)
    {
        switch (operation)
        {
            case "describe":
                _ = AspValidation.Parse<AspDescribeRequest>(args);
                return new AspDocumentDescription(AspProtocol.Version, AspDocuments.Profile, context.Store, context.Selection,
                    provider.ProviderId, provider.ProviderVersion, context.Policy,
                    provider.QueryProfiles.Where(p => context.Policy.QueryProfiles!.Contains(p.Reference.Id, StringComparer.Ordinal)).ToArray(),
                    provider.DocumentSchema, AspDocumentValidation.Digest(provider.DocumentSchema), provider is IAspDocumentPublisher,
                    provider.ConfigurationSchema);
            case "get":
                var get = AspValidation.Parse<AspDocumentGet>(args); AspDocumentValidation.Identity(get.Id);
                return Check(await provider.GetAsync(context, get, ct), context, get.Id, get.Revision);
            case "put":
                var put = AspValidation.Parse<AspDocumentPut>(args); AspDocumentValidation.Identity(put.Id);
                AspValidation.Deadline(put.NotAfter, clock); AspDocumentValidation.Document(put.Value, context.Policy.MaximumDocumentBytes);
                return Check(await provider.PutAsync(context, put, ct), context, put.Id);
            case "delete":
                var delete = AspValidation.Parse<AspDocumentDelete>(args); AspDocumentValidation.Identity(delete.Id);
                AspDocumentValidation.Identity(delete.ExpectedRevision); AspValidation.Deadline(delete.NotAfter, clock);
                var deleted = await provider.DeleteAsync(context, delete, ct);
                AspValidation.Require(deleted.Id == delete.Id && deleted.Revision == delete.ExpectedRevision, "invalid_result", "Invalid deletion receipt.");
                return deleted;
            case "query":
                var query = AspValidation.Parse<AspDocumentQuery>(args);
                AspValidation.Require(context.Policy.QueryProfiles!.Contains(query.Profile.Id, StringComparer.Ordinal)
                    && provider.QueryProfiles.Any(p => p.Reference == query.Profile), "unsupported_capability", "This exact query profile/model is not available.");
                AspValidation.Require(query.PageSize > 0 && query.PageSize <= context.Policy.MaximumPageSize,
                    "limit_exceeded", "The page size exceeds its effective bound.");
                var authority = AspDocumentValidation.Digest(JsonSerializer.SerializeToElement(new { context, query.Profile, query.Query, query.PageSize }, AspProtocol.JsonOptions));
                var position = query.Cursor is null ? null : Open(query.Cursor, authority);
                var page = await provider.QueryAsync(context, query, position, ct);
                AspValidation.Require(page.Documents.Count + page.Groups.Count <= query.PageSize
                    && (page.Position is null || page.Documents.Count + page.Groups.Count > 0), "invalid_result", "Invalid query page.");
                foreach (var document in page.Documents) Check(document, context);
                return new AspDocumentPage(page.Documents, page.Groups, page.Position is null ? null : Seal(authority, page.Position));
            case "prepare":
                var prepare = AspValidation.Parse<AspDocumentPrepare>(args);
                AspDocumentValidation.Identity(prepare.PublicationId); AspValidation.Deadline(prepare.NotAfter, clock);
                AspValidation.Require(prepare.Documents.Count is > 0 and <= 100, "limit_exceeded", "Publication document count exceeds its bound.");
                foreach (var document in prepare.Documents)
                { AspDocumentValidation.Identity(document.Id); AspDocumentValidation.Document(document.Value, context.Policy.MaximumDocumentBytes); }
                return await Publisher(provider).PrepareAsync(context, prepare, ct);
            default:
                var publication = AspValidation.Parse<AspDocumentPublicationRequest>(args);
                AspDocumentValidation.Identity(publication.PublicationId);
                AspDocumentValidation.Publication(context, publication.PublicationId);
                if (operation == "seal") return await Publisher(provider).SealAsync(context, publication, ct);
                if (operation == "inspect") return await Publisher(provider).InspectAsync(context, publication, ct);
                await Publisher(provider).RetireAsync(context, publication, ct);
                return new AspDocumentPublication(publication.PublicationId, publication.Digest, 0, AspDocumentValidation.Digest(provider.DocumentSchema));
        }
    }

    private static IAspDocumentPublisher Publisher(IAspDocumentProvider provider) => provider as IAspDocumentPublisher
        ?? throw new AspException("unsupported_capability", "This provider does not implement immutable publications.");
    private static AspDocument Check(AspDocument value, AspDocumentContext context, string? id = null, string? revision = null)
    {
        AspDocumentValidation.Identity(value.Id); AspDocumentValidation.Identity(value.Revision);
        AspDocumentValidation.Document(value.Value, context.Policy.MaximumDocumentBytes);
        AspValidation.Require((id is null || value.Id == id) && (revision is null || value.Revision == revision), "invalid_result", "The provider changed document identity.");
        return value;
    }
    private string Seal(string authority, string position)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Cursor(authority, position, clock.GetUtcNow().AddMinutes(5)), AspProtocol.JsonOptions);
        var result = Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(HMACSHA256.HashData(cursorKey, bytes));
        AspValidation.Require(result.Length <= 4096, "limit_exceeded", "The continuation exceeds its bound.");
        return result;
    }
    private string Open(string token, string authority)
    {
        try
        {
            AspValidation.Require(token.Length <= 4096, "invalid_reference", "Invalid continuation.");
            var parts = token.Split('.');
            AspValidation.Require(parts.Length == 2, "invalid_reference", "Invalid continuation.");
            var bytes = Convert.FromBase64String(parts[0]);
            AspValidation.Require(CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(parts[1]), HMACSHA256.HashData(cursorKey, bytes)),
                "invalid_reference", "Invalid continuation.");
            var cursor = JsonSerializer.Deserialize<Cursor>(bytes, AspProtocol.JsonOptions)!;
            AspValidation.Require(cursor.Authority == authority && cursor.Expires > clock.GetUtcNow(), "invalid_reference", "Continuation context or lifetime differs.");
            return cursor.Position;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NullReferenceException)
        { throw new AspException("invalid_reference", "Invalid continuation."); }
    }
    private static CallToolResult Reply(string requestId, object? data = null, AspError? error = null)
    {
        var json = JsonSerializer.SerializeToElement(new AspReply<object>(AspProtocol.Version, requestId, data, error), AspProtocol.JsonOptions);
        return new() { IsError = error is not null, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
