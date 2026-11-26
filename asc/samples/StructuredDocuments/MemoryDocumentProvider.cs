using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Asc.Abstractions;
using Agentstration.Asc.OData;

namespace Asc.Samples.StructuredDocuments;

/// <summary>Bounded conformance fixture, not a durable production store.</summary>
public sealed class MemoryDocumentProvider(JsonElement schema, TimeProvider clock) : IAscDocumentProvider, IAscDocumentPublisher, IAscDocumentSubsetProvider
{
    private readonly ODataDocumentProfile profile = new(schema);
    private readonly object gate = new();
    private readonly Dictionary<string, AscDocument> documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AscDocumentPublication Evidence, bool Sealed)> publications = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Digest, DateTimeOffset Expires, object Result)> receipts = new(StringComparer.Ordinal);
    public string ProviderId => "sample-documents";
    public string ProviderVersion => "1.0.0";
    public JsonElement ConfigurationSchema => JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false });
    public JsonElement DocumentSchema => schema;
    public IReadOnlyList<AscQueryProfileDescriptor> QueryProfiles => [profile.Descriptor];
    private static string Partition(AscDocumentContext context) => context.Store.StoreId + ":" + context.Store.BindingId + ":" + context.Selection.Collection + ":" + context.Selection.Publication + ":";
    private static string PublicationKey(AscDocumentContext context, string id) => context.Store.StoreId + ":" + context.Store.BindingId + ":" + context.Selection.Collection + ":" + id;
    public ValueTask<AscDocument> GetAsync(AscDocumentContext context, AscDocumentGet request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); AscDocumentValidation.Demand(context, "get");
        AscValidation.Require(context.Policy.DocumentIds is null || context.Policy.DocumentIds.Contains(request.Id, StringComparer.Ordinal), "forbidden", "Document is outside the authorized selection.");
        lock (gate)
        {
            Published(context);
            var value = documents.GetValueOrDefault(Partition(context) + request.Id) ?? throw new AscException("not_found", "The document is unavailable.");
            AscValidation.Require(request.Revision is null || request.Revision == value.Revision, "version_unavailable", "The exact document revision is unavailable.");
            return ValueTask.FromResult(value);
        }
    }
    public ValueTask<AscDocument> PutAsync(AscDocumentContext context, AscDocumentPut request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); AscDocumentValidation.Demand(context, "put");
        return ValueTask.FromResult(Mutate(context, "put", request.RequestId, request.NotAfter, request, () =>
        {
            AscDocumentValidation.Document(request.Value, context.Policy.MaximumDocumentBytes); DocumentModel.Validate(schema, request.Value);
            var key = Partition(context) + request.Id; var previous = documents.GetValueOrDefault(key);
            AscValidation.Require(request.ExpectedRevision is null ? previous is null : previous?.Revision == request.ExpectedRevision,
                "conflict", "Document creation/revision conflict.");
            AscValidation.Require(previous is not null || documents.Count < 128, "capacity_exceeded", "The sample document capacity was reached.");
            var result = new AscDocument(request.Id, Guid.NewGuid().ToString("N"), request.Value.Clone()); documents[key] = result; return result;
        }));
    }
    public ValueTask<AscDocumentDeleted> DeleteAsync(AscDocumentContext context, AscDocumentDelete request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); AscDocumentValidation.Demand(context, "delete");
        return ValueTask.FromResult(Mutate(context, "delete", request.RequestId, request.NotAfter, request, () =>
        {
            var key = Partition(context) + request.Id;
            AscValidation.Require(documents.TryGetValue(key, out var previous) && previous.Revision == request.ExpectedRevision, "conflict", "Document deletion/revision conflict.");
            documents.Remove(key); return new AscDocumentDeleted(request.Id, request.ExpectedRevision);
        }));
    }
    public ValueTask<AscDocumentProviderPage> QueryAsync(AscDocumentContext context, AscDocumentQuery request, string? position, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); AscDocumentValidation.Demand(context, "query");
        AscValidation.Require(request.Profile == profile.Descriptor.Reference, "unsupported_capability", "Unknown query profile.");
        var query = profile.Parse(request.Query);
        List<AscDocument> selected;
        lock (gate)
        {
            Published(context); var partition = Partition(context);
            selected = documents.Where(p => p.Key.StartsWith(partition, StringComparison.Ordinal)).Select(p => p.Value)
                .Where(d => context.Policy.DocumentIds is null || context.Policy.DocumentIds.Contains(d.Id, StringComparer.Ordinal))
                .Where(d => query.Filter is null || ODataDocumentQuery.Evaluate(query.Filter, d.Value) is true).ToList();
        }
        selected.Sort(query.CompareDocuments);
        if (query.IsAggregate)
        {
            AscValidation.Require(position is null, "invalid_reference", "Aggregate results have no continuation.");
            var groups = selected.GroupBy(d => GroupKey(query.GroupBy.Select(g => ODataDocumentQuery.Evaluate(g.Expression, d.Value)))).ToList();
            var results = new List<JsonElement>();
            if (selected.Count == 0 && query.GroupBy.Count == 0) results.Add(Aggregate(query, [], []));
            else foreach (var group in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                using var keys = JsonDocument.Parse(group.Key);
                results.Add(Aggregate(query, group.ToArray(), keys.RootElement.EnumerateArray().ToArray()));
            }
            AscValidation.Require(results.Count <= request.PageSize, "limit_exceeded", "Too many aggregate groups.");
            return ValueTask.FromResult(new AscDocumentProviderPage([], results));
        }
        if (position is not null)
        {
            var after = JsonSerializer.Deserialize<ODataDocumentPosition>(position, AscContract.JsonOptions)!;
            selected = selected.Where(document => After(query, document, after)).ToList();
        }
        var page = selected.Take(request.PageSize).ToArray();
        var next = selected.Count > page.Length ? JsonSerializer.Serialize(new ODataDocumentPosition(page[^1].Id,
            query.Sort.Select(s => JsonSerializer.SerializeToElement(ODataDocumentQuery.Evaluate(s.Expression, page[^1].Value))).ToArray()), AscContract.JsonOptions) : null;
        return ValueTask.FromResult(new AscDocumentProviderPage(page.Select(query.Project).ToArray(), [], next));
    }
    private static string GroupKey(IEnumerable<object?> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var value in values)
            {
                if (value is decimal number) writer.WriteRawValue(number.ToString("G29", System.Globalization.CultureInfo.InvariantCulture));
                else JsonSerializer.Serialize(writer, value);
            }
            writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
    private static JsonElement Aggregate(ODataDocumentQuery query, AscDocument[] documents, JsonElement[] keys)
    {
        var row = new JsonObject();
        for (var i = 0; i < query.GroupBy.Count; i++) ODataDocumentQuery.Set(row, query.GroupBy[i].Path, JsonNode.Parse(keys[i].GetRawText()));
        foreach (var aggregate in query.Aggregates)
        {
            if (aggregate.Method == "VirtualPropertyCount") { row[aggregate.Alias] = documents.Length; continue; }
            var values = documents.Select(d => ODataDocumentQuery.Evaluate(aggregate.Expression, d.Value)).Where(v => v is not null).ToArray();
            object? value = values.Length == 0 ? null : aggregate.Method switch
            {
                "Sum" => values.Cast<decimal>().Sum(),
                "Min" => values.Min(Comparer<object?>.Create(ODataDocumentQuery.Compare)),
                "Max" => values.Max(Comparer<object?>.Create(ODataDocumentQuery.Compare)),
                _ => throw new AscException("unsupported_query", "Unsupported aggregate.")
            };
            row[aggregate.Alias] = JsonSerializer.SerializeToNode(value);
        }
        return JsonSerializer.SerializeToElement(row);
    }
    private static bool After(ODataDocumentQuery query, AscDocument document, ODataDocumentPosition after)
    {
        AscValidation.Require(after.Values.Count == query.Sort.Count, "invalid_reference", "Invalid query position.");
        for (var i = 0; i < query.Sort.Count; i++)
        {
            var compared = ODataDocumentQuery.Compare(ODataDocumentQuery.Evaluate(query.Sort[i].Expression, document.Value), ODataDocumentQuery.Scalar(after.Values[i]));
            if (compared != 0) return query.Sort[i].Descending ? compared < 0 : compared > 0;
        }
        return string.CompareOrdinal(document.Id, after.Id) > 0;
    }
    public ValueTask<AscDocumentPublication> PrepareAsync(AscDocumentContext context, AscDocumentPrepare request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); AscDocumentValidation.Demand(context, "prepare");
        return ValueTask.FromResult(Mutate(context, "prepare", request.RequestId, request.NotAfter, request, () =>
        {
            var key = PublicationKey(context, request.PublicationId);
            var digest = AscDocumentValidation.Digest(JsonSerializer.SerializeToElement(request.Documents.OrderBy(d => d.Id, StringComparer.Ordinal)));
            if (publications.TryGetValue(key, out var previous))
            { AscValidation.Require(previous.Evidence.Digest == digest, "conflict", "Publication content changed."); return previous.Evidence; }
            AscValidation.Require(publications.Count < 128 && documents.Count + request.Documents.Count <= 128, "capacity_exceeded", "Sample publication capacity reached.");
            foreach (var document in request.Documents) { AscDocumentValidation.Document(document.Value, context.Policy.MaximumDocumentBytes); DocumentModel.Validate(schema, document.Value); }
            var target = context with { Selection = context.Selection with { Publication = request.PublicationId } };
            foreach (var document in request.Documents) documents.Add(Partition(target) + document.Id, new(document.Id, digest, document.Value.Clone()));
            var evidence = new AscDocumentPublication(request.PublicationId, digest, request.Documents.Count, AscDocumentValidation.Digest(schema));
            publications.Add(key, (evidence, false)); return evidence;
        }));
    }
    public ValueTask<AscDocumentPublication> SealAsync(AscDocumentContext context, AscDocumentPublicationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); AscDocumentValidation.Demand(context, "seal");
        lock (gate) { var item = Find(context, request); publications[PublicationKey(context, request.PublicationId)] = (item.Evidence, true); return ValueTask.FromResult(item.Evidence); }
    }
    public ValueTask<AscDocumentPublication> InspectAsync(AscDocumentContext context, AscDocumentPublicationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); AscDocumentValidation.Demand(context, "inspect");
        lock (gate) { var item = Find(context, request); AscValidation.Require(item.Sealed, "version_unavailable", "Publication is not sealed."); return ValueTask.FromResult(item.Evidence); }
    }
    public ValueTask RetireAsync(AscDocumentContext context, AscDocumentPublicationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); AscDocumentValidation.Demand(context, "retire");
        lock (gate)
        {
            if (!publications.ContainsKey(PublicationKey(context, request.PublicationId))) return ValueTask.CompletedTask;
            _ = Find(context, request); publications.Remove(PublicationKey(context, request.PublicationId));
            var partition = Partition(context with { Selection = context.Selection with { Publication = request.PublicationId } });
            foreach (var key in documents.Keys.Where(k => k.StartsWith(partition, StringComparison.Ordinal)).ToArray()) documents.Remove(key);
            return ValueTask.CompletedTask;
        }
    }
    private (AscDocumentPublication Evidence, bool Sealed) Find(AscDocumentContext context, AscDocumentPublicationRequest request)
    {
        AscValidation.Require(publications.TryGetValue(PublicationKey(context, request.PublicationId), out var result)
            && result.Evidence.Digest == request.Digest, "version_unavailable", "The exact publication is unavailable."); return result;
    }
    private void Published(AscDocumentContext context)
    {
        if (context.Selection.Publication is { } id)
            AscValidation.Require(publications.TryGetValue(PublicationKey(context, id), out var item) && item.Sealed, "version_unavailable", "The exact sealed publication is unavailable.");
    }
    private T Mutate<T>(AscDocumentContext context, string operation, string id, DateTimeOffset deadline, object request, Func<T> mutate)
    {
        AscValidation.Deadline(deadline, clock);
        lock (gate)
        {
            foreach (var expired in receipts.Where(r => r.Value.Expires <= clock.GetUtcNow()).Select(r => r.Key).ToArray()) receipts.Remove(expired);
            var key = AscDocumentValidation.Digest(JsonSerializer.SerializeToElement(context)) + ":" + id;
            var digest = AscDocumentValidation.Digest(JsonSerializer.SerializeToElement(new { operation, request }));
            if (receipts.TryGetValue(key, out var replay))
            { AscValidation.Require(replay.Digest == digest, "idempotency_conflict", "The request identity has different arguments."); return (T)replay.Result; }
            AscValidation.Require(receipts.Count < 128, "capacity_exceeded", "Sample receipt capacity reached.");
            var value = mutate(); receipts.Add(key, (digest, deadline, value!)); return value;
        }
    }
}
