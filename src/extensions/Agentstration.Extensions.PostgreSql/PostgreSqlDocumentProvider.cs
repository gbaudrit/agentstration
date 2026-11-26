using System.Data;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Asc.Abstractions;
using Agentstration.Asc.OData;
using Npgsql;
using NpgsqlTypes;

namespace Agentstration.Extensions.PostgreSql;

/// <summary>Fixed extension-owned layout; every statement is partitioned independently of the query.</summary>
public sealed class PostgreSqlDocumentProvider(string connectionString, JsonElement schema, TimeProvider? timeProvider = null) : IAscDocumentProvider, IAscDocumentPublisher, IAscDocumentSubsetProvider
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ODataDocumentProfile profile = new(schema);
    public string ProviderId => "postgresql-jsonb";
    public string ProviderVersion => "1.0.0";
    public JsonElement ConfigurationSchema => PostgreSqlStorageTypes.ConfigurationSchema;
    public JsonElement DocumentSchema => schema;
    public IReadOnlyList<AscQueryProfileDescriptor> QueryProfiles => [profile.Descriptor];
    public override string ToString() => "[REDACTED]";

    /// <summary>Explicit operator provisioning only; never called by content/query arguments.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            CREATE SCHEMA IF NOT EXISTS asc_jsonb;
            CREATE TABLE IF NOT EXISTS asc_jsonb.documents (
              store_id text NOT NULL, binding_id text NOT NULL, collection_id text NOT NULL,
              publication_id text NOT NULL DEFAULT '', id text NOT NULL, revision text NOT NULL, body jsonb NOT NULL,
              PRIMARY KEY(store_id,binding_id,collection_id,publication_id,id));
            CREATE INDEX IF NOT EXISTS documents_body_gin ON asc_jsonb.documents USING gin(body jsonb_path_ops);
            CREATE TABLE IF NOT EXISTS asc_jsonb.publications (
              store_id text NOT NULL, binding_id text NOT NULL, collection_id text NOT NULL, publication_id text NOT NULL,
              digest text NOT NULL, count integer NOT NULL, model_digest text NOT NULL, sealed boolean NOT NULL DEFAULT false,
              PRIMARY KEY(store_id,binding_id,collection_id,publication_id));
            CREATE TABLE IF NOT EXISTS asc_jsonb.receipts (
              authority text NOT NULL, request_id text NOT NULL, request_digest text NOT NULL, result jsonb NOT NULL,
              expires_at timestamptz NOT NULL, PRIMARY KEY(authority,request_id));
            CREATE INDEX IF NOT EXISTS receipts_expiry ON asc_jsonb.receipts(expires_at);
            """, connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask<AscDocument> GetAsync(AscDocumentContext context, AscDocumentGet request, CancellationToken ct)
    {
        AscDocumentValidation.Demand(context, "get");
        AscValidation.Require(context.Policy.DocumentIds is null || context.Policy.DocumentIds.Contains(request.Id, StringComparer.Ordinal), "forbidden", "Document is outside the authorized selection.");
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await EnsurePublishedAsync(connection, transaction, context, ct);
        await using var command = Command(connection, transaction, context,
            "SELECT d.id,d.revision,d.body::text FROM asc_jsonb.documents d WHERE " + Partition + " AND d.id=@id");
        command.Parameters.AddWithValue("id", request.Id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        AscValidation.Require(await reader.ReadAsync(ct), "not_found", "The document is unavailable.");
        var result = ReadDocument(reader);
        AscValidation.Require(request.Revision is null || result.Revision == request.Revision,
            "version_unavailable", "The requested document revision is unavailable.");
        return result;
    }

    public ValueTask<AscDocument> PutAsync(AscDocumentContext context, AscDocumentPut request, CancellationToken ct) =>
        MutateAsync(context, "put", request.RequestId, request.NotAfter, request, async (connection, transaction) =>
        {
            AscDocumentValidation.Identity(request.Id); AscDocumentValidation.Document(request.Value, context.Policy.MaximumDocumentBytes);
            DocumentModel.Validate(schema, request.Value);
            await using var count = Command(connection, transaction, context, "SELECT count(*) FROM asc_jsonb.documents d WHERE " + Partition);
            var total = (long)(await count.ExecuteScalarAsync(ct))!;
            AscValidation.Require(request.ExpectedRevision is not null || total < 10000, "capacity_exceeded", "The collection document limit was reached.");
            var revision = Guid.NewGuid().ToString("N");
            var sql = request.ExpectedRevision is null
                ? "INSERT INTO asc_jsonb.documents(store_id,binding_id,collection_id,publication_id,id,revision,body) VALUES(@store,@binding,@collection,@publication,@id,@revision,@body) ON CONFLICT DO NOTHING"
                : "UPDATE asc_jsonb.documents d SET revision=@revision,body=@body WHERE " + Partition + " AND d.id=@id AND d.revision=@expected";
            await using var command = Command(connection, transaction, context, sql);
            command.Parameters.AddWithValue("id", request.Id); command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("body", NpgsqlDbType.Jsonb, request.Value.GetRawText());
            if (request.ExpectedRevision is not null) command.Parameters.AddWithValue("expected", request.ExpectedRevision);
            AscValidation.Require(await command.ExecuteNonQueryAsync(ct) == 1, "conflict", "The document already exists or its revision changed.");
            return new AscDocument(request.Id, revision, request.Value.Clone());
        }, ct);

    public ValueTask<AscDocumentDeleted> DeleteAsync(AscDocumentContext context, AscDocumentDelete request, CancellationToken ct) =>
        MutateAsync(context, "delete", request.RequestId, request.NotAfter, request, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, context,
                "DELETE FROM asc_jsonb.documents d WHERE " + Partition + " AND d.id=@id AND d.revision=@revision");
            command.Parameters.AddWithValue("id", request.Id); command.Parameters.AddWithValue("revision", request.ExpectedRevision);
            AscValidation.Require(await command.ExecuteNonQueryAsync(ct) == 1, "conflict", "The document is unavailable or its revision changed.");
            return new AscDocumentDeleted(request.Id, request.ExpectedRevision);
        }, ct);

    public async ValueTask<AscDocumentProviderPage> QueryAsync(AscDocumentContext context, AscDocumentQuery request, string? position, CancellationToken ct)
    {
        AscDocumentValidation.Demand(context, "query");
        AscValidation.Require(request.Profile == profile.Descriptor.Reference && context.Policy.QueryProfiles!.Contains(request.Profile.Id, StringComparer.Ordinal),
            "unsupported_capability", "The exact query profile is unavailable.");
        AscValidation.Require(request.PageSize > 0 && request.PageSize <= context.Policy.MaximumPageSize, "limit_exceeded", "Page size exceeds its bound.");
        var query = profile.Parse(request.Query);
        var builder = new ODataJsonbSql();
        var filter = query.Filter is null ? "TRUE" : builder.Expression(query.Filter);
        if (context.Policy.DocumentIds is not null) filter = "d.id = ANY(@allowed_documents) AND (" + filter + ")";
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await EnsurePublishedAsync(connection, transaction, context, ct);
        if (query.IsAggregate) return await AggregateAsync(connection, transaction, context, request, position, query, builder, filter, ct);
        var after = position is null ? "TRUE" : builder.After(query,
            JsonSerializer.Deserialize<ODataDocumentPosition>(position, AscContract.JsonOptions) ?? throw new AscException("invalid_reference", "Invalid query position."));
        var order = builder.Order(query);
        await using var command = Command(connection, transaction, context,
            "SELECT d.id,d.revision,d.body::text FROM asc_jsonb.documents d WHERE " + Partition
            + " AND (" + filter + ") AND (" + after + ") ORDER BY " + order + " LIMIT @limit");
        command.Parameters.AddRange(builder.Parameters.ToArray()); command.Parameters.AddWithValue("limit", request.PageSize + 1);
        var documents = new List<AscDocument>(); long bytes = 0;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            AscValidation.Require((bytes += reader.GetString(2).Length * 4L) <= context.Policy.MaximumResultBytes * 4L,
                "limit_exceeded", "The query result exceeds its memory bound; reduce the page.");
            documents.Add(ReadDocument(reader));
        }
        string? next = null;
        if (documents.Count > request.PageSize)
        {
            documents.RemoveAt(documents.Count - 1); var last = documents[^1];
            next = JsonSerializer.Serialize(new ODataDocumentPosition(last.Id,
                query.Sort.Select(s => JsonSerializer.SerializeToElement(ODataDocumentQuery.Evaluate(s.Expression, last.Value))).ToArray()), AscContract.JsonOptions);
        }
        return new(documents.Select(query.Project).ToArray(), [], next);
    }

    private static async Task<AscDocumentProviderPage> AggregateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, AscDocumentContext context,
        AscDocumentQuery request, string? position, ODataDocumentQuery query, ODataJsonbSql builder, string filter, CancellationToken ct)
    {
        AscValidation.Require(position is null, "invalid_reference", "Bounded aggregate results have no continuation.");
        var fields = query.GroupBy.Select(g => builder.Expression(g.Expression)).ToList();
        var groups = fields.ToArray();
        foreach (var aggregate in query.Aggregates)
        {
            var function = aggregate.Method switch { "VirtualPropertyCount" => "count(*)", "Sum" => "sum", "Min" => "min", "Max" => "max", _ => throw new AscException("unsupported_query", "Unsupported aggregate.") };
            fields.Add(function == "count(*)" ? function : function + "(" + builder.Expression(aggregate.Expression) + ")");
        }
        var grouping = groups.Length == 0 ? "" : " GROUP BY " + string.Join(',', groups) + " ORDER BY " + string.Join(',', groups.Select(g => g + " ASC NULLS FIRST"));
        await using var command = Command(connection, transaction, context,
            "SELECT " + string.Join(',', fields) + " FROM asc_jsonb.documents d WHERE " + Partition + " AND (" + filter + ")" + grouping + " LIMIT @limit");
        command.Parameters.AddRange(builder.Parameters.ToArray()); command.Parameters.AddWithValue("limit", request.PageSize + 1);
        var results = new List<JsonElement>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            AscValidation.Require(results.Count < request.PageSize, "limit_exceeded", "Too many aggregate groups; narrow the filter.");
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

    public ValueTask<AscDocumentPublication> PrepareAsync(AscDocumentContext context, AscDocumentPrepare request, CancellationToken ct) =>
        MutateAsync(context, "prepare", request.RequestId, request.NotAfter, request, async (connection, transaction) =>
        {
            AscDocumentValidation.Identity(request.PublicationId);
            AscValidation.Require(request.Documents.Count is > 0 and <= 100 && request.Documents.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() == request.Documents.Count,
                "limit_exceeded", "Invalid publication size or duplicate identities.");
            var digest = AscDocumentValidation.Digest(JsonSerializer.SerializeToElement(request.Documents.OrderBy(d => d.Id, StringComparer.Ordinal)));
            var selection = context with { Selection = context.Selection with { Publication = request.PublicationId } };
            await using var existing = Command(connection, transaction, selection,
                "SELECT digest,count,model_digest FROM asc_jsonb.publications d WHERE " + PublicationPartition);
            await using (var reader = await existing.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    AscValidation.Require(reader.GetString(0) == digest && reader.GetString(2) == profile.Descriptor.Reference.ModelDigest,
                        "conflict", "The publication identity already has different content/model evidence.");
                    return new AscDocumentPublication(request.PublicationId, digest, reader.GetInt32(1), reader.GetString(2));
                }
            }
            await using var capacity = Command(connection, transaction, context,
                "SELECT count(*) FROM asc_jsonb.publications d WHERE d.store_id=@store AND d.binding_id=@binding AND d.collection_id=@collection");
            AscValidation.Require((long)(await capacity.ExecuteScalarAsync(ct))! < 256, "capacity_exceeded", "Retire old publications before publishing more.");
            foreach (var item in request.Documents)
            {
                AscDocumentValidation.Identity(item.Id); AscDocumentValidation.Document(item.Value, context.Policy.MaximumDocumentBytes); DocumentModel.Validate(schema, item.Value);
                await using var insert = Command(connection, transaction, selection,
                    "INSERT INTO asc_jsonb.documents(store_id,binding_id,collection_id,publication_id,id,revision,body) VALUES(@store,@binding,@collection,@publication,@id,@revision,@body)");
                insert.Parameters.AddWithValue("id", item.Id); insert.Parameters.AddWithValue("revision", digest);
                insert.Parameters.AddWithValue("body", NpgsqlDbType.Jsonb, item.Value.GetRawText()); await insert.ExecuteNonQueryAsync(ct);
            }
            await using var publication = Command(connection, transaction, selection,
                "INSERT INTO asc_jsonb.publications(store_id,binding_id,collection_id,publication_id,digest,count,model_digest) VALUES(@store,@binding,@collection,@publication,@digest,@count,@model)");
            publication.Parameters.AddWithValue("digest", digest); publication.Parameters.AddWithValue("count", request.Documents.Count);
            publication.Parameters.AddWithValue("model", profile.Descriptor.Reference.ModelDigest); await publication.ExecuteNonQueryAsync(ct);
            return new AscDocumentPublication(request.PublicationId, digest, request.Documents.Count, profile.Descriptor.Reference.ModelDigest);
        }, ct);

    public async ValueTask<AscDocumentPublication> SealAsync(AscDocumentContext context, AscDocumentPublicationRequest request, CancellationToken ct)
    {
        AscDocumentValidation.Demand(context, "seal"); AscDocumentValidation.Publication(context, request.PublicationId);
        var target = context with { Selection = context.Selection with { Publication = request.PublicationId } };
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, null, target,
            "UPDATE asc_jsonb.publications d SET sealed=true WHERE " + PublicationPartition + " AND digest=@digest RETURNING count,model_digest");
        command.Parameters.AddWithValue("digest", request.Digest);
        await using var reader = await command.ExecuteReaderAsync(ct);
        AscValidation.Require(await reader.ReadAsync(ct), "version_unavailable", "The exact publication is unavailable.");
        AscValidation.Require(reader.GetString(1) == profile.Descriptor.Reference.ModelDigest, "version_unavailable", "The publication model is unavailable.");
        return new(request.PublicationId, request.Digest, reader.GetInt32(0), reader.GetString(1));
    }
    public async ValueTask<AscDocumentPublication> InspectAsync(AscDocumentContext context, AscDocumentPublicationRequest request, CancellationToken ct)
    {
        AscDocumentValidation.Demand(context, "inspect"); AscDocumentValidation.Publication(context, request.PublicationId);
        var target = context with { Selection = context.Selection with { Publication = request.PublicationId } };
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, null, target,
            "SELECT count,model_digest FROM asc_jsonb.publications d WHERE " + PublicationPartition + " AND digest=@digest AND sealed");
        command.Parameters.AddWithValue("digest", request.Digest); await using var reader = await command.ExecuteReaderAsync(ct);
        AscValidation.Require(await reader.ReadAsync(ct), "version_unavailable", "The sealed publication is unavailable.");
        AscValidation.Require(reader.GetString(1) == profile.Descriptor.Reference.ModelDigest, "version_unavailable", "The publication model is unavailable.");
        return new(request.PublicationId, request.Digest, reader.GetInt32(0), reader.GetString(1));
    }
    public async ValueTask RetireAsync(AscDocumentContext context, AscDocumentPublicationRequest request, CancellationToken ct)
    {
        AscDocumentValidation.Demand(context, "retire"); AscDocumentValidation.Publication(context, request.PublicationId);
        var target = context with { Selection = context.Selection with { Publication = request.PublicationId } };
        await using var connection = await OpenAsync(ct); await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, context, ct);
        await using var existing = Command(connection, transaction, target, "SELECT digest FROM asc_jsonb.publications d WHERE " + PublicationPartition);
        var retained = await existing.ExecuteScalarAsync(ct);
        AscValidation.Require(retained is null || retained is string digest && digest == request.Digest, "conflict", "The exact publication digest differs.");
        await using var command = Command(connection, transaction, target,
            "DELETE FROM asc_jsonb.publications d WHERE " + PublicationPartition + " AND digest=@digest RETURNING digest");
        command.Parameters.AddWithValue("digest", request.Digest);
        if (await command.ExecuteScalarAsync(ct) is not null)
        {
            await using var documents = Command(connection, transaction, target, "DELETE FROM asc_jsonb.documents d WHERE " + Partition);
            await documents.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private async ValueTask<T> MutateAsync<T>(AscDocumentContext context, string operation, string requestId, DateTimeOffset notAfter,
        object request, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> mutate, CancellationToken ct)
    {
        AscDocumentValidation.Demand(context, operation); AscValidation.RequestId(requestId); AscValidation.Deadline(notAfter, clock);
        var authority = AscDocumentValidation.Digest(JsonSerializer.SerializeToElement(context, AscContract.JsonOptions));
        var digest = AscDocumentValidation.Digest(JsonSerializer.SerializeToElement(new { operation, request }, AscContract.JsonOptions));
        await using var connection = await OpenAsync(ct); await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockAsync(connection, transaction, context, ct);
        await using var find = new NpgsqlCommand("SELECT request_digest,result::text FROM asc_jsonb.receipts WHERE authority=@authority AND request_id=@id", connection, transaction);
        find.Parameters.AddWithValue("authority", authority); find.Parameters.AddWithValue("id", requestId);
        await using (var reader = await find.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                AscValidation.Require(reader.GetString(0) == digest, "idempotency_conflict", "The request identity already has different arguments.");
                return JsonSerializer.Deserialize<T>(reader.GetString(1), AscContract.JsonOptions)!;
            }
        }
        await using var capacity = new NpgsqlCommand("SELECT count(*) FROM asc_jsonb.receipts WHERE authority=@authority", connection, transaction);
        capacity.Parameters.AddWithValue("authority", authority);
        AscValidation.Require((long)(await capacity.ExecuteScalarAsync(ct))! < 1024, "capacity_exceeded", "The mutation receipt capacity was reached.");
        var result = await mutate(connection, transaction);
        await using var receipt = new NpgsqlCommand("INSERT INTO asc_jsonb.receipts(authority,request_id,request_digest,result,expires_at) VALUES(@authority,@id,@digest,@result,@expires)", connection, transaction);
        receipt.Parameters.AddWithValue("authority", authority); receipt.Parameters.AddWithValue("id", requestId); receipt.Parameters.AddWithValue("digest", digest);
        receipt.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(result, AscContract.JsonOptions));
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
            throw new AscException(code, "The database connection could not be established.");
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, AscDocumentContext context, CancellationToken ct)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(context.Store.StoreId + ":" + context.Store.BindingId + ":" + context.Selection.Collection));
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key); DELETE FROM asc_jsonb.receipts WHERE (authority,request_id) IN (SELECT authority,request_id FROM asc_jsonb.receipts WHERE expires_at < now() ORDER BY expires_at LIMIT 256)", connection, transaction) { CommandTimeout = 10 };
        command.Parameters.AddWithValue("key", BitConverter.ToInt64(bytes)); await command.ExecuteNonQueryAsync(ct);
    }
    private const string Partition = "d.store_id=@store AND d.binding_id=@binding AND d.collection_id=@collection AND d.publication_id=@publication";
    private const string PublicationPartition = Partition;
    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, AscDocumentContext context, string sql)
    {
        var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 10 };
        command.Parameters.AddWithValue("store", context.Store.StoreId); command.Parameters.AddWithValue("binding", context.Store.BindingId);
        command.Parameters.AddWithValue("collection", context.Selection.Collection); command.Parameters.AddWithValue("publication", context.Selection.Publication ?? "");
        if (context.Policy.DocumentIds is not null)
            command.Parameters.AddWithValue("allowed_documents", context.Policy.DocumentIds.ToArray());
        return command;
    }
    private async Task EnsurePublishedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, AscDocumentContext context, CancellationToken ct)
    {
        if (context.Selection.Publication is null) return;
        await using var command = Command(connection, transaction, context,
            "SELECT model_digest FROM asc_jsonb.publications d WHERE " + PublicationPartition + " AND sealed");
        AscValidation.Require(await command.ExecuteScalarAsync(ct) is string model && model == profile.Descriptor.Reference.ModelDigest, "version_unavailable", "The exact sealed publication is unavailable.");
    }
    private static AscDocument ReadDocument(NpgsqlDataReader reader)
    {
        using var json = JsonDocument.Parse(reader.GetString(2));
        return new(reader.GetString(0), reader.GetString(1), json.RootElement.Clone());
    }
}
