using System.Text.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.Functions;
using Agentstration.Functions.Abstractions;
using Agentstration.Infrastructure.Functions;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Storage.Contracts;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class JsonExplodeTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static readonly JsonElement Item = Json("""{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"],"additionalProperties":false}""");

    [TestMethod]
    public async Task SelectedSharedSchemaDescribesRecordsWithoutClaimingTheCollection()
    {
        var context = new NativeFunctionExecutionContext();
        var uid = Guid.NewGuid();
        using var invocation = context.Enter("accepted", new Dictionary<string, SchemaBindingSnapshot>
        {
            ["itemSchema"] = new("$.itemSchema", SchemaFormatIds.JsonSchema, null, Item, "selected-hash",
                new()
                {
                    Name = "envelope",
                    Version = "1.0.0",
                    ScopeRef = ResourceScopeRef.Instance,
                    Namespace = ResourceNamespace.Default,
                    SchemaPath = "/properties/results/items"
                }, uid, 3)
            { ParentDefinitionHash = "parent-hash" }
        });
        var function = new JsonExplodeFunction(null!, null!, null!, null!, context);
        var input = Json("""{"value":{"results":[{"id":2},{"id":1}]},"path":"/results","itemSchema":{}}""");
        var output = await function.ExecuteAsync(input, default);
        var records = JsonRecordProjection.Parse(output)!;
        Assert.AreEqual(2, records.Value[0].GetProperty("id").GetInt32());
        Assert.AreEqual(uid, records.RecordContract.SchemaEvidence!.SchemaUid);
        Assert.AreEqual("parent-hash", records.RecordContract.SchemaEvidence.ParentDefinitionHash);
        Assert.AreEqual("selected-hash", records.RecordContract.SchemaEvidence.DefinitionHash);
        Assert.IsNull(records.CollectionContract.SchemaEvidence);
        Assert.AreEqual("array", records.CollectionContract.Schema!.Value.GetProperty("type").GetString());
        Assert.IsTrue(JsonElement.DeepEquals(Item, records.CollectionContract.Schema.Value.GetProperty("items")));
        Assert.IsTrue(JsonElement.DeepEquals(output, await function.ExecuteAsync(input, default)));
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("[1]")]
    [DataRow("[{\"id\":\"wrong\"}]")]
    [DataRow("[{\"other\":1}]")]
    [DataRow("{}")]
    public async Task InvalidSelectionsFailWithoutPartialResults(string selection)
    {
        var context = new NativeFunctionExecutionContext();
        using var invocation = context.Enter("accepted", new Dictionary<string, SchemaBindingSnapshot>
        { ["itemSchema"] = new("$.itemSchema", SchemaFormatIds.JsonSchema, null, Item, "schema") });
        var function = new JsonExplodeFunction(null!, null!, null!, null!, context);
        var failure = await Assert.ThrowsAsync<Exception>(() => function.ExecuteAsync(JsonSerializer.SerializeToElement(new
        { value = new { results = Json(selection) }, path = "/results", itemSchema = new { } }), default).AsTask());
        Assert.IsTrue(failure is TransformationFunctionException or DataContractException);
    }

    [TestMethod]
    public void RootObjectIsOneRecordAndLimitsRejectRatherThanTruncate()
    {
        var root = Json("""{"results":[{"id":1},{"id":2}]}""");
        var rootContract = DataContracts.Declared(Json("""{"type":"object"}"""), "root", "application/json")!;
        var normalized = JsonRecordProjection.Normalize(root, rootContract);
        Assert.AreEqual(1, normalized.Value.GetArrayLength());
        Assert.IsTrue(JsonElement.DeepEquals(root, normalized.Value[0]));
        Assert.AreEqual(rootContract.Digest, normalized.RecordContract.Digest);
        Assert.IsTrue(normalized.RecordContract.Validated);
        var businessMarker = Json("""{"$agentstration":"json.records/v1","status":"business-data"}""");
        Assert.IsTrue(JsonElement.DeepEquals(businessMarker, JsonRecordProjection.Normalize(businessMarker, rootContract).Value[0]),
            "An acquired object stays intact when no explicit record transformation has run.");
        Assert.ThrowsExactly<ArgumentException>(() => JsonRecordProjection.Normalize(Json("[{}]"), null));
        Assert.ThrowsExactly<ArgumentException>(() => StorageRecordSets.Encode(Enumerable.Repeat(Json("{}"), 101).ToArray()));
        Assert.ThrowsExactly<ArgumentException>(() => StorageRecordSets.Encode([JsonSerializer.SerializeToElement(new { text = new string('x', 65536) })]));
        Assert.ThrowsExactly<ArgumentException>(() => (normalized with { RecordContract = rootContract with { Digest = "forged" } }).Validate());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => normalized.Validate(cancelled.Token));
    }

    [TestMethod]
    public async Task ManifestProjectionChangesRowsWithoutMultiplyingArtifacts()
    {
        var context = new NativeFunctionExecutionContext();
        using var invocation = context.Enter("accepted", new Dictionary<string, SchemaBindingSnapshot>
        { ["itemSchema"] = new("$.itemSchema", SchemaFormatIds.JsonSchema, null, Item, "schema") });
        var function = new JsonExplodeFunction(null!, null!, null!, null!, context);
        var output = await function.ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            value = new
            {
                artifacts = new[] { new { artifactId = "source", kind = "inline", recordContract = new { },
                contentBase64 = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(Json("""[{"results":[{"id":1},{"id":2}]}]"""))) } }
            },
            path = "/results",
            itemSchema = new { }
        }), default);
        var artifact = output.GetProperty("artifacts").EnumerateArray().Single();
        using var content = JsonDocument.Parse(Convert.FromBase64String(artifact.GetProperty("contentBase64").GetString()!));
        Assert.AreEqual(2, content.RootElement.GetArrayLength());
        Assert.AreEqual("source", artifact.GetProperty("provenance").GetProperty("inputArtifactId").GetString());
    }

    [TestMethod]
    public void MaximumMembershipRoundTripsWithoutDuplicatingTheRecordSchemaInThePhysicalReference()
    {
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var storage = new StorageRevisionReference(new(scope, ResourceNamespace.Default, "Storage", "local"), Guid.NewGuid(), 1, "binding");
        var target = new StorageContentRequest(storage, new(scope, new(ResourceNamespace.Default, "DataSource", "source")), new());
        var bytes = StorageRecordSets.Encode(Enumerable.Repeat(Json("{}"), 100).ToArray());
        var reference = new StoragePublicationReference(target, "records", StorageRecordSets.Digest(bytes), bytes.Length, StorageRecordSets.Digest(bytes))
        { Records = new(Enumerable.Range(0, 100).Select(index => new StorageRecordMember("member-" + index, 2, StorageRecordSets.Digest("{}"u8))).ToArray()) };
        var rowContract = DataContracts.Declared(Json("""{"type":"object"}"""), "root")!;
        var location = new ArtifactStorageMaterialization(new(scope.TargetId!.Value), target, reference.Key, reference.Version,
            reference.Length, reference.Sha256, reference)
        { RecordContract = rowContract };
        var decoded = ArtifactStorageReferences.Parse(ArtifactStorageReferences.Encode(location))!;
        Assert.HasCount(100, StorageRecordSets.Members(decoded.Publication!));
        Assert.IsNull(decoded.RecordContract, "The row schema remains catalogue metadata, outside the physical content reference.");
        Assert.ThrowsExactly<ArgumentException>(() => StorageRecordSets.Members(reference with
        {
            Records = reference.Records with
            { Members = [reference.Records.Members[0], reference.Records.Members[0]] }
        }));
        Assert.ThrowsExactly<ArgumentException>(() => StorageRecordSets.Members(reference with
        {
            Records = reference.Records with
            { Members = [reference.Records.Members[0] with { Id = "../escape" }] }
        }));
    }
}
