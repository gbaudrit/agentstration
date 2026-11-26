using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace Agentstration.Atc.Abstractions;

public static class AtcSchemas
{
    public static JsonElement Input(string operation) => operation switch
    {
        "describe" => Schema<AtcInvocation<AtcDescribeRequest>>(),
        "list" => Schema<AtcInvocation<AtcListRequest>>(),
        "read" => Schema<AtcInvocation<AtcReadRequest>>(),
        "mutate" => Schema<AtcInvocation<AtcMutationRequest>>(),
        _ => throw new AtcException("unsupported_operation")
    };
    public static JsonElement Output(string operation) => operation switch
    {
        "describe" => Reply<AtcDescription>(),
        "list" => Reply<AtcPage>(),
        "read" => Reply<AtcItem>(),
        "mutate" => Reply<AtcReceipt>(),
        _ => throw new AtcException("unsupported_operation")
    };

    public static AtcDeclaration Declaration(string prefix = "atc")
    {
        AtcValidation.Prefix(prefix);
        return new(AtcContract.Id, AtcContract.Version, prefix, AtcContract.Tools.SelectMany(operation => new[]
        {
            KeyValuePair.Create(operation + ".input", Digest(Input(operation))),
            KeyValuePair.Create(operation + ".output", Digest(Output(operation)))
        }).ToDictionary(StringComparer.Ordinal));
    }

    public static void Validate(AtcDeclaration? declaration)
    {
        AtcValidation.Require(declaration is not null && declaration.ContractId == AtcContract.Id, "incompatible_contract");
        AtcValidation.Revision(declaration.Version); AtcValidation.Prefix(declaration.ToolPrefix);
        var expected = Declaration(declaration.ToolPrefix);
        AtcValidation.Require(declaration.SchemaDigests is { Count: 8 }
            && expected.SchemaDigests.All(pair => declaration.SchemaDigests.TryGetValue(pair.Key, out var actual) && actual == pair.Value), "incompatible_schema");
    }

    private static string Digest(JsonElement schema) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(schema.GetRawText())));
    private static JsonElement Reply<T>()
    {
        var schema = JsonNode.Parse(Schema<AtcReply<T>>().GetRawText())!.AsObject();
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
        var node = AtcContract.Json.GetJsonSchemaAsNode(typeof(T), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, schema) =>
            {
                if (schema is not JsonObject value) return schema;
                if (value.ContainsKey("properties")) value["additionalProperties"] = false;
                switch (context.PropertyInfo?.Name)
                {
                    case "contractId": value["const"] = AtcContract.Id; break;
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
                    case "cursor": value["maxLength"] = 4096; break;
                    case "limit": value["minimum"] = 1; value["maximum"] = 100; break;
                }
                return value;
            }
        });
        node.AsObject()["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        return JsonSerializer.SerializeToElement(node);
    }
}
