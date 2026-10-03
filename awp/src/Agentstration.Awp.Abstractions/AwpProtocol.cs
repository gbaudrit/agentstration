using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Awp.Abstractions;

public static class AwpProtocol
{
    public const string Version = "1.0";
    public const string BasePath = "/api/awp/v1";
    public const string RegistrationPath = BasePath + "/workers/register";
    public const string ClaimPath = BasePath + "/assignments/claim";

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new AwpIdentifierJsonConverterFactory());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}

public sealed record AwpEnvelope<TPayload>(
    string ProtocolVersion,
    Guid MessageId,
    DateTimeOffset SentAt,
    TPayload Payload);
