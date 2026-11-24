extern alias GitHubExtension;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using GitHub = GitHubExtension::Agentstration.Extensions.GitHub;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed partial class GitHubProviderTests
{
    private const string Repository = "fixture/repository";
    private const string Credential = "public-disposable-synthetic-github-fixture";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly GitHub.AllowedGitHubRepository[] Allowed = [new(Repository, "42"), new("fixture/second", "43")];
    private static readonly string HeadRevision = new('a', 40);
    private static string Key(char value) => new(value, 64);

    [TestMethod]
    public async Task GitHubIssueLostResponseRecoversAfterProviderRestartWithoutPublishingChildren()
    {
        using var fixture = new Fixture(); fixture.Native.LoseIssueResponse = true;
        var command = new GitHub.GitHubTaskInput("Publish", Repository, Title: "Composite issue", Body: "Internal children", Labels: ["agentstration:planned"]);
        var lost = await fixture.Issues().InvokeAsync("execute", Key('a'), Key('b'), command, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        Assert.AreEqual("Uncertain", lost.Status); Assert.AreEqual(1, fixture.Native.Issues.Count);
        var recovered = await fixture.Issues().InvokeAsync("recover", Key('a'), Key('b'), command, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        Assert.AreEqual("Succeeded", recovered.Status, recovered.ErrorCode); Assert.AreEqual("1", recovered.Task!.TaskId);
        Assert.AreEqual(1, fixture.Native.Issues.Count); Assert.AreEqual(1, fixture.Native.IssuePosts);
        var replay = await fixture.Issues().InvokeAsync("execute", Key('a'), Key('b'), command, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        Assert.AreEqual(JsonSerializer.Serialize(recovered), JsonSerializer.Serialize(replay)); Assert.AreEqual(1, fixture.Native.IssuePosts);
        var conflict = await fixture.Issues().InvokeAsync("execute", Key('a'), Key('c'), command with { Title = "Other intent" }, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        Assert.AreEqual("github_operation_conflict", conflict.ErrorCode); Assert.AreEqual(1, fixture.Native.IssuePosts);
        foreach (var path in Directory.GetFiles(fixture.Directory, "*.json")) Assert.IsFalse(File.ReadAllText(path).Contains(Credential, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GitHubManagedLabelAndNativeRelationshipsPreserveUnrelatedDataAndRejectStaleContentEdits()
    {
        using var fixture = new Fixture(); fixture.Native.AddIssue("Child"); fixture.Native.AddIssue("Parent"); fixture.Native.AddIssue("Prerequisite");
        var readInput = new GitHub.GitHubTaskInput("Read", Repository, "1");
        var read = await fixture.Issues().InvokeAsync("execute", Key('a'), Key('b'), readInput, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        Assert.AreEqual("Succeeded", read.Status, read.ErrorCode);
        var update = new GitHub.GitHubTaskInput("Update", Repository, "1", Labels: ["agentstration:planned"],
            Relations: [new("parent", Repository, "2"), new("blocked-by", Repository, "3")], ExpectedRevision: read.Task!.Revision);
        var result = await fixture.Issues().InvokeAsync("execute", Key('b'), Key('c'), update, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        Assert.AreEqual("Succeeded", result.Status, result.ErrorCode);
        CollectionAssert.Contains(result.Task!.Labels.ToArray(), "unrelated"); CollectionAssert.Contains(result.Task.Labels.ToArray(), "agentstration:planned");
        CollectionAssert.Contains(result.Task.Relations.ToArray(), new GitHub.GitHubRelation("parent", Repository, "2"));
        CollectionAssert.Contains(result.Task.Relations.ToArray(), new GitHub.GitHubRelation("blocked-by", Repository, "3"));
        var parent = await fixture.Issues().InvokeAsync("execute", Key('c'), Key('d'), readInput with { TaskId = "2" }, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        CollectionAssert.Contains(parent.Task!.Relations.ToArray(), new GitHub.GitHubRelation("child", Repository, "1"));
        var stale = await fixture.Issues().InvokeAsync("execute", Key('d'), Key('e'), update, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        Assert.AreEqual("Rejected", stale.Status); Assert.AreEqual("github_issue_revision_changed", stale.ErrorCode);
        var content = await fixture.Issues().InvokeAsync("execute", Key('e'), Key('f'), update with { Title = "Unsafe edit" }, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        Assert.AreEqual("github_conditional_update_unsupported", content.ErrorCode); Assert.AreEqual(1, fixture.Native.LabelPosts);
        fixture.Native.IncompleteRelations = true;
        var incomplete = await fixture.Issues().InvokeAsync("execute", Key('f'), Key('a'), readInput, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        Assert.AreEqual("github_observation_incomplete", incomplete.ErrorCode);
    }

    [TestMethod]
    public async Task GitHubRepositoryIdentityCredentialsAndOriginFailClosed()
    {
        using var fixture = new Fixture(); var request = JsonSerializer.SerializeToElement(new { repositories = new[] { Repository, "fixture/second" } });
        var result = await fixture.Coding().InvokeAsync("repository.resolve", "execute", Key('a'), Key('b'), request, "https://api.github.com", Credential, Allowed, default);
        Assert.AreEqual("Succeeded", result.Status, result.ErrorCode); Assert.HasCount(2, result.Repositories!); Assert.AreEqual("42", result.Repositories![0].NativeId);
        var denied = await fixture.Coding().InvokeAsync("repository.resolve", "execute", Key('a'), Key('b'), request, "https://api.github.com", "denied", Allowed, default);
        Assert.AreEqual("Rejected", denied.Status); Assert.AreEqual("github_credentials_denied", denied.ErrorCode);
        var changed = await fixture.Coding().InvokeAsync("repository.resolve", "execute", Key('a'), Key('b'), request, "https://api.github.com", Credential,
            [new(Repository, "999"), Allowed[1]], default);
        Assert.AreEqual("github_repository_identity_changed", changed.ErrorCode);
        var origin = await fixture.Coding().InvokeAsync("repository.resolve", "execute", Key('a'), Key('b'), request, "https://attacker.example", Credential, Allowed, default);
        Assert.AreEqual("github_connection_invalid", origin.ErrorCode);
        var foreign = await fixture.Coding().InvokeAsync("repository.resolve", "execute", Key('a'), Key('b'),
            JsonSerializer.SerializeToElement(new { repositories = new[] { "foreign/repo" } }), "https://api.github.com", Credential, Allowed, default);
        Assert.AreEqual("github_repository_denied", foreign.ErrorCode);
    }

    [TestMethod]
    public async Task GitHubPullRequestLostResponseRecoversOriginalNativeArtifactAndNeverDuplicatesAfterUnprovableAbsence()
    {
        using var fixture = new Fixture(); fixture.Native.LosePullResponse = true;
        var input = new GitHub.GitHubPullRequestInput(Repository, Title: "Deliver issue", Head: "agentstration/task", Base: "main", ExpectedHeadRevision: HeadRevision);
        var request = JsonSerializer.SerializeToElement(input, Json);
        var lost = await fixture.Coding().InvokeAsync("pull-request.create", "execute", Key('a'), Key('b'), request, "https://api.github.com", Credential, Allowed, default);
        Assert.AreEqual("Uncertain", lost.Status); Assert.AreEqual(1, fixture.Native.PullPosts);
        fixture.Native.HidePulls = true;
        var absent = await fixture.Coding().InvokeAsync("pull-request.create", "execute", Key('a'), Key('b'), request, "https://api.github.com", Credential, Allowed, default);
        Assert.AreEqual("Uncertain", absent.Status); Assert.AreEqual(1, fixture.Native.PullPosts);
        fixture.Native.HidePulls = false;
        var recovered = await fixture.Coding().InvokeAsync("pull-request.create", "recover", Key('a'), Key('b'), request, "https://api.github.com", Credential, Allowed, default);
        Assert.AreEqual("Succeeded", recovered.Status, recovered.ErrorCode); Assert.AreEqual(HeadRevision, recovered.PullRequest!.HeadRevision);
        var read = await fixture.Coding().InvokeAsync("pull-request.read", "execute", Key('b'), Key('c'),
            JsonSerializer.SerializeToElement(new GitHub.GitHubPullRequestInput(Repository, recovered.PullRequest.Number), Json), "https://api.github.com", Credential, Allowed, default);
        Assert.AreEqual(recovered.PullRequest.NativeId, read.PullRequest!.NativeId); Assert.AreEqual(1, fixture.Native.PullPosts);
    }

    [TestMethod]
    public async Task GitHubConcurrentPublicationAndCancellationAfterEffectAreRetrySafe()
    {
        using var fixture = new Fixture(); var input = new GitHub.GitHubTaskInput("Publish", Repository, Title: "One publication");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Issues().InvokeAsync("execute", Key('a'), Key('b'), input,
            "https://api.github.com", Credential, Allowed, "agentstration:", default)));
        Assert.IsTrue(results.All(value => value.Status == "Succeeded")); Assert.AreEqual(1, fixture.Native.IssuePosts);
        using var cancellation = new CancellationTokenSource(); fixture.Native.CancelAfterIssue = cancellation;
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Issues().InvokeAsync("execute", Key('b'), Key('c'), input,
            "https://api.github.com", Credential, Allowed, "agentstration:", cancellation.Token));
        var recovered = await fixture.Issues().InvokeAsync("recover", Key('b'), Key('c'), input, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        Assert.AreEqual("Succeeded", recovered.Status, recovered.ErrorCode); Assert.AreEqual(2, fixture.Native.IssuePosts);
    }

    [TestMethod]
    public async Task GitHubInvalidPersistedPhaseNeverAuthorizesAnotherRemoteWrite()
    {
        using var fixture = new Fixture(); fixture.Native.LoseIssueResponse = true;
        var input = new GitHub.GitHubTaskInput("Publish", Repository, Title: "One uncertain issue");
        var first = await fixture.Issues().InvokeAsync("execute", Key('a'), Key('b'), input, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        Assert.AreEqual("Uncertain", first.Status);
        var path = Path.Combine(fixture.Directory, Key('a') + ".json");
        var persisted = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        persisted["Phase"] = "unknown"; await File.WriteAllTextAsync(path, persisted.ToJsonString());
        var replay = await fixture.Issues().InvokeAsync("execute", Key('a'), Key('b'), input, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        Assert.AreEqual("Uncertain", replay.Status); Assert.AreEqual("github_journal_invalid", replay.ErrorCode);
        Assert.AreEqual(1, fixture.Native.IssuePosts);
    }

    [TestMethod]
    public async Task GitHubProviderRechecksNativeIssueAuthorityBeforeReturningATerminalReceipt()
    {
        using var fixture = new Fixture();
        var input = new GitHub.GitHubTaskInput("Publish", Repository, Title: "Protected issue", Body: "Private content");
        var first = await fixture.Issues().InvokeAsync("execute", Key('a'), Key('b'), input, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        Assert.AreEqual("Succeeded", first.Status);
        fixture.Native.DenyIssueReads = true;
        var replay = await fixture.Issues().InvokeAsync("execute", Key('a'), Key('b'), input, "https://api.github.com", Credential, Allowed, "agentstration:", default);
        Assert.AreEqual("Uncertain", replay.Status); Assert.AreEqual("github_credentials_denied", replay.ErrorCode); Assert.IsNull(replay.Task);
        Assert.AreEqual(1, fixture.Native.IssuePosts);
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "agentstration-github-tests-" + Guid.NewGuid().ToString("N"));
        public NativeGitHub Native { get; } = new();
        private readonly HttpClient http;
        public Fixture() { http = new(Native); }
        public GitHub.GitHubProviderOptions Options => new() { StateDirectory = Directory };
        public GitHub.GitHubIssueOperations Issues() => new(new(http, Microsoft.Extensions.Options.Options.Create(Options)), new(Microsoft.Extensions.Options.Options.Create(Options)));
        public GitHub.GitHubCodingOperations Coding() => new(new(http, Microsoft.Extensions.Options.Options.Create(Options)), new(Microsoft.Extensions.Options.Options.Create(Options)));
        public void Dispose() { http.Dispose(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }

    private sealed class NativeGitHub : HttpMessageHandler
    {
        public Dictionary<int, NativeIssue> Issues { get; } = [];
        private readonly Dictionary<int, JsonElement> pulls = [];
        public bool LoseIssueResponse { get; set; }
        public bool LosePullResponse { get; set; }
        public bool HidePulls { get; set; }
        public bool IncompleteRelations { get; set; }
        public bool DenyIssueReads { get; set; }
        public CancellationTokenSource? CancelAfterIssue { get; set; }
        public int IssuePosts { get; private set; }
        public int PullPosts { get; private set; }
        public int LabelPosts { get; private set; }
        public void AddIssue(string title) => Issues.Add(Issues.Count + 1, new(title, "Fixture body", ["unrelated"]));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.Headers.Authorization?.Parameter != Credential) return new(HttpStatusCode.Unauthorized);
            var parts = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var body = request.Content is null ? default : await request.Content.ReadFromJsonAsync<JsonElement>(token);
            object? output = null;
            if (parts.Length == 3) output = new { id = parts[2] == "second" ? 43 : 42, full_name = parts[1] + "/" + parts[2], default_branch = "main", clone_url = "https://github.com/fixture/repository.git", html_url = "https://github.com/fixture/repository" };
            else if (parts[3] == "commits") output = new { sha = HeadRevision };
            else if (parts[3] == "issues")
            {
                if (DenyIssueReads && request.Method == HttpMethod.Get) return new(HttpStatusCode.Forbidden);
                if (parts.Length == 4 && request.Method == HttpMethod.Post)
                {
                    IssuePosts++; var number = Issues.Count + 1;
                    Issues.Add(number, new(body.GetProperty("title").GetString()!, body.GetProperty("body").GetString()!,
                        body.TryGetProperty("labels", out var labels) ? labels.EnumerateArray().Select(value => value.GetString()!).ToList() : []));
                    if (LoseIssueResponse) { LoseIssueResponse = false; throw new HttpRequestException("Synthetic response loss"); }
                    CancelAfterIssue?.Cancel(); token.ThrowIfCancellationRequested(); output = Issue(number);
                }
                else if (parts.Length == 4) output = Issues.Keys.Select(Issue).ToArray();
                else
                {
                    var number = int.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture); var issue = Issues[number];
                    if (parts.Length == 5) output = Issue(number);
                    else if (parts[5] == "labels")
                    { LabelPosts++; issue.Labels.AddRange(body.GetProperty("labels").EnumerateArray().Select(value => value.GetString()!).Except(issue.Labels)); output = issue.Labels.Select(name => new { name }).ToArray(); }
                    else if (parts[5] == "parent")
                    { if (issue.Parent is not { } parent) return new(HttpStatusCode.NotFound); output = Issue(parent); }
                    else if (parts[5] == "sub_issues")
                    {
                        if (request.Method == HttpMethod.Post) Issues[body.GetProperty("sub_issue_id").GetInt32() - 100].Parent = number;
                        output = Issues.Where(value => value.Value.Parent == number).Select(value => Issue(value.Key)).ToArray();
                    }
                    else if (parts[5] == "dependencies")
                    {
                        if (request.Method == HttpMethod.Post) issue.Dependencies.Add(body.GetProperty("issue_id").GetInt32() - 100);
                        output = IncompleteRelations ? Array.Empty<JsonElement>() : issue.Dependencies.Select(Issue).ToArray();
                    }
                }
            }
            else if (parts[3] == "pulls")
            {
                if (request.Method == HttpMethod.Post)
                {
                    PullPosts++; var number = 1000 + PullPosts;
                    var pr = JsonSerializer.SerializeToElement(new { id = number + 100, number, html_url = $"https://github.com/{Repository}/pull/{number}",
                        title = body.GetProperty("title").GetString(), body = body.GetProperty("body").GetString(), state = "open", draft = body.GetProperty("draft").GetBoolean(), merged = false,
                        head = new { @ref = body.GetProperty("head").GetString(), sha = HeadRevision }, @base = new { @ref = body.GetProperty("base").GetString(), sha = new string('b', 40) }, updated_at = "2026-11-24T00:00:00Z" });
                    pulls.Add(number, pr); if (LosePullResponse) { LosePullResponse = false; throw new HttpRequestException("Synthetic response loss"); } output = pr;
                }
                else output = parts.Length == 4 ? (HidePulls ? Array.Empty<JsonElement>() : pulls.Values.ToArray()) : pulls[int.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture)];
            }
            return output is null ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = JsonContent.Create(output) };
        }
        private JsonElement Issue(int number)
        {
            var issue = Issues[number];
            return JsonSerializer.SerializeToElement(new { id = 100 + number, number, title = issue.Title, body = issue.Body, state = "open", html_url = $"https://github.com/{Repository}/issues/{number}",
                repository_url = "https://api.github.com/repos/" + Repository, labels = issue.Labels.Select(name => new { name }).ToArray(),
                sub_issues_summary = new { total = Issues.Count(value => value.Value.Parent == number), completed = 0, percent_completed = 0 },
                issue_dependencies_summary = new { blocked_by = issue.Dependencies.Count, blocking = 0, total_blocked_by = issue.Dependencies.Count, total_blocking = 0 } });
        }
    }
    private sealed class NativeIssue(string title, string body, List<string> labels)
    {
        public string Title { get; } = title; public string Body { get; } = body; public List<string> Labels { get; } = labels;
        public int? Parent { get; set; } public HashSet<int> Dependencies { get; } = [];
    }
}
