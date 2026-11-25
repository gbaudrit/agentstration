using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Infrastructure.Work;
using Agentstration.Work;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public void YamlUsageRoundTripPreservesNormalizedAccountingAndDigest()
    {
        var id = new WorkUsageId(Guid.NewGuid());
        var total = FlowRunTokenUsageAggregation.Invocation(13, 7, 23);
        var accounting = new WorkUsageRecord(id, id, 1, WorkPlanId.New(), 1, new string('a', 64), new(Guid.NewGuid()), 1, new string('b', 64),
            new(WorkUsageSourceKind.CodingCli, "coding-cli/1.0", Guid.NewGuid(), Guid.NewGuid(), "fixture-run", WorkPlanAttemptStatus.Failed)
            { AssignmentId = Guid.NewGuid(), AssignmentAttemptId = Guid.NewGuid(), InvocationId = Guid.NewGuid() },
            WorkUsageMeasurement.Measured, new(total, total) { CachedInputTokens = 3, ReasoningOutputTokens = 2 }, DateTimeOffset.UnixEpoch)
        { ProjectId = new(Guid.NewGuid()), ParticipantId = new(Guid.NewGuid()) };
        var usage = new WorkSharingUsage(id.Value, accounting.PlanId, accounting.TaskId, new Dictionary<string, long>()) { Accounting = accounting };
        var change = WorkSharingContent.Normalize(new(new(WorkSharingDomain.Usage, id.ToString()), 0, JsonSerializer.SerializeToElement(usage, SharingJson)));
        Assert.IsTrue(change.IsSuccess, change.ErrorCode);
        var record = new WorkSharingRecord(change.Value!.Key, 1, change.Value.Content, WorkPlanDefinition.Digest(change.Value.Content), accounting.ParticipantId.Value, DateTimeOffset.UnixEpoch);
        var codec = new YamlWorkDocumentCodec();
        var encoded = codec.Write(record);
        var decoded = codec.Read<WorkSharingRecord>(encoded, 32 * 1024);
        Assert.IsTrue(decoded.IsSuccess, decoded.ErrorCode);
        var normalized = WorkSharingContent.Normalize(new(decoded.Value!.Key, 0, decoded.Value.Content));
        Assert.IsTrue(normalized.IsSuccess, normalized.ErrorCode);
        Assert.AreEqual(record.Digest, WorkPlanDefinition.Digest(normalized.Value!.Content),
            System.Text.Encoding.UTF8.GetString(encoded) + "\n" + normalized.Value.Content.GetRawText());
    }
}
