using System.Text;
using System.Text.Json;
using Agentstration.Testing;
using Agentstration.Extensions.Git;
using Microsoft.Extensions.Options;

namespace Agentstration.SourceProviders.Git.Tests;

[TestClass]
public sealed class GitRepositoryOperationTests
{
    private static string Key(char value) => new(value, 64);
    private static string Content(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static GitRepositoryInput Publication(string revision, string value = "{\"revision\":2}") => new("refs/heads/main",
        BaseRevision: revision, ExpectedRefRevision: revision, Changes: [new("shared/plan.json", Content(value))], Message: "Publish fixture changes");
    private static GitRepositoryOperations Service(AuthenticatedGitFixture fixture) => new(
        Options.Create(new GitSourceProviderOptions { TemporaryDirectory = Path.Combine(fixture.Root, "operations"), MaximumRepositoryBytes = 32 * 1024 * 1024 }),
        Options.Create(Configuration(fixture)), new(Options.Create(Configuration(fixture))), TimeProvider.System);
    private static GitOperationsOptions Configuration(AuthenticatedGitFixture fixture) => new()
    { Enabled = true, AllowedRepositories = [fixture.Repository], AllowHttpLoopback = true, StateDirectory = Path.Combine(fixture.Root, "journal") };
    private static Task<GitRepositoryResponse> Invoke(GitRepositoryOperations service, AuthenticatedGitFixture fixture, string operation, GitRepositoryInput input,
        char key = 'a', char digest = 'b', string mode = "execute", string? credential = null, string? repository = null, CancellationToken token = default) =>
        service.InvokeAsync(operation, mode, Key(key), Key(digest), input, repository ?? fixture.Repository, "Basic", "fixture", credential ?? AuthenticatedGitFixture.Credential,
            ["refs/heads/main", "refs/heads/feature"], ["shared"], token);

    [TestMethod]
    public async Task AuthenticatedNativeGitResolvesReadsPublishesExactCommitAndReturnsBoundedDiff()
    {
        await using var fixture = await AuthenticatedGitFixture.CreateAsync(); using var service = Service(fixture);
        var resolved = await Invoke(service, fixture, "resolve", new("refs/heads/main"));
        Assert.AreEqual("Succeeded", resolved.Status, resolved.ErrorCode); Assert.AreEqual(fixture.InitialRevision, resolved.Revision);
        var read = await Invoke(service, fixture, "read", new("refs/heads/main", fixture.InitialRevision, Paths: ["shared/plan.json"]));
        Assert.AreEqual("Succeeded", read.Status, read.ErrorCode); Assert.AreEqual(Content("{\"revision\":1}"), read.Files!.Single().ContentBase64);
        var published = await Invoke(service, fixture, "publish", Publication(fixture.InitialRevision));
        Assert.AreEqual("Succeeded", published.Status, published.ErrorCode);
        Assert.AreEqual(published.Revision, (await fixture.GitAsync(fixture.RepositoryPath, "rev-parse", "refs/heads/main")).Trim());
        Assert.AreEqual(fixture.InitialRevision, (await fixture.GitAsync(fixture.RepositoryPath, "rev-parse", published.Revision + "^")).Trim());
        Assert.AreEqual("{\"revision\":2}", await fixture.GitAsync(fixture.RepositoryPath, "show", published.Revision + ":shared/plan.json"));
        Assert.AreEqual("Not granted by the connection path policy", await fixture.GitAsync(fixture.RepositoryPath, "show", published.Revision + ":private.txt"));
        var diff = await Invoke(service, fixture, "diff", new("refs/heads/main", published.Revision, fixture.InitialRevision));
        Assert.AreEqual("Succeeded", diff.Status, diff.ErrorCode); StringAssert.Contains(Encoding.UTF8.GetString(Convert.FromBase64String(diff.DiffBase64!)), "+{\"revision\":2}");
        var old = await Invoke(service, fixture, "read", new("refs/heads/main", fixture.InitialRevision, Paths: ["shared/plan.json"]));
        Assert.AreEqual(Content("{\"revision\":1}"), old.Files!.Single().ContentBase64);
        Assert.AreEqual("2", (await fixture.GitAsync(fixture.RepositoryPath, "rev-list", "--count", "refs/heads/main")).Trim());
    }

    [TestMethod]
    public async Task NativeGitRejectsMovedRemoteBaseAndRetainsConcurrentContribution()
    {
        await using var fixture = await AuthenticatedGitFixture.CreateAsync(); using var service = Service(fixture);
        await File.WriteAllTextAsync(Path.Combine(fixture.Work, "shared", "other.json"), "Concurrent contribution");
        await fixture.GitAsync(fixture.Work, "add", "."); await fixture.GitAsync(fixture.Work, "commit", "-m", "Concurrent contribution");
        await fixture.GitAsync(fixture.Work, "push", fixture.RepositoryPath, "HEAD:refs/heads/main");
        var current = (await fixture.GitAsync(fixture.RepositoryPath, "rev-parse", "main")).Trim();
        var stale = await Invoke(service, fixture, "publish", Publication(fixture.InitialRevision));
        Assert.AreEqual("Conflict", stale.Status); Assert.AreEqual("git_base_revision_changed", stale.ErrorCode); Assert.AreEqual(current, stale.CurrentRevision);
        Assert.AreEqual(current, (await fixture.GitAsync(fixture.RepositoryPath, "rev-parse", "main")).Trim());
        Assert.AreEqual("Concurrent contribution", await fixture.GitAsync(fixture.RepositoryPath, "show", "main:shared/other.json"));
    }

    [TestMethod]
    public async Task NativeGitCompareAndSwapRejectsAnActualReceiveRaceWithoutRewritingTheRemote()
    {
        await using var fixture = await AuthenticatedGitFixture.CreateAsync(); using var service = Service(fixture);
        await File.WriteAllTextAsync(Path.Combine(fixture.Work, "shared", "race.json"), "Concurrent contribution");
        await fixture.GitAsync(fixture.Work, "add", "."); await fixture.GitAsync(fixture.Work, "commit", "-m", "Race contribution");
        var other = (await fixture.GitAsync(fixture.Work, "rev-parse", "HEAD")).Trim();
        await fixture.GitAsync(fixture.Work, "push", fixture.RepositoryPath, "HEAD:refs/heads/concurrent-objects");
        fixture.BeforeWrite = async () => { await fixture.GitAsync(fixture.RepositoryPath, "update-ref", "refs/heads/main", other, fixture.InitialRevision); };
        var result = await Invoke(service, fixture, "publish", Publication(fixture.InitialRevision));
        Assert.AreEqual("Conflict", result.Status, result.ErrorCode); Assert.AreEqual("git_base_revision_changed", result.ErrorCode);
        Assert.AreEqual(other, (await fixture.GitAsync(fixture.RepositoryPath, "rev-parse", "main")).Trim());
        Assert.AreEqual("Concurrent contribution", await fixture.GitAsync(fixture.RepositoryPath, "show", "main:shared/race.json"));
    }

    [TestMethod]
    public async Task NativeGitPublicationRecoversLostResponseAfterRestartWithoutDuplicatingCommit()
    {
        await using var fixture = await AuthenticatedGitFixture.CreateAsync(); var input = Publication(fixture.InitialRevision);
        using (var service = Service(fixture))
        {
            fixture.LoseNextWriteResponse = true;
            var lost = await Invoke(service, fixture, "publish", input);
            Assert.AreEqual("Uncertain", lost.Status, lost.ErrorCode);
        }
        fixture.Unavailable = false;
        using var restarted = Service(fixture);
        var recovered = await Invoke(restarted, fixture, "publish", input, mode: "recover");
        Assert.AreEqual("Succeeded", recovered.Status, recovered.ErrorCode);
        var replay = await Invoke(restarted, fixture, "publish", input);
        Assert.AreEqual(JsonSerializer.Serialize(recovered), JsonSerializer.Serialize(replay));
        Assert.AreEqual("2", (await fixture.GitAsync(fixture.RepositoryPath, "rev-list", "--count", "main")).Trim());
        Assert.AreEqual(recovered.Revision, (await fixture.GitAsync(fixture.RepositoryPath, "rev-parse", "main")).Trim());
        var conflict = await Invoke(restarted, fixture, "publish", input with { Message = "Changed operation intent" });
        Assert.AreEqual("Rejected", conflict.Status); Assert.AreEqual("git_operation_conflict", conflict.ErrorCode);
        foreach (var path in Directory.GetFiles(Path.Combine(fixture.Root, "journal"), "*.json")) Assert.IsFalse((await File.ReadAllTextAsync(path)).Contains(AuthenticatedGitFixture.Credential, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NativeGitConcurrentReplaysAndNewRefCreationKeepOneStablePublication()
    {
        await using var fixture = await AuthenticatedGitFixture.CreateAsync(); using var service = Service(fixture);
        var input = Publication(fixture.InitialRevision) with { RefName = "refs/heads/feature/delivery", ExpectedRefRevision = null };
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Invoke(service, fixture, "publish", input)));
        Assert.IsTrue(results.All(result => result.Status == "Succeeded"), string.Join(',', results.Select(result => result.ErrorCode ?? result.Status)));
        Assert.AreEqual(1, results.Select(result => result.Revision).Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual("2", (await fixture.GitAsync(fixture.RepositoryPath, "rev-list", "--count", input.RefName)).Trim());
        Assert.AreEqual(fixture.InitialRevision, (await fixture.GitAsync(fixture.RepositoryPath, "rev-parse", "main")).Trim());
    }

    [TestMethod]
    public async Task NativeGitRejectsUnauthorizedPathsRepositoryCredentialsAndOversizedChanges()
    {
        await using var fixture = await AuthenticatedGitFixture.CreateAsync(); using var service = Service(fixture);
        foreach (var path in new[] { "../private.txt", "private.txt", "shared/../../private.txt", "shared/.git/config", "/shared/plan.json", "shared\\plan.json" })
        {
            var denied = await Invoke(service, fixture, "read", new("refs/heads/main", fixture.InitialRevision, Paths: [path]));
            Assert.AreEqual("Rejected", denied.Status); Assert.AreEqual("git_path_denied", denied.ErrorCode);
        }
        var repository = await Invoke(service, fixture, "resolve", new("refs/heads/main"), repository: fixture.Repository + "/other");
        Assert.AreEqual("git_repository_denied", repository.ErrorCode);
        var credentials = await Invoke(service, fixture, "resolve", new("refs/heads/main"), credential: "public-disposable-denied-git-fixture");
        Assert.AreEqual("Rejected", credentials.Status); Assert.IsFalse(JsonSerializer.Serialize(credentials).Contains(AuthenticatedGitFixture.Credential, StringComparison.Ordinal));
        var oversized = await Invoke(service, fixture, "publish", Publication(fixture.InitialRevision) with { Changes = [new("shared/large.bin", new string('A', 2 * 1024 * 1024))] });
        Assert.AreEqual("git_changeset_too_large", oversized.ErrorCode);
        Assert.AreEqual(fixture.InitialRevision, (await fixture.GitAsync(fixture.RepositoryPath, "rev-parse", "main")).Trim());
    }

    [TestMethod]
    public async Task NativeGitCancellationAndMissingReceiptDoNotPublishAndInvalidJournalCannotReissue()
    {
        await using var fixture = await AuthenticatedGitFixture.CreateAsync(); using var service = Service(fixture);
        var input = Publication(fixture.InitialRevision);
        var missing = await Invoke(service, fixture, "publish", input, mode: "recover"); Assert.AreEqual("Missing", missing.Status);
        using var cancellation = new CancellationTokenSource(); await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Invoke(service, fixture, "publish", input, token: cancellation.Token));
        var published = await Invoke(service, fixture, "publish", input); Assert.AreEqual("Succeeded", published.Status, published.ErrorCode);
        var path = Path.Combine(fixture.Root, "journal", Key('a') + ".json");
        var stored = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!; stored["Phase"] = "unknown";
        await File.WriteAllTextAsync(path, stored.ToJsonString());
        var replay = await Invoke(service, fixture, "publish", input); Assert.AreEqual("git_journal_invalid", replay.ErrorCode);
        Assert.AreEqual("2", (await fixture.GitAsync(fixture.RepositoryPath, "rev-list", "--count", "main")).Trim());
    }
    [TestMethod]
    public async Task NativeGitRecoveryRejectsACandidateThatDoesNotProveTheOriginalPublicationIdentity()
    {
        await using var fixture = await AuthenticatedGitFixture.CreateAsync(); using var service = Service(fixture);
        var input = Publication(fixture.InitialRevision);
        var published = await Invoke(service, fixture, "publish", input); Assert.AreEqual("Succeeded", published.Status, published.ErrorCode);
        var path = Path.Combine(fixture.Root, "journal", Key('a') + ".json");
        var stored = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        stored["Candidate"] = fixture.InitialRevision; stored["Result"] = null; await File.WriteAllTextAsync(path, stored.ToJsonString());
        var replay = await Invoke(service, fixture, "publish", input, mode: "recover");
        Assert.AreEqual("Uncertain", replay.Status); Assert.AreEqual("git_publication_outcome_uncertain", replay.ErrorCode);
        Assert.AreEqual(published.Revision, (await fixture.GitAsync(fixture.RepositoryPath, "rev-parse", "main")).Trim());
    }

    [TestMethod]
    public async Task DefaultDisabledOperationsNeedNoGitExecutableRemoteAccessOrProviderState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-disabled-git-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new GitOperationsOptions { StateDirectory = directory });
        using var service = new GitRepositoryOperations(Options.Create(new GitSourceProviderOptions { GitExecutable = "not-an-installed-git-executable", TemporaryDirectory = directory }),
            options, new(options), TimeProvider.System);
        var result = await service.InvokeAsync("resolve", "execute", Key('a'), Key('b'), new("refs/heads/main"),
            "https://example.invalid/repository.git", "None", "", "", ["refs/heads/main"], ["shared"], default);
        Assert.AreEqual("Rejected", result.Status); Assert.AreEqual("git_operations_disabled", result.ErrorCode);
        Assert.IsFalse(Directory.Exists(directory));
    }

}
