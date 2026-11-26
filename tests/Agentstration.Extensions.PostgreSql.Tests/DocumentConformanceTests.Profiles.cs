using System.Text.Json;
using Agentstration.Asc.Abstractions;
using Agentstration.Extensions.PostgreSql;
using Asc.Samples.StructuredDocuments;

namespace Agentstration.Extensions.PostgreSql.Tests;

public sealed partial class DocumentConformanceTests
{
    [TestMethod]
    public async Task GenericSdkAndMcpCarryAnAlternateTextProfileWithoutODataFallback()
    {
        await using var host = await Host.StartAsync(false, new TextProfileProvider(), TextProfileProvider.Id);
        await host.Client.PutAsync(host.Put("independent", "{}"));
        var pin = host.Client.Description.QueryProfiles.Single().Reference;
        Assert.AreEqual("text+json", pin.Encoding);
        var result = await host.Client.QueryAsync(new("text-query", pin, Json("\"all\"")));
        Assert.AreEqual("independent", result.Documents.Single().Id);
        await Error("unsupported_query", () => host.Client.QueryAsync(new("no-guess", pin, Json("""{"$filter":"true"}"""))));
        await Error("unsupported_capability", () => host.Client.QueryAsync(new("no-fallback", pin with { Id = "odata/4.01" }, Json("{}"))));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConditionalConcurrentWritersHaveOneWinnerAndCancellationDoesNotWrite(bool postgres)
    {
        await using var host = await Host.StartAsync(postgres);
        var first = await host.Client.PutAsync(host.Put("race", "{}"));
        async Task<bool> Replace(string status)
        {
            try { await host.Client.PutAsync(host.Put("race", JsonSerializer.Serialize(new { status })) with { ExpectedRevision = first.Revision }); return true; }
            catch (AscException exception) when (exception.Code == "conflict") { return false; }
        }
        var winners = await Task.WhenAll(Replace("first"), Replace("second"));
        Assert.AreEqual(1, winners.Count(winner => winner));
        using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => host.Client.PutAsync(host.Put("cancelled", "{}"), cancelled.Token));
        await Error("not_found", () => host.Client.GetAsync(new("cancelled-read", "cancelled")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StandardNullComparisonsAndBoundedSchemaFailuresStayPortable(bool postgres)
    {
        await using var host = await Host.StartAsync(postgres);
        await host.Client.PutAsync(host.Put("missing", "{}"));
        await host.Client.PutAsync(host.Put("null", """{"optional":null}"""));
        foreach (var filter in new[] { "optional eq null", "optional ge null", "optional le null" })
            Assert.HasCount(2, (await host.Client.QueryAsync(host.Query(JsonSerializer.Serialize(new Dictionary<string, string> { ["$filter"] = filter })))).Documents);
        Assert.IsEmpty((await host.Client.QueryAsync(host.Query("""{"$filter":"optional gt null"}"""))).Documents);
        await Error("unsupported_query", () => host.Client.QueryAsync(host.Query("""{"$orderby":"optional"}""")));
        await Error("unsupported_query", () => host.Client.QueryAsync(host.Query("""{"$apply":"aggregate(status with sum as Invalid)"}""")));
    }

    [TestMethod]
    public void SchemaValidationSupportsNullableScalarsAndRejectsMalformedOrUnsupportedContracts()
    {
        var schema = Json("""{"type":"object","properties":{"value":{"type":["number","null"]}},"required":["value"],"additionalProperties":false}""");
        DocumentModel.Validate(schema, Json("""{"value":null}"""));
        DocumentModel.Validate(schema, Json("""{"value":0.125}"""));
        Assert.AreEqual("schema_validation", Assert.Throws<AscException>(() => DocumentModel.Validate(schema, Json("{}"))).Code);
        foreach (var malformed in new[] { """{"type":42}""", """{"required":"value"}""", """{"properties":[]}""", """{"minLength":-1}""", """{"minimum":2,"maximum":1}""" })
            Assert.Throws<AscException>(() => DocumentModel.ValidateSchema(Json(malformed)));
        Assert.AreEqual("unsupported_schema", Assert.Throws<AscException>(() => DocumentModel.ValidateSchema(Json("""{"$ref":"https://external.invalid/schema"}"""))).Code);
    }

    [TestMethod]
    public async Task PostgreSqlMutationReceiptsAndRevisionsSurviveProviderReconstruction()
    {
        var connection = Environment.GetEnvironmentVariable("AGENTSTRATION_TEST_ASC_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) Assert.Inconclusive("Set AGENTSTRATION_TEST_ASC_POSTGRES for the local database lane.");
        var first = new PostgreSqlDocumentProvider(connection, Schema); await first.InitializeAsync(default);
        var context = new AscDocumentContext(new("restart-" + Guid.NewGuid().ToString("N"), "documents", "binding"), new("isolated"), "subject", "grant",
            new("isolated", Put: true, Delete: true, QueryProfiles: ["odata/4.01"]));
        var write = new AscDocumentPut("before-restart", DateTimeOffset.UtcNow.AddMinutes(2), "retained", Json("{}"));
        var saved = await first.PutAsync(context, write, default);
        var restarted = new PostgreSqlDocumentProvider(connection, Schema);
        Assert.AreEqual(saved.Revision, (await restarted.GetAsync(context, new("after-restart", saved.Id), default)).Revision);
        Assert.AreEqual(saved.Revision, (await restarted.PutAsync(context, write, default)).Revision);
        var foreign = context with { Selection = new("other") };
        Assert.AreEqual("forbidden", (await Assert.ThrowsAsync<AscException>(async () => await restarted.GetAsync(foreign, new("cross-collection", saved.Id), default))).Code);
    }

    private sealed class TextProfileProvider : IAscDocumentProvider
    {
        public const string Id = "fixture.text/1";
        private readonly MemoryDocumentProvider inner = new(Schema, TimeProvider.System);
        public string ProviderId => "fixture.text";
        public string ProviderVersion => "1.0.0";
        public JsonElement ConfigurationSchema => Json("{}");
        public JsonElement DocumentSchema => Schema;
        public IReadOnlyList<AscQueryProfileDescriptor> QueryProfiles => [new(new(Id, "1.0", "text+json", AscDocumentValidation.Digest(Schema)),
            "Test fixture proving open profile encoding", ["all"], Json("""{"type":"string","enum":["all"]}"""))];
        public ValueTask<AscDocument> GetAsync(AscDocumentContext c, AscDocumentGet r, CancellationToken ct) => inner.GetAsync(c, r, ct);
        public ValueTask<AscDocument> PutAsync(AscDocumentContext c, AscDocumentPut r, CancellationToken ct) => inner.PutAsync(c, r, ct);
        public ValueTask<AscDocumentDeleted> DeleteAsync(AscDocumentContext c, AscDocumentDelete r, CancellationToken ct) => inner.DeleteAsync(c, r, ct);
        public ValueTask<AscDocumentProviderPage> QueryAsync(AscDocumentContext c, AscDocumentQuery r, string? position, CancellationToken ct)
        {
            AscDocumentValidation.Demand(c, "query");
            AscValidation.Require(r.Profile == QueryProfiles[0].Reference, "unsupported_capability", "Unknown fixture profile.");
            AscValidation.Require(r.Query.ValueKind == JsonValueKind.String && r.Query.GetString() == "all", "unsupported_query", "Expected the fixture text command.");
            var converted = c with { Policy = c.Policy with { QueryProfiles = ["odata/4.01"] } };
            return inner.QueryAsync(converted, r with { Profile = inner.QueryProfiles[0].Reference, Query = Json("{}") }, position, ct);
        }
    }
}
