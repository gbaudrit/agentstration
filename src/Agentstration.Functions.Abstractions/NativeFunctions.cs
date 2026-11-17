using System.Text.Json;

namespace Agentstration.Functions.Abstractions;

/// <summary>One compiled implementation. The host owns validation, authorization and invocation lifetime.</summary>
public interface INativeTransformationFunction
{
    NativeFunctionDescriptor Descriptor { get; }
    ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken cancellationToken);
}

public sealed record NativeFunctionDescriptor(string Id, string Version, JsonElement InputSchema, JsonElement OutputSchema);

/// <summary>Host-supplied technical identity; it is never a caller-authored business argument.</summary>
public interface INativeFunctionExecutionContext
{
    string? InvocationId { get; }
}

public sealed class TransformationFunctionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>RFC 6901 selection. No expressions, wildcard expansion or implicit fallback.</summary>
public static class FunctionJsonPointer
{
    public static JsonElement Select(JsonElement value, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length > 1024 || path.Length > 0 && path[0] != '/') throw Invalid();
        if (path.Length == 0) return value;
        foreach (var encoded in path[1..].Split('/'))
        {
            for (var index = 0; index < encoded.Length; index++)
                if (encoded[index] == '~' && (++index >= encoded.Length || encoded[index] is not ('0' or '1'))) throw Invalid();
            var segment = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(segment, out var property)) value = property;
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(segment, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var position) && (segment == "0" || !segment.StartsWith('0'))
                && position >= 0 && position < value.GetArrayLength()) value = value[position];
            else throw new TransformationFunctionException("function_path_not_found", "The selected JSON Pointer does not address an available value.");
        }
        return value;
    }

    private static TransformationFunctionException Invalid() => new("function_path_invalid", "A bounded RFC 6901 JSON Pointer is required.");
}
