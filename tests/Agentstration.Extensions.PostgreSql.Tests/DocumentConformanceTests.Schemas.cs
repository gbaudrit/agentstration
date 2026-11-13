using Agentstration.Asp.Abstractions;

namespace Agentstration.Extensions.PostgreSql.Tests;

public sealed partial class DocumentConformanceTests
{
    private static readonly string Alerts = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nantes-alerts-response.json"));

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalDefinitionsNullableObjectsAndAssertedFormatsRoundTripAndQueryThroughMcp(bool postgres)
    {
        var schema = Json(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nantes-alerts.schema.json")));
        await using var host = await Host.StartAsync(postgres, schema: schema);
        await host.Client.PutAsync(host.Put("alerts", Alerts));
        var page = await host.Client.QueryAsync(host.Query("""{"$filter":"results/any(a:a/status eq 'active')"}"""));
        Assert.AreEqual("alerts", page.Documents.Single().Id);
        Assert.AreEqual(System.Text.Json.JsonValueKind.Null, page.Documents.Single().Value.GetProperty("results")[0].GetProperty("geolocalisation").ValueKind);
        await host.Client.PutAsync(host.Put("located", Alerts.Replace("\"geolocalisation\":null", "\"geolocalisation\":{\"lon\":-1.55,\"lat\":47.21}", StringComparison.Ordinal)));
        await Error("schema_validation", () => host.Client.PutAsync(host.Put("bad-date", Alerts.Replace("2026-11-13T10:00:00Z", "2026-99-13", StringComparison.Ordinal))));
        await Error("schema_validation", () => host.Client.PutAsync(host.Put("bad-uri", Alerts.Replace("\"lieu_site_web\":null", "\"lieu_site_web\":\"not a URI\"", StringComparison.Ordinal))));
        await Error("schema_validation", () => host.Client.PutAsync(host.Put("bad-required", Alerts.Replace("\"id\":\"fixture\",", "", StringComparison.Ordinal))));
    }

    [TestMethod]
    [DataRow("{\"type\":\"object\",\"properties\":{\"x\":{\"$ref\":\"https://example.invalid/schema\"}}}", "unsupported_schema", "$ref")]
    [DataRow("{\"type\":\"object\",\"properties\":{\"x\":{\"$ref\":\"#/$defs/missing\"}}}", "invalid_schema", "$ref")]
    [DataRow("{\"type\":\"object\",\"$defs\":{\"loop\":{\"$ref\":\"#/$defs/loop\"}}}", "unsupported_schema", "$ref")]
    [DataRow("{\"type\":\"object\",\"properties\":{\"x\":{\"format\":\"unknown\"}}}", "unsupported_schema", "format")]
    public void UnsupportedSchemaConstructsCarryAnExactDiagnosticWithoutResolvingRemoteContent(string schema, string code, string keyword)
    {
        var error = Assert.Throws<AspException>(() => DocumentModel.ValidateSchema(Json(schema)));
        Assert.AreEqual(code, error.Code);
        Assert.AreEqual(keyword, error.SchemaKeyword);
        Assert.IsNotNull(error.SchemaPath);
        Assert.IsTrue(error.SchemaPath.Contains(keyword, StringComparison.Ordinal));
    }
}
