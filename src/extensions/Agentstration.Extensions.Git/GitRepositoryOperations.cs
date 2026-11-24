using System.Text.Json;
using System.Diagnostics;
using Agentstration.Aep.AspNetCore;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.Git;

public sealed class GitRepositoryOperations(IOptions<GitSourceProviderOptions> sourceOptions, IOptions<GitOperationsOptions> operationOptions,
    GitPublicationJournal journal, TimeProvider clock) : IDisposable
{
    private static readonly ActivitySource Activities = new("Agentstration.Extensions.Git.Operations");
    private readonly SemaphoreSlim slots = new(4);
    private int waiting;

    public async Task<GitRepositoryResponse> InvokeAsync(string operation, string mode, string operationKey, string intentDigest,
        GitRepositoryInput request, string repository, string authentication, string username, string credential,
        IReadOnlyList<string> allowedRefPrefixes, IReadOnlyList<string> allowedPathPrefixes, CancellationToken token)
    {
        GitRepositoryResponse Result(string status, string? code = null) => new(operationKey, intentDigest, status, request?.RefName ?? "", ErrorCode: code);
        if (!Coordinate(operationKey) || !Coordinate(intentDigest) || operation is not ("resolve" or "read" or "diff" or "publish") || mode is not ("execute" or "recover"))
            return Result("Rejected", "git_operation_invalid");
        using var activity = Activities.StartActivity("git.repository." + operation);
        activity?.SetTag("git.operation.key", operationKey);
        if (Interlocked.Increment(ref waiting) > 32) { Interlocked.Decrement(ref waiting); return Result("Rejected", "git_operation_busy"); }
        bool acquired;
        try { acquired = await slots.WaitAsync(TimeSpan.FromSeconds(1), token); }
        finally { Interlocked.Decrement(ref waiting); }
        if (!acquired) return Result("Rejected", "git_operation_busy");
        var sent = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(operationOptions.Value.TimeoutSeconds), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var cancellation = linked.Token;
        try
        {
            await using var connection = new GitRepositoryConnection(sourceOptions.Value, operationOptions.Value, repository, authentication, username,
                credential, allowedRefPrefixes, allowedPathPrefixes);
            connection.Validate(request, operation);
            if (operation != "publish")
            {
                var current = await connection.ResolveAsync(request.RefName, cancellation);
                if (current is null) return Result("Missing", "git_ref_not_found");
                if (operation == "resolve") return new(operationKey, intentDigest, "Succeeded", request.RefName, current, CurrentRevision: current);
                await connection.FetchAsync(current, cancellation);
                if (!await connection.AncestorAsync(request.Revision!, current, cancellation)) return Result("Rejected", "git_revision_not_authorized");
                if (operation == "read") return new(operationKey, intentDigest, "Succeeded", request.RefName, request.Revision, CurrentRevision: current,
                    Files: await connection.ReadAsync(request, cancellation));
                if (!await connection.AncestorAsync(request.BaseRevision!, current, cancellation)) return Result("Rejected", "git_revision_not_authorized");
                return new(operationKey, intentDigest, "Succeeded", request.RefName, request.Revision, request.BaseRevision, current,
                    DiffBase64: await connection.DiffAsync(request, cancellation));
            }
            // Authorize native repository access before exposing even receipt existence.
            // Until the journal is read, an unavailable provider cannot prove no prior effect.
            sent = true;
            await connection.ResolveAsync(request.RefName, cancellation);
            await using var lease = await journal.OpenAsync(operationKey, cancellation);
            var input = JsonSerializer.SerializeToElement(request);
            var intent = await lease.ReadAsync(cancellation);
            var authority = connection.Repository;
            if (intent is not null && (intent.Authority != authority || intent.IntentDigest != intentDigest || intent.Input.GetRawText() != input.GetRawText()))
                return Result("Rejected", "git_operation_conflict");
            sent = intent?.Phase == "sent";
            if (intent?.Result is { } terminal)
            {
                if (terminal.OperationKey != operationKey || terminal.IntentDigest != intentDigest || terminal.RefName != request.RefName
                    || terminal.Status == "Succeeded" && terminal.Revision != intent.Candidate) return Result("Uncertain", "git_journal_invalid");
                // Recheck native access even when the durable result itself is immutable.
                await connection.ResolveAsync(request.RefName, cancellation);
                return terminal;
            }
            if (mode == "recover" && !sent) return Result("Missing");
            if (intent is null)
            {
                intent = new(authority, intentDigest, input, clock.GetUtcNow().ToUnixTimeSeconds(), "prepared");
                await lease.SaveAsync(intent, cancellation);
                activity?.SetTag("git.publication.phase", "prepared");
            }
            if (sent)
            {
                var recovered = await RecoverAsync(connection, request, intent.Candidate!, operationKey, intentDigest, cancellation);
                if (recovered.Status == "Succeeded") await lease.SaveAsync(intent with { Result = recovered }, cancellation);
                return recovered;
            }
            var observed = await connection.ResolveAsync(request.RefName, cancellation);
            if (observed != request.ExpectedRefRevision)
            {
                var conflict = Result("Conflict", "git_base_revision_changed") with { CurrentRevision = observed };
                await lease.SaveAsync(intent with { Result = conflict }, cancellation); return conflict;
            }
            if (request.BaseRevision is { } baseline) await connection.FetchAsync(baseline, cancellation);
            var candidate = await connection.BuildAsync(request, intent.Timestamp, operationKey, intentDigest, cancellation);
            if (intent.Candidate is { } previous && previous != candidate) return Result("Rejected", "git_journal_invalid");
            intent = intent with { Candidate = candidate, Phase = "sent" };
            await lease.SaveAsync(intent, cancellation); sent = true;
            activity?.SetTag("git.publication.phase", "sent");
            // A lease supplies the native compare-and-swap precondition. The only candidate
            // generated above is a direct child of the expected revision (or a new ref).
            var pushed = await connection.PushAsync(request, candidate, cancellation);
            GitRepositoryResponse result;
            if (pushed.ExitCode == 0) result = new(operationKey, intentDigest, "Succeeded", request.RefName, candidate, request.BaseRevision, candidate);
            else if (pushed.Text.Split('\n').Any(line => line.StartsWith("!\t", StringComparison.Ordinal)
                && (line.Contains(":" + request.RefName + "\t[rejected]", StringComparison.Ordinal)
                    || line.Contains(":" + request.RefName + "\t[remote rejected]", StringComparison.Ordinal))))
            {
                var changed = await connection.ResolveAsync(request.RefName, cancellation);
                result = changed != request.ExpectedRefRevision
                    ? Result("Conflict", "git_base_revision_changed") with { CurrentRevision = changed }
                    : Result("Rejected", "git_publication_rejected");
            }
            else result = await RecoverAsync(connection, request, candidate, operationKey, intentDigest, cancellation);
            if (result.Status != "Uncertain") await lease.SaveAsync(intent with { Result = result }, cancellation);
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Result(sent ? "Uncertain" : "Rejected", "git_operation_timeout"); }
        catch (GitOperationException error) { return Result(sent ? "Uncertain" : "Rejected", error.Code); }
        catch (AepServerException) { return Result(sent ? "Uncertain" : "Rejected", "git_unavailable"); }
        catch (JsonException) { return Result("Uncertain", "git_journal_invalid"); }
        finally { slots.Release(); }
    }

    private static async Task<GitRepositoryResponse> RecoverAsync(GitRepositoryConnection connection, GitRepositoryInput input, string candidate,
        string key, string digest, CancellationToken token)
    {
        var current = await connection.ResolveAsync(input.RefName, token);
        if (current is not null)
        {
            await connection.FetchAsync(current, token);
            if (await connection.AncestorAsync(candidate, current, token)
                && await connection.VerifyPublicationAsync(input, candidate, key, digest, token))
                return new(key, digest, "Succeeded", input.RefName, candidate, input.BaseRevision, current);
        }
        return new(key, digest, "Uncertain", input.RefName, candidate, input.BaseRevision, current, ErrorCode: "git_publication_outcome_uncertain");
    }
    private static bool Coordinate(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    public void Dispose() { slots.Dispose(); GC.SuppressFinalize(this); }
}
