using System.Globalization;
using System.Text.Json;
using Agentstration.Atc.Abstractions;

namespace Agentstration.Extensions.GitHub;

public sealed class GitHubTrackingProvider(GitHubContractSession session) : IAtcProvider
{
    public string ImplementationId => "Agentstration.Extensions.GitHub";
    public string ImplementationVersion => "1.0.0";
    // GitHub has no unsafe-method CAS. Missing journals cannot prove absence of remote effects.
    public AtcCapabilities Capabilities { get; } = new(["list", "read", "create", "update", "relate"], ["classifications.add"],
        [AtcRelationKind.Parent, AtcRelationKind.Child, AtcRelationKind.BlockedBy], AtcConcurrency.Observation, AtcRecoveryScope.Process, 86_400, new(20));
    public async ValueTask<AtcItem> ReadAsync(AtcAccessContext context, AtcReadRequest request, CancellationToken token)
    {
        if (!GitHubConnection.Number(request.ItemId)) throw new AtcException("invalid_item");
        try { return Map(await session.ReadAsync(request.ItemId, token)); } catch (GitHubProviderException error) { throw new AtcException(error.Code); }
    }
    public async ValueTask<AtcPage> ListAsync(AtcAccessContext context, AtcListRequest request, CancellationToken token)
    {
        var page = request.Cursor is null ? 1 : int.TryParse(request.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        if (page is < 1 or > 10) throw new AtcException("invalid_cursor");
        try
        {
            var raw = await session.SendAsync($"repos/{session.Options.Repository}/issues?state=all&per_page={request.Limit}&page={page}", token);
            var items = new List<AtcItem>();
            foreach (var item in raw.EnumerateArray().Where(value => !value.TryGetProperty("pull_request", out _)))
                items.Add(Map(await session.ReadAsync(GitHubConnection.Number(item), token)));
            return new(items, raw.GetArrayLength() == request.Limit && page < 10 ? (page + 1).ToString(CultureInfo.InvariantCulture) : null);
        }
        catch (GitHubProviderException error) { throw new AtcException(error.Code); }
    }
    public async ValueTask<AtcReceipt> MutateAsync(AtcAccessContext context, AtcMutationRequest request, CancellationToken token)
    {
        GitHubTaskInput input;
        switch (request.Change)
        {
            case AtcCreate create: input = new("Publish", session.Options.Repository, Title: create.Title, Body: create.Body); break;
            case AtcUpdate update when update.Title is null && update.Body is null:
                input = new("Update", session.Options.Repository, update.ItemId, Labels: update.AddClassifications, ExpectedRevision: update.ExpectedRevision); break;
            case AtcAddRelation relation when relation.Relation.Item.ProviderId == session.Target.ProviderId && relation.Relation.Item.TargetId == session.Target.TargetId
                && relation.Relation.Kind is AtcRelationKind.Parent or AtcRelationKind.BlockedBy:
                input = new("Update", session.Options.Repository, relation.ItemId, Relations: [new(relation.Relation.Kind == AtcRelationKind.Parent ? "parent" : "blocked-by",
                    session.Options.Repository, relation.Relation.Item.ItemId)], ExpectedRevision: relation.ExpectedRevision); break;
            default: return new(request.OperationKey, request.IntentDigest, AtcMutationStatus.Rejected, ErrorCode: "unsupported_operation");
        }
        try
        {
            var result = await session.IssueAsync(request, input, token);
            var status = result.Status switch { "Succeeded" => AtcMutationStatus.Applied, "Rejected" => AtcMutationStatus.Rejected, "Missing" => AtcMutationStatus.Missing, _ => AtcMutationStatus.Uncertain };
            return new(request.OperationKey, request.IntentDigest, status, status == AtcMutationStatus.Applied && result.Task is { } task ? Map(task) : null, result.ErrorCode);
        }
        catch (GitHubProviderException error) { return new(request.OperationKey, request.IntentDigest, AtcMutationStatus.Uncertain, ErrorCode: error.Code); }
    }
    private AtcItem Map(GitHubTaskSnapshot value)
    {
        if (value.Relations.Any(relation => relation.Repository != session.Options.Repository)) throw new AtcException("related_target_unavailable");
        return new(new(session.Target.ProviderId, session.Target.TargetId, value.TaskId), value.Revision, value.Title, value.Body,
            new(value.State, value.CompletionReason), value.Labels, value.Relations.Select(relation => new AtcRelation(relation.Kind switch
            { "parent" => AtcRelationKind.Parent, "child" => AtcRelationKind.Child, "blocked-by" => AtcRelationKind.BlockedBy, _ => throw new AtcException("unsupported_relation") },
                new(session.Target.ProviderId, session.Target.TargetId, relation.TaskId))).ToArray(), true, [], []);
    }
}
