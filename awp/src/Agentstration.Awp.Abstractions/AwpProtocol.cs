using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Awp.Abstractions;

public static class AwpProtocol
{
    public const string Version = "1.0";
    public const string BasePath = "/api/awp/v1";
    public const string RegistrationPath = BasePath + "/workers/register";
    public const string ClaimPath = BasePath + "/assignments/claim";
    public const string HeartbeatPath = BasePath + "/assignments/heartbeat";
    public const string ExecutionMaterialPath = BasePath + "/assignments/material";
    public const string OpenStepExecutionPath = BasePath + "/assignments/steps/open";
    public const string OpenTurnPath = BasePath + "/assignments/turns/open";
    public const string AppendEventsPath = BasePath + "/assignments/events";
    public const string StoreCheckpointPath = BasePath + "/assignments/checkpoints";
    public const string GetCheckpointPath = BasePath + "/assignments/checkpoints/get";
    public const string CompleteAssignmentPath = BasePath + "/assignments/complete";
    public const string FailAssignmentPath = BasePath + "/assignments/fail";
    public const string InvokeModelPath = BasePath + "/assignments/model/invoke";
    public const string InvokeToolPath = BasePath + "/assignments/tools/invoke";
    public const string InvokeFlowToolPath = BasePath + "/assignments/flow-tools/invoke";
    public const string CaptureFlowArtifactPath = BasePath + "/assignments/flow-artifacts/capture";
    public const string CleanupFlowArtifactPath = BasePath + "/assignments/flow-artifacts/cleanup";
    public const string StoreArtifactPath = BasePath + "/assignments/artifacts";
    public const string GetArtifactPath = BasePath + "/assignments/artifacts/get";
    public const string CreateChildFlowPath = BasePath + "/assignments/child-flows";

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
