using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace Agentstration.Afc.Abstractions;

public static class AfcSchemas
{
    public static JsonElement Input(string operation) => operation switch
    {
        "describe" => Schema<AfcInvocation<AfcDescribeRequest>>(),
        "resolve" => Schema<AfcInvocation<AfcResolveRequest>>(),
        "read" => Schema<AfcInvocation<AfcReadRequest>>(),
        "create" => Schema<AfcInvocation<AfcCreateRequest>>(),
        "observe" => Schema<AfcInvocation<AfcObserveRequest>>(),
        _ => throw new AfcException("unsupported_operation")
    };
    public static JsonElement Output(string operation) => operation switch
    {
        "describe" => Reply<AfcDescription>(),
        "resolve" => Reply<AfcRepository>(),
        "read" => Reply<AfcChangeRequest>(),
        "create" => Reply<AfcReceipt>(),
        "observe" => Reply<AfcObservations>(),
        _ => throw new AfcException("unsupported_operation")
    };

    public static AfcDeclaration Declaration(string prefix = "afc")
    {
        AfcValidation.Prefix(prefix);
        return new(AfcContract.Id, AfcContract.Version, prefix, AfcContract.Tools.SelectMany(operation => new[]
        {
            KeyValuePair.Create(operation + ".input", Digest(Input(operation))),
            KeyValuePair.Create(operation + ".output", Digest(Output(operation)))
        }).ToDictionary(StringComparer.Ordinal));
    }

    public static void Validate(AfcDeclaration? declaration)
    {
        AfcValidation.Require(declaration is not null && declaration.ContractId == AfcContract.Id, "incompatible_contract");
        AfcValidation.Revision(declaration.Version); AfcValidation.Prefix(declaration.ToolPrefix);
        var expected = Declaration(declaration.ToolPrefix);
        AfcValidation.Require(declaration.SchemaDigests is { Count: 10 }
            && expected.SchemaDigests.All(pair => declaration.SchemaDigests.TryGetValue(pair.Key, out var actual) && actual == pair.Value), "incompatible_schema");
    }

    private static string Digest(JsonElement schema) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(schema.GetRawText())));
    private static JsonElement Reply<T>()
    {
        var schema = JsonNode.Parse(Schema<AfcReply<T>>().GetRawText())!.AsObject();
        schema["oneOf"] = new JsonArray(Branch("data", "error"), Branch("error", "data"));
        return JsonSerializer.SerializeToElement(schema);
    }
    private static JsonObject Branch(string present, string absent) => new()
    {
        ["required"] = new JsonArray(present),
        ["not"] = new JsonObject { ["required"] = new JsonArray(absent) },
        ["properties"] = new JsonObject { [present] = new JsonObject { ["not"] = new JsonObject { ["type"] = "null" } } }
    };
    private static JsonElement Schema<T>()
    {
        var node = AfcContract.Json.GetJsonSchemaAsNode(typeof(T), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, schema) =>
            {
                if (schema is not JsonObject value) return schema;
                if (value.ContainsKey("properties")) value["additionalProperties"] = false;
                switch (context.PropertyInfo?.Name)
                {
                    case "contractId": value["const"] = AfcContract.Id; break;
                    case "requestId":
                    case "operationKey":
                    case "providerId":
                    case "targetId":
                    case "bindingId":
                    case "revision":
                    case "expectedRevision":
                    case "itemId":
                    case "transitionId":
                        value["minLength"] = 1; value["maxLength"] = 128; break;
                    case "title": value["minLength"] = 1; value["maxLength"] = 512; break;
                    case "text": case "body": value["maxLength"] = 32768; break;
                }
                return value;
            }
        });
        node.AsObject()["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        return JsonSerializer.SerializeToElement(node);
    }
}
