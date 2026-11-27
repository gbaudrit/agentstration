using System.Text.Json;
using Agentstration.Afc.Abstractions;

namespace Agentstration.Extensions.GitHub;

public sealed class GitHubForgeProvider(GitHubContractSession session) : IAfcProvider
{
    public string ImplementationId => "Agentstration.Extensions.GitHub";
    public string ImplementationVersion => "1.0.0";
    public AfcCapabilities Capabilities { get; } = new(["resolve", "read", "create"], AfcConcurrency.Observation, AfcRecoveryScope.Process, 86_400, false, false, new());
    public async ValueTask<AfcRepository> ResolveAsync(AfcAccessContext context, AfcResolveRequest request, CancellationToken token)
    {
        try
        {
            var repository = await session.SendAsync("repos/" + session.Options.Repository, token);
            var refs = new List<AfcCommitRef>();
            foreach (var name in request.Refs.Count == 0 ? [GitHubConnection.Text(repository, "default_branch")] : request.Refs)
            {
                if (!GitHubCodingOperations.Branch(name)) throw new AfcException("invalid_ref");
                var commit = await session.SendAsync($"repos/{session.Options.Repository}/commits/{Uri.EscapeDataString(name)}", token);
                refs.Add(new(name, GitHubConnection.Text(commit, "sha")));
            }
            return new(context.Target.ProviderId, context.Target.TargetId, GitHubConnection.Id(repository), GitHubConnection.Text(repository, "full_name"),
                new(GitHubConnection.Text(repository, "html_url")), GitHubConnection.Text(repository, "default_branch"), refs, new(GitHubConnection.Text(repository, "clone_url")));
        }
        catch (GitHubProviderException error) { throw new AfcException(error.Code); }
    }
    public async ValueTask<AfcChangeRequest> ReadAsync(AfcAccessContext context, AfcReadRequest request, CancellationToken token)
    {
        if (request.RepositoryId != context.Target.TargetId || !GitHubConnection.Number(request.NativeId)) throw new AfcException("foreign_repository_or_item");
        try { return Map(await session.SendAsync($"repos/{session.Options.Repository}/pulls/{request.NativeId}", token)); }
        catch (GitHubProviderException error) { throw new AfcException(error.Code); }
    }
    public async ValueTask<AfcReceipt> CreateAsync(AfcAccessContext context, AfcCreateRequest request, CancellationToken token)
    {
        if (request.Intent.RepositoryId != context.Target.TargetId)
            return new(request.OperationKey, request.IntentDigest, AfcReceiptStatus.Rejected, ErrorCode: "unsupported_tracking_or_repository");
        try
        {
            var intent = request.Intent;
            var result = await session.PullAsync(request, new(session.Options.Repository, Title: intent.Title, Body: GitHubForgeBody.Encode(intent.Body, intent.Tracking ?? []), Head: intent.Head.Name,
                Base: intent.Base.Name, ExpectedHeadRevision: intent.Head.ObjectId, Draft: intent.Draft)
            { ExpectedBaseRevision = intent.Base.ObjectId }, token);
            var status = result.Status switch { "Succeeded" => AfcReceiptStatus.Applied, "Rejected" => AfcReceiptStatus.Rejected, "Missing" => AfcReceiptStatus.Missing, _ => AfcReceiptStatus.Uncertain };
            var item = status == AfcReceiptStatus.Applied && result.PullRequest is { } pr ? await session.SendAsync($"repos/{session.Options.Repository}/pulls/{pr.Number}", token) : (JsonElement?)null;
            var receipt = new AfcReceipt(request.OperationKey, request.IntentDigest, status, item is { } value ? Map(value) : null, result.ErrorCode);
            try { AfcValidation.Receipt(receipt, context.Target, request); }
            catch (AfcException) { return new(request.OperationKey, request.IntentDigest, AfcReceiptStatus.Uncertain, ErrorCode: "github_native_state_changed"); }
            return receipt;
        }
        catch (GitHubProviderException error) { return new(request.OperationKey, request.IntentDigest, AfcReceiptStatus.Uncertain, ErrorCode: error.Code); }
    }
    public ValueTask<AfcObservations> ObserveAsync(AfcAccessContext context, AfcObserveRequest request, CancellationToken token) => throw new AfcException("unsupported_operation");
    private AfcChangeRequest Map(JsonElement pr)
    {
        var body = GitHubForgeBody.Decode(pr.GetProperty("body").GetString());
        return new(new(session.Target.ProviderId, session.Target.TargetId, session.Target.TargetId, GitHubConnection.Number(pr)),
        GitHubConnection.Number(pr), GitHubConnection.Digest(pr), new(GitHubConnection.Text(pr, "html_url")), GitHubConnection.Text(pr, "title"), body.Body,
        GitHubConnection.Text(pr, "state"), pr.GetProperty("draft").GetBoolean(), pr.TryGetProperty("merged", out var merged) && merged.GetBoolean(),
        new(GitHubConnection.Text(pr.GetProperty("head"), "ref"), GitHubConnection.Text(pr.GetProperty("head"), "sha")),
        new(GitHubConnection.Text(pr.GetProperty("base"), "ref"), GitHubConnection.Text(pr.GetProperty("base"), "sha")), body.Tracking);
    }
}
