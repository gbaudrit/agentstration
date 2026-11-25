using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class JsonCollectionContractTests
{
    [TestMethod]
    public void DeclaredPathsPreserveEscapingAndExactParentEvidence()
    {
        var parent = Snapshot("""{"type":"object","properties":{"a/b~c":{"type":"array","items":{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"]}},"other":{"type":"array","items":{"type":"string"}}}}""");
        var choices = JsonCollectionContracts.Discover(parent);
        Assert.IsNull(choices.Diagnostic);
        var collection = choices.Collections.Single();
        Assert.AreEqual("/a~1b~0c", collection.DataPath);
        Assert.AreEqual("/properties/a~1b~0c/items", collection.SchemaPath);
        Assert.AreEqual(parent.Source!.Name, collection.Item.Source!.Name);
        Assert.AreEqual(parent.Source.Version, collection.Item.Source.Version);
        Assert.AreEqual(parent.SchemaUid, collection.Item.SchemaUid);
        Assert.AreEqual(parent.DefinitionHash, collection.Item.ParentDefinitionHash);
        Assert.AreNotEqual(parent.DefinitionHash, collection.Item.DefinitionHash);
    }

    [TestMethod]
    public void ReferenceTargetLocationIsRetainedAndArrayWildcardIsNeverInvented()
    {
        var parent = Snapshot("""{"type":"object","properties":{"rows":{"$ref":"#/$defs/records"}},"$defs":{"records":{"type":"array","items":{"type":"object","properties":{"nested":{"type":"array","items":{"type":"object"}}}}}}}""");
        var choice = JsonCollectionContracts.Discover(parent).Collections.Single();
        Assert.AreEqual("/rows", choice.DataPath);
        Assert.AreEqual("/$defs/records/items", choice.SchemaPath);
    }

    [TestMethod]
    [DataRow("{\"$ref\":\"#\"}", "schema_reference_cycle")]
    [DataRow("{\"$ref\":\"https://invalid.example/schema\"}", "schema_reference_remote_unsupported")]
    [DataRow("{\"type\":\"object\",\"oneOf\":[{\"type\":\"object\"},{\"type\":\"object\"}]}", "schema_collection_ambiguous")]
    public void AmbiguityAndUnsupportedReferencesDoNotProduceProposals(string json, string diagnostic)
    {
        var choices = JsonCollectionContracts.Discover(Snapshot(json));
        Assert.IsEmpty(choices.Collections);
        Assert.AreEqual(diagnostic, choices.Diagnostic);
    }

    [TestMethod]
    public void RootCollectionIsSupportedWithoutInferringAnythingFromData()
    {
        var choice = JsonCollectionContracts.Discover(Snapshot("""{"type":"array","items":{"type":"object"}}""")).Collections.Single();
        Assert.AreEqual("", choice.DataPath);
        Assert.AreEqual("/items", choice.SchemaPath);
    }

    private static SchemaBindingSnapshot Snapshot(string json)
    {
        var content = JsonSerializer.Deserialize<JsonElement>(json);
        return new("function.output", SchemaFormatIds.JsonSchema, BuiltinJsonSchemaFormatHandler.Draft202012, content,
            JsonSchemaSelection.DefinitionHash(SchemaFormatIds.JsonSchema, BuiltinJsonSchemaFormatHandler.Draft202012, content),
            new() { Name = "declared", Version = "1.2.3", ScopeRef = ResourceScopeRef.Instance, Namespace = ResourceNamespace.Default }, Guid.NewGuid(), 3);
    }
}
