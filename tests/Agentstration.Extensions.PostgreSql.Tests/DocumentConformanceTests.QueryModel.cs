using System.Text.Json;
using Agentstration.Asc.Abstractions;
using Agentstration.Asc.OData;

namespace Agentstration.Extensions.PostgreSql.Tests;

public sealed partial class DocumentConformanceTests
{
    private static JsonElement AlertRecordModel => Json("""
        {"type":"object","properties":{"id":{"type":"string"},"titre":{"type":"string"},
        "debut":{"type":"string","format":"date-time"},"fin":{"type":"string","format":"date-time"},
        "status":{"type":"string"},"lieu_nom_complet":{"type":"string"},"lieu_ville":{"type":"string"},
        "importance":{"type":"string"}},"required":["id"],"additionalProperties":false}
        """);
    private static JsonElement AlertEnvelopeModel => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { total_count = new { type = "integer" }, results = new { type = "array", items = AlertRecordModel } },
        required = new[] { "total_count", "results" },
        additionalProperties = false
    });
    private const string AlertQuery = """
        {"$filter":"status eq 'open'","$orderby":"debut desc",
        "$select":"id, titre, debut, fin, lieu_nom_complet, lieu_ville, importance"}
        """;

    [TestMethod]
    public void OrderingRequiresADeclaredScalarAtTheActualQueryModelLevel()
    {
        var envelope = new ODataDocumentProfile(AlertEnvelopeModel);
        var failure = Assert.Throws<AscException>(() => envelope.Parse(Json(AlertQuery)));
        Assert.AreEqual("unsupported_query", failure.Code);
        StringAssert.Contains(failure.Message, "Sorting requires a declared scalar field");
        var records = new ODataDocumentProfile(AlertRecordModel).Parse(Json(AlertQuery));
        Assert.HasCount(7, records.Select);
        Assert.IsTrue(records.Sort.Single().Descending);
    }

    [TestMethod]
    public async Task ArrayPredicatesSelectWholeEnvelopesWhileRecordQueriesFilterIndividualItems()
    {
        var record = Json("""
            {"id":"fixture-alert","titre":"Synthetic notification","debut":"2026-11-10T08:00:00+00:00",
            "fin":"2026-11-17T18:00:00+00:00","status":"EN COURS","lieu_nom_complet":"Fixture place",
            "lieu_ville":"Fixture city","importance":"haute"}
            """);
        await using (var envelopes = await Host.StartAsync(false, schema: AlertEnvelopeModel))
        {
            var value = JsonSerializer.Serialize(new { total_count = 1, results = new[] { record } });
            await envelopes.Client.PutAsync(envelopes.Put("envelope", value));
            await Error("unsupported_query", () => envelopes.Client.QueryAsync(envelopes.Query(AlertQuery)));
            var selected = await envelopes.Client.QueryAsync(envelopes.Query("""{"$filter":"results/any(a: a/status eq 'EN COURS')"}"""));
            Assert.HasCount(1, selected.Documents);
            Assert.AreEqual(1, selected.Documents.Single().Value.GetProperty("results").GetArrayLength());
        }
        await using var records = await Host.StartAsync(false, schema: AlertRecordModel);
        await records.Client.PutAsync(records.Put("record", record.GetRawText()));
        Assert.IsEmpty((await records.Client.QueryAsync(records.Query(AlertQuery))).Documents);
        var correctStatus = await records.Client.QueryAsync(records.Query(AlertQuery.Replace("'open'", "'EN COURS'", StringComparison.Ordinal)));
        Assert.AreEqual("fixture-alert", correctStatus.Documents.Single().Value.GetProperty("id").GetString());
        Assert.IsFalse(correctStatus.Documents.Single().Value.TryGetProperty("results", out _));
    }
}
