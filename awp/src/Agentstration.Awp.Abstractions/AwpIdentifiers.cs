using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Awp.Abstractions;

[JsonConverter(typeof(AwpIdentifierJsonConverterFactory))]
public readonly record struct AwpWorkerId(Guid Value);

[JsonConverter(typeof(AwpIdentifierJsonConverterFactory))]
public readonly record struct AwpWorkerSessionId(Guid Value);

[JsonConverter(typeof(AwpIdentifierJsonConverterFactory))]
public readonly record struct AwpAssignmentId(Guid Value);

[JsonConverter(typeof(AwpIdentifierJsonConverterFactory))]
public readonly record struct AwpAssignmentAttemptId(Guid Value);

[JsonConverter(typeof(AwpIdentifierJsonConverterFactory))]
public readonly record struct AwpEventId(Guid Value);

[JsonConverter(typeof(AwpIdentifierJsonConverterFactory))]
public readonly record struct AwpStepExecutionId(Guid Value);

[JsonConverter(typeof(AwpIdentifierJsonConverterFactory))]
public readonly record struct AwpTurnId(Guid Value);

[JsonConverter(typeof(AwpIdentifierJsonConverterFactory))]
public readonly record struct AwpTurnAttemptId(Guid Value);

[JsonConverter(typeof(AwpIdentifierJsonConverterFactory))]
public readonly record struct AwpToolCallId(Guid Value);

public sealed class AwpIdentifierJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(AwpWorkerId)
        || typeToConvert == typeof(AwpWorkerSessionId)
        || typeToConvert == typeof(AwpAssignmentId)
        || typeToConvert == typeof(AwpAssignmentAttemptId)
        || typeToConvert == typeof(AwpEventId)
        || typeToConvert == typeof(AwpStepExecutionId)
        || typeToConvert == typeof(AwpTurnId)
        || typeToConvert == typeof(AwpTurnAttemptId)
        || typeToConvert == typeof(AwpToolCallId);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert == typeof(AwpWorkerId)) return new GuidIdentifierConverter<AwpWorkerId>(value => new(value), value => value.Value);
        if (typeToConvert == typeof(AwpWorkerSessionId)) return new GuidIdentifierConverter<AwpWorkerSessionId>(value => new(value), value => value.Value);
        if (typeToConvert == typeof(AwpAssignmentId)) return new GuidIdentifierConverter<AwpAssignmentId>(value => new(value), value => value.Value);
        if (typeToConvert == typeof(AwpAssignmentAttemptId)) return new GuidIdentifierConverter<AwpAssignmentAttemptId>(value => new(value), value => value.Value);
        if (typeToConvert == typeof(AwpEventId)) return new GuidIdentifierConverter<AwpEventId>(value => new(value), value => value.Value);
        if (typeToConvert == typeof(AwpStepExecutionId)) return new GuidIdentifierConverter<AwpStepExecutionId>(value => new(value), value => value.Value);
        if (typeToConvert == typeof(AwpTurnId)) return new GuidIdentifierConverter<AwpTurnId>(value => new(value), value => value.Value);
        if (typeToConvert == typeof(AwpTurnAttemptId)) return new GuidIdentifierConverter<AwpTurnAttemptId>(value => new(value), value => value.Value);
        if (typeToConvert == typeof(AwpToolCallId)) return new GuidIdentifierConverter<AwpToolCallId>(value => new(value), value => value.Value);
        throw new NotSupportedException($"AWP identifier type '{typeToConvert}' is not supported.");
    }

    private sealed class GuidIdentifierConverter<TIdentifier>(
        Func<Guid, TIdentifier> create,
        Func<TIdentifier, Guid> getValue) : JsonConverter<TIdentifier>
    {
        public override TIdentifier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (!Guid.TryParseExact(value, "D", out var identifier) || identifier == Guid.Empty)
                throw new JsonException("An AWP identifier must be a non-empty canonical GUID.");
            return create(identifier);
        }

        public override void Write(Utf8JsonWriter writer, TIdentifier value, JsonSerializerOptions options)
        {
            var identifier = getValue(value);
            if (identifier == Guid.Empty) throw new JsonException("An AWP identifier must not be empty.");
            writer.WriteStringValue(identifier.ToString("D"));
        }
    }
}
