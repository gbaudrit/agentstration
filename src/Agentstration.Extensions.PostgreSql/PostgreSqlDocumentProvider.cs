using System.Data;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.OData;
using Npgsql;
using NpgsqlTypes;

namespace Agentstration.Extensions.PostgreSql;

/// <summary>Fixed extension-owned layout; every statement is partitioned independently of the query.</summary>
public sealed class PostgreSqlDocumentProvider(string connectionString, JsonElement schema, TimeProvider? timeProvider = null) : IAspDocumentProvider, IAspDocumentPublisher
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ODataDocumentProfile profile = new(schema);
    public string ProviderId => "postgresql-jsonb";
    public string ProviderVersion => "1.0.0";
    public JsonElement ConfigurationSchema => PostgreSqlStorageTypes.ConfigurationSchema;
    public JsonElement DocumentSchema => schema;
    public IReadOnlyList<AspQueryProfileDescriptor> QueryProfiles => [profile.Descriptor];
    public override string ToString() => "[REDACTED]";

    /// <summary>Explicit operator provisioning only; never called by content/query arguments.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            CREATE SCHEMA IF NOT EXISTS asp_jsonb;
            CREATE TABLE IF NOT EXISTS asp_jsonb.documents (
              store_id text NOT NULL, binding_id text NOT NULL, collection_id text NOT NULL,
              publication_id text NOT NULL DEFAULT '', id text NOT NULL, revision text NOT NULL, body jsonb NOT NULL,
              PRIMARY KEY(store_id,binding_id,collection_id,publication_id,id));
            CREATE INDEX IF NOT EXISTS documents_body_gin ON asp_jsonb.documents USING gin(body jsonb_path_ops);
            CREATE TABLE IF NOT EXISTS asp_jsonb.publications (
              store_id text NOT NULL, binding_id text NOT NULL, collection_id text NOT NULL, publication_id text NOT NULL,
              digest text NOT NULL, count integer NOT NULL, model_digest text NOT NULL, sealed boolean NOT NULL DEFAULT false,
              PRIMARY KEY(store_id,binding_id,collection_id,publication_id));
            CREATE TABLE IF NOT EXISTS asp_jsonb.receipts (
              authority text NOT NULL, request_id text NOT NULL, request_digest text NOT NULL, result jsonb NOT NULL,
              expires_at timestamptz NOT NULL, PRIMARY KEY(authority,request_id));
            CREATE INDEX IF NOT EXISTS receipts_expiry ON asp_jsonb.receipts(expires_at);
            """, connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask<AspDocument> GetAsync(AspDocumentContext context, AspDocumentGet request, CancellationToken ct)
    {
        AspDocumentValidation.Demand(context, "get");
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await EnsurePublishedAsync(connection, transaction, context, ct);
        await using var command = Command(connection, transaction, context,
            "SELECT d.id,d.revision,d.body::text FROM asp_jsonb.documents d WHERE " + Partition + " AND d.id=@id");
        command.Parameters.AddWithValue("id", request.Id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        AspValidation.Require(await reader.ReadAsync(ct), "not_found", "The document is unavailable.");
        var result = ReadDocument(reader);
        AspValidation.Require(request.Revision is null || result.Revision == request.Revision,
            "version_unavailable", "The requested document revision is unavailable.");
        return result;
    }

    public ValueTask<AspDocument> PutAsync(AspDocumentContext context, AspDocumentPut request, CancellationToken ct) =>
        MutateAsync(context, "put", request.RequestId, request.NotAfter, request, async (connection, transaction) =>
        {
            AspDocumentValidation.Identity(request.Id); AspDocumentValidation.Document(request.Value, context.Policy.MaximumDocumentBytes);
            DocumentModel.Validate(schema, request.Value);
            await using var count = Command(connection, transaction, context, "SELECT count(*) FROM asp_jsonb.documents d WHERE " + Partition);
            var total = (long)(await count.ExecuteScalarAsync(ct))!;
            AspValidation.Require(request.ExpectedRevision is not null || total < 10000, "capacity_exceeded", "The collection document limit was reached.");
            var revision = Guid.NewGuid().ToString("N");
            var sql = request.ExpectedRevision is null
                ? "INSERT INTO asp_jsonb.documents(store_id,binding_id,collection_id,publication_id,id,revision,body) VALUES(@store,@binding,@collection,@publication,@id,@revision,@body) ON CONFLICT DO NOTHING"
                : "UPDATE asp_jsonb.documents d SET revision=@revision,body=@body WHERE " + Partition + " AND d.id=@id AND d.revision=@expected";
            await using var command = Command(connection, transaction, context, sql);
            command.Parameters.AddWithValue("id", request.Id); command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("body", NpgsqlDbType.Jsonb, request.Value.GetRawText());
            if (request.ExpectedRevision is not null) command.Parameters.AddWithValue("expected", request.ExpectedRevision);
            AspValidation.Require(await command.ExecuteNonQueryAsync(ct) == 1, "conflict", "The document already exists or its revision changed.");
            return new AspDocument(request.Id, revision, request.Value.Clone());
        }, ct);

    public ValueTask<AspDocumentDeleted> DeleteAsync(AspDocumentContext context, AspDocumentDelete request, CancellationToken ct) =>
        MutateAsync(context, "delete", request.RequestId, request.NotAfter, request, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, context,
                "DELETE FROM asp_jsonb.documents d WHERE " + Partition + " AND d.id=@id AND d.revision=@revision");
            command.Parameters.AddWithValue("id", request.Id); command.Parameters.AddWithValue("revision", request.ExpectedRevision);
            AspValidation.Require(await command.ExecuteNonQueryAsync(ct) == 1, "conflict", "The document is unavailable or its revision changed.");
            return new AspDocumentDeleted(request.Id, request.ExpectedRevision);
        }, ct);

    public async ValueTask<AspDocumentProviderPage> QueryAsync(AspDocumentContext context, AspDocumentQuery request, string? position, CancellationToken ct)
    {
        AspDocumentValidation.Demand(context, "query");
        AspValidation.Require(request.Profile == profile.Descriptor.Reference && context.Policy.QueryProfiles!.Contains(request.Profile.Id, StringComparer.Ordinal),
            "unsupported_capability", "The exact query profile is unavailable.");
        AspValidation.Require(request.PageSize > 0 && request.PageSize <= context.Policy.MaximumPageSize, "limit_exceeded", "Page size exceeds its bound.");
        var query = profile.Parse(request.Query);
        var builder = new ODataJsonbSql();
        var filter = query.Filter is null ? "TRUE" : builder.Expression(query.Filter);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await EnsurePublishedAsync(connection, transaction, context, ct);
        if (query.IsAggregate) return await AggregateAsync(connection, transaction, context, request, position, query, builder, filter, ct);
        var after = position is null ? "TRUE" : builder.After(query,
            JsonSerializer.Deserialize<ODataDocumentPosition>(position, AspProtocol.JsonOptions) ?? throw new AspException("invalid_reference", "Invalid query position."));
        var order = builder.Order(query);
        await using var command = Command(connection, transaction, context,
            "SELECT d.id,d.revision,d.body::text FROM asp_jsonb.documents d WHERE " + Partition
            + " AND (" + filter + ") AND (" + after + ") ORDER BY " + order + " LIMIT @limit");
        command.Parameters.AddRange(builder.Parameters.ToArray()); command.Parameters.AddWithValue("limit", request.PageSize + 1);
        var documents = new List<AspDocument>(); long bytes = 0;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            AspValidation.Require((bytes += reader.GetString(2).Length * 4L) <= context.Policy.MaximumResultBytes * 4L,
                "limit_exceeded", "The query result exceeds its memory bound; reduce the page.");
            documents.Add(ReadDocument(reader));
        }
        string? next = null;
        if (documents.Count > request.PageSize)
        {
            documents.RemoveAt(documents.Count - 1); var last = documents[^1];
            next = JsonSerializer.Serialize(new ODataDocumentPosition(last.Id,
                query.Sort.Select(s => JsonSerializer.SerializeToElement(ODataDocumentQuery.Evaluate(s.Expression, last.Value))).ToArray()), AspProtocol.JsonOptions);
        }
        return new(documents.Select(query.Project).ToArray(), [], next);
    }

    private static async Task<AspDocumentProviderPage> AggregateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, AspDocumentContext context,
        AspDocumentQuery request, string? position, ODataDocumentQuery query, ODataJsonbSql builder, string filter, CancellationToken ct)
    {
        AspValidation.Require(position is null, "invalid_reference", "Bounded aggregate results have no continuation.");
        var fields = query.GroupBy.Select(g => builder.Expression(g.Expression)).ToList();
        var groups = fields.ToArray();
        foreach (var aggregate in query.Aggregates)
        {
            var function = aggregate.Method switch { "VirtualPropertyCount" => "count(*)", "Sum" => "sum", "Min" => "min", "Max" => "max", _ => throw new AspException("unsupported_query", "Unsupported aggregate.") };
            fields.Add(function == "count(*)" ? function : function + "(" + builder.Expression(aggregate.Expression) + ")");
        }
        var grouping = groups.Length == 0 ? "" : " GROUP BY " + string.Join(',', groups) + " ORDER BY " + string.Join(',', groups.Select(g => g + " ASC NULLS FIRST"));
        await using var command = Command(connection, transaction, context,
            "SELECT " + string.Join(',', fields) + " FROM asp_jsonb.documents d WHERE " + Partition + " AND (" + filter + ")" + grouping + " LIMIT @limit");
        command.Parameters.AddRange(builder.Parameters.ToArray()); command.Parameters.AddWithValue("limit", request.PageSize + 1);
        var results = new List<JsonElement>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            AspValidation.Require(results.Count < request.PageSize, "limit_exceeded", "Too many aggregate groups; narrow the filter.");
            var row = new JsonObject(); var index = 0;
            foreach (var group in query.GroupBy)
            { ODataDocumentQuery.Set(row, group.Path, reader.IsDBNull(index) ? null : JsonSerializer.SerializeToNode(reader.GetValue(index))); index++; }
            // Advance for nullable grouping fields too.
            index = query.GroupBy.Count;
            foreach (var aggregate in query.Aggregates)
            { row[aggregate.Alias] = reader.IsDBNull(index) ? null : JsonSerializer.SerializeToNode(reader.GetValue(index)); index++; }
            results.Add(JsonSerializer.SerializeToElement(row));
        }
        return new([], results);
    }

    public ValueTask<AspDocumentPublication> PrepareAsync(AspDocumentContext context, AspDocumentPrepare request, CancellationToken ct) =>
        MutateAsync(context, "prepare", request.RequestId, request.NotAfter, request, async (connection, transaction) =>
        {
            AspDocumentValidation.Identity(request.PublicationId);
            AspValidation.Require(request.Documents.Count is > 0 and <= 100 && request.Documents.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() == request.Documents.Count,
                "limit_exceeded", "Invalid publication size or duplicate identities.");
            var digest = AspDocumentValidation.Digest(JsonSerializer.SerializeToElement(request.Documents.OrderBy(d => d.Id, StringComparer.Ordinal)));
            var selection = context with { Selection = context.Selection with { Publication = request.PublicationId } };
            await using var existing = Command(connection, transaction, selection,
                "SELECT digest,count,model_digest FROM asp_jsonb.publications d WHERE " + PublicationPartition);
            await using (var reader = await existing.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    AspValidation.Require(reader.GetString(0) == digest && reader.GetString(2) == profile.Descriptor.Reference.ModelDigest,
                        "conflict", "The publication identity already has different content/model evidence.");
                    return new AspDocumentPublication(request.PublicationId, digest, reader.GetInt32(1), reader.GetString(2));
                }
            }
            await using var capacity = Command(connection, transaction, context,
                "SELECT count(*) FROM asp_jsonb.publications d WHERE d.store_id=@store AND d.binding_id=@binding AND d.collection_id=@collection");
            AspValidation.Require((long)(await capacity.ExecuteScalarAsync(ct))! < 256, "capacity_exceeded", "Retire old publications before publishing more.");
            foreach (var item in request.Documents)
            {
                AspDocumentValidation.Identity(item.Id); AspDocumentValidation.Document(item.Value, context.Policy.MaximumDocumentBytes); DocumentModel.Validate(schema, item.Value);
                await using var insert = Command(connection, transaction, selection,
                    "INSERT INTO asp_jsonb.documents(store_id,binding_id,collection_id,publication_id,id,revision,body) VALUES(@store,@binding,@collection,@publication,@id,@revision,@body)");
                insert.Parameters.AddWithValue("id", item.Id); insert.Parameters.AddWithValue("revision", digest);
                insert.Parameters.AddWithValue("body", NpgsqlDbType.Jsonb, item.Value.GetRawText()); await insert.ExecuteNonQueryAsync(ct);
            }
            await using var publication = Command(connection, transaction, selection,
                "INSERT INTO asp_jsonb.publications(store_id,binding_id,collection_id,publication_id,digest,count,model_digest) VALUES(@store,@binding,@collection,@publication,@digest,@count,@model)");
            publication.Parameters.AddWithValue("digest", digest); publication.Parameters.AddWithValue("count", request.Documents.Count);
            publication.Parameters.AddWithValue("model", profile.Descriptor.Reference.ModelDigest); await publication.ExecuteNonQueryAsync(ct);
            return new AspDocumentPublication(request.PublicationId, digest, request.Documents.Count, profile.Descriptor.Reference.ModelDigest);
        }, ct);

    public async ValueTask<AspDocumentPublication> SealAsync(AspDocumentContext context, AspDocumentPublicationRequest request, CancellationToken ct)
    {
        AspDocumentValidation.Demand(context, "seal"); AspDocumentValidation.Publication(context, request.PublicationId);
        var target = context with { Selection = context.Selection with { Publication = request.PublicationId } };
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, null, target,
            "UPDATE asp_jsonb.publications d SET sealed=true WHERE " + PublicationPartition + " AND digest=@digest RETURNING count,model_digest");
        command.Parameters.AddWithValue("digest", request.Digest);
        await using var reader = await command.ExecuteReaderAsync(ct);
        AspValidation.Require(await reader.ReadAsync(ct), "version_unavailable", "The exact publication is unavailable.");
        AspValidation.Require(reader.GetString(1) == profile.Descriptor.Reference.ModelDigest, "version_unavailable", "The publication model is unavailable.");
        return new(request.PublicationId, request.Digest, reader.GetInt32(0), reader.GetString(1));
    }
    public async ValueTask<AspDocumentPublication> InspectAsync(AspDocumentContext context, AspDocumentPublicationRequest request, CancellationToken ct)
    {
        AspDocumentValidation.Demand(context, "inspect"); AspDocumentValidation.Publication(context, request.PublicationId);
        var target = context with { Selection = context.Selection with { Publication = request.PublicationId } };
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, null, target,
            "SELECT count,model_digest FROM asp_jsonb.publications d WHERE " + PublicationPartition + " AND digest=@digest AND sealed");
        command.Parameters.AddWithValue("digest", request.Digest); await using var reader = await command.ExecuteReaderAsync(ct);
        AspValidation.Require(await reader.ReadAsync(ct), "version_unavailable", "The sealed publication is unavailable.");
        AspValidation.Require(reader.GetString(1) == profile.Descriptor.Reference.ModelDigest, "version_unavailable", "The publication model is unavailable.");
        return new(request.PublicationId, request.Digest, reader.GetInt32(0), reader.GetString(1));
    }
    public async ValueTask RetireAsync(AspDocumentContext context, AspDocumentPublicationRequest request, CancellationToken ct)
    {
        AspDocumentValidation.Demand(context, "retire"); AspDocumentValidation.Publication(context, request.PublicationId);
        var target = context with { Selection = context.Selection with { Publication = request.PublicationId } };
        await using var connection = await OpenAsync(ct); await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, context, ct);
        await using var existing = Command(connection, transaction, target, "SELECT digest FROM asp_jsonb.publications d WHERE " + PublicationPartition);
        var retained = await existing.ExecuteScalarAsync(ct);
        AspValidation.Require(retained is null || retained is string digest && digest == request.Digest, "conflict", "The exact publication digest differs.");
        await using var command = Command(connection, transaction, target,
            "DELETE FROM asp_jsonb.publications d WHERE " + PublicationPartition + " AND digest=@digest RETURNING digest");
        command.Parameters.AddWithValue("digest", request.Digest);
        if (await command.ExecuteScalarAsync(ct) is not null)
        {
            await using var documents = Command(connection, transaction, target, "DELETE FROM asp_jsonb.documents d WHERE " + Partition);
            await documents.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private async ValueTask<T> MutateAsync<T>(AspDocumentContext context, string operation, string requestId, DateTimeOffset notAfter,
        object request, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> mutate, CancellationToken ct)
    {
        AspDocumentValidation.Demand(context, operation); AspValidation.RequestId(requestId); AspValidation.Deadline(notAfter, clock);
        var authority = AspDocumentValidation.Digest(JsonSerializer.SerializeToElement(context, AspProtocol.JsonOptions));
        var digest = AspDocumentValidation.Digest(JsonSerializer.SerializeToElement(new { operation, request }, AspProtocol.JsonOptions));
        await using var connection = await OpenAsync(ct); await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockAsync(connection, transaction, context, ct);
        await using var find = new NpgsqlCommand("SELECT request_digest,result::text FROM asp_jsonb.receipts WHERE authority=@authority AND request_id=@id", connection, transaction);
        find.Parameters.AddWithValue("authority", authority); find.Parameters.AddWithValue("id", requestId);
        await using (var reader = await find.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                AspValidation.Require(reader.GetString(0) == digest, "idempotency_conflict", "The request identity already has different arguments.");
                return JsonSerializer.Deserialize<T>(reader.GetString(1), AspProtocol.JsonOptions)!;
            }
        }
        await using var capacity = new NpgsqlCommand("SELECT count(*) FROM asp_jsonb.receipts WHERE authority=@authority", connection, transaction);
        capacity.Parameters.AddWithValue("authority", authority);
        AspValidation.Require((long)(await capacity.ExecuteScalarAsync(ct))! < 1024, "capacity_exceeded", "The mutation receipt capacity was reached.");
        var result = await mutate(connection, transaction);
        await using var receipt = new NpgsqlCommand("INSERT INTO asp_jsonb.receipts(authority,request_id,request_digest,result,expires_at) VALUES(@authority,@id,@digest,@result,@expires)", connection, transaction);
        receipt.Parameters.AddWithValue("authority", authority); receipt.Parameters.AddWithValue("id", requestId); receipt.Parameters.AddWithValue("digest", digest);
        receipt.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(result, AspProtocol.JsonOptions));
        receipt.Parameters.AddWithValue("expires", notAfter); await receipt.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct); return result;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch (NpgsqlException exception)
        {
            await connection.DisposeAsync();
            var code = "backend_unavailable";
            for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
            {
                if (cause is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                { code = "backend_connection_refused"; break; }
                if (cause is TimeoutException or SocketException { SocketErrorCode: SocketError.TimedOut })
                    code = "backend_timeout";
            }
            throw new AspException(code, "The database connection could not be established.");
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, AspDocumentContext context, CancellationToken ct)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(context.Store.StoreId + ":" + context.Store.BindingId + ":" + context.Selection.Collection));
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key); DELETE FROM asp_jsonb.receipts WHERE (authority,request_id) IN (SELECT authority,request_id FROM asp_jsonb.receipts WHERE expires_at < now() ORDER BY expires_at LIMIT 256)", connection, transaction) { CommandTimeout = 10 };
        command.Parameters.AddWithValue("key", BitConverter.ToInt64(bytes)); await command.ExecuteNonQueryAsync(ct);
    }
    private const string Partition = "d.store_id=@store AND d.binding_id=@binding AND d.collection_id=@collection AND d.publication_id=@publication";
    private const string PublicationPartition = Partition;
    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, AspDocumentContext context, string sql)
    {
        var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 10 };
        command.Parameters.AddWithValue("store", context.Store.StoreId); command.Parameters.AddWithValue("binding", context.Store.BindingId);
        command.Parameters.AddWithValue("collection", context.Selection.Collection); command.Parameters.AddWithValue("publication", context.Selection.Publication ?? "");
        return command;
    }
    private async Task EnsurePublishedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, AspDocumentContext context, CancellationToken ct)
    {
        if (context.Selection.Publication is null) return;
        await using var command = Command(connection, transaction, context,
            "SELECT model_digest FROM asp_jsonb.publications d WHERE " + PublicationPartition + " AND sealed");
        AspValidation.Require(await command.ExecuteScalarAsync(ct) is string model && model == profile.Descriptor.Reference.ModelDigest, "version_unavailable", "The exact sealed publication is unavailable.");
    }
    private static AspDocument ReadDocument(NpgsqlDataReader reader)
    {
        using var json = JsonDocument.Parse(reader.GetString(2));
        return new(reader.GetString(0), reader.GetString(1), json.RootElement.Clone());
    }
}
