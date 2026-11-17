using System.Text.Json;
using Agentstration.Functions.Abstractions;

namespace Agentstration.Functions.Builtins;

public sealed class JsonSelectFunction : INativeTransformationFunction
{
    public NativeFunctionDescriptor Descriptor { get; } = FunctionSchemas.Descriptor("json.select", """{"value":{},"path":{"type":"string","maxLength":1024}}""", ["value", "path"]);
    public ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(FunctionJsonPointer.Select(input.GetProperty("value"), input.GetProperty("path").GetString()!).Clone());
    }
}

public sealed class JsonProjectFunction : INativeTransformationFunction
{
    public NativeFunctionDescriptor Descriptor { get; } = FunctionSchemas.Descriptor("json.project", """{"value":{},"properties":{"type":"array","maxItems":64,"items":{"type":"object","properties":{"name":{"type":"string","minLength":1,"maxLength":128},"path":{"type":"string","maxLength":1024}},"required":["name","path"],"additionalProperties":false}}}""", ["value", "properties"], """{"type":"object"}""");
    public ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in input.GetProperty("properties").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!values.TryAdd(property.GetProperty("name").GetString()!, FunctionJsonPointer.Select(input.GetProperty("value"), property.GetProperty("path").GetString()!).Clone()))
                throw new TransformationFunctionException("function_projection_duplicate", "Projected property names must be unique.");
        }
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(values));
    }
}

public sealed class CollectionFilterFunction : INativeTransformationFunction
{
    public NativeFunctionDescriptor Descriptor { get; } = FunctionSchemas.Descriptor("collection.filter", """{"value":{"type":"array","maxItems":4096},"path":{"type":"string","maxLength":1024},"equals":{}}""", ["value", "path", "equals"], """{"type":"array"}""");
    public ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var values = new List<JsonElement>();
        foreach (var item in input.GetProperty("value").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (JsonElement.DeepEquals(FunctionJsonPointer.Select(item, input.GetProperty("path").GetString()!), input.GetProperty("equals"))) values.Add(item);
        }
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(values));
    }
}

public sealed class CollectionDistinctFunction : INativeTransformationFunction
{
    public NativeFunctionDescriptor Descriptor { get; } = FunctionSchemas.Descriptor("collection.distinct", """{"value":{"type":"array","maxItems":4096},"path":{"type":"string","maxLength":1024}}""", ["value", "path"], """{"type":"array"}""");
    public ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var keys = new List<JsonElement>();
        var values = new List<JsonElement>();
        foreach (var item in input.GetProperty("value").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = FunctionJsonPointer.Select(item, input.GetProperty("path").GetString()!);
            if (keys.Any(existing => JsonElement.DeepEquals(existing, key))) continue;
            keys.Add(key); values.Add(item);
        }
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(values));
    }
}

public static class FunctionSchemas
{
    public static NativeFunctionDescriptor Descriptor(string id, string properties, string[] required, string output = "{}") =>
        new(id, "1.0.0", JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = JsonSerializer.Deserialize<JsonElement>(properties), required, additionalProperties = false
        }), JsonSerializer.Deserialize<JsonElement>(output));
}
