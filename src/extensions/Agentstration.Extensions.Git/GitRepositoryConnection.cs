using System.Text;

namespace Agentstration.Extensions.Git;

internal sealed class GitRepositoryConnection : IAsyncDisposable
{
    internal const int MaximumBytes = 1024 * 1024;
    private readonly GitSourceProviderOptions source;
    private readonly GitCommandRunner runner;
    private readonly Dictionary<string, string> environment = new(StringComparer.Ordinal);
    private readonly string[] references;
    private readonly string[] paths;
    private readonly string directory;
    private bool initialized;
    internal string Repository { get; }

    internal GitRepositoryConnection(GitSourceProviderOptions source, GitOperationsOptions options, string repository,
        string authentication, string username, string credential, IReadOnlyList<string> allowedRefs, IReadOnlyList<string> allowedPaths)
    {
        this.source = source;
        if (!options.Enabled) throw new GitOperationException("git_operations_disabled");
        Repository = CanonicalRepository(repository, options, source.AllowLocalRepositories);
        if (!options.AllowedRepositories.Any(value => CanonicalRepository(value, options, source.AllowLocalRepositories) == Repository))
            throw new GitOperationException("git_repository_denied");
        if (allowedRefs is not { Count: >= 1 and <= 16 } || allowedRefs.Any(value => value is null || value != "refs/heads/" && !ValidRef(value.TrimEnd('/')))
            || allowedPaths is not { Count: >= 1 and <= 16 } || allowedPaths.Any(value => value is null || value != "." && !ValidPath(value.TrimEnd('/')))
            || authentication is not ("None" or "Basic" or "Bearer") || username is null || credential is null || username.Length > 128 || username.Any(character => char.IsControl(character) || character == ':')
            || credential.Length > 8192 || credential.Any(char.IsControl)
            || authentication != "None" && string.IsNullOrWhiteSpace(credential)) throw new GitOperationException("git_connection_invalid");
        references = allowedRefs.Select(value => value.TrimEnd('/')).ToArray();
        paths = allowedPaths.Select(value => value.TrimEnd('/')).ToArray();
        if (authentication != "None")
        {
            if (!Uri.TryCreate(Repository, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) throw new GitOperationException("git_authentication_invalid");
            var header = authentication == "Bearer" ? "Bearer " + credential : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + credential));
            environment["GIT_CONFIG_COUNT"] = "1";
            environment["GIT_CONFIG_KEY_0"] = $"http.{Repository}.extraHeader";
            environment["GIT_CONFIG_VALUE_0"] = "Authorization: " + header;
        }
        var root = source.TemporaryDirectory is { Length: > 0 } temporary ? Path.GetFullPath(temporary) : Path.Combine(Path.GetTempPath(), "agentstration-git-operations");
        directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        runner = new(source, options.AllowHttpLoopback, protectRemote: true);
    }

    internal static bool ValidOptions(GitOperationsOptions options, GitSourceProviderOptions source)
    {
        if (!options.Enabled) return true;
        if (options.AllowedRepositories is not { Length: >= 1 and <= 16 } || string.IsNullOrWhiteSpace(options.StateDirectory)
            || options.TimeoutSeconds is < 1 or > 120) return false;
        try
        {
            foreach (var repository in options.AllowedRepositories) CanonicalRepository(repository, options, source.AllowLocalRepositories);
            return true;
        }
        catch (GitOperationException) { return false; }
    }

    internal static string CanonicalRepository(string value, GitOperationsOptions options, bool allowLocal)
    {
        if (value is not { Length: > 0 and <= 2048 } || value.Any(char.IsControl)) throw new GitOperationException("git_repository_invalid");
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new GitOperationException("git_repository_invalid");
            if (uri.Scheme == "https" || options.AllowHttpLoopback && uri.Scheme == "http" && uri.IsLoopback) return uri.AbsoluteUri.TrimEnd('/');
            if (uri.IsFile && allowLocal) return new Uri(Path.GetFullPath(uri.LocalPath)).AbsoluteUri.TrimEnd('/');
        }
        if (allowLocal && Path.IsPathFullyQualified(value)) return new Uri(Path.GetFullPath(value)).AbsoluteUri.TrimEnd('/');
        throw new GitOperationException("git_repository_invalid");
    }

    internal void Validate(GitRepositoryInput input, string operation)
    {
        if (input is null || !ValidRef(input.RefName) || !references.Any(prefix => input.RefName == prefix || input.RefName.StartsWith(prefix + "/", StringComparison.Ordinal)))
            throw new GitOperationException("git_ref_denied");
        if (input.Paths is { Count: > 64 } || input.Paths?.Any(path => !Permits(path)) == true
            || input.Paths is not null && input.Paths.Distinct(StringComparer.Ordinal).Count() != input.Paths.Count)
            throw new GitOperationException("git_path_denied");
        if (operation is "read" or "diff" && !Revision(input.Revision) || operation == "diff" && !Revision(input.BaseRevision)) throw new GitOperationException("git_revision_invalid");
        if (operation == "read" && input.Paths is not { Count: >= 1 }) throw new GitOperationException("git_paths_required");
        if (operation != "publish")
        {
            if (input.Changes is not null || input.Message is not null || input.ExpectedRefRevision is not null) throw new GitOperationException("git_input_invalid");
            return;
        }
        if (input.Revision is not null || input.Paths is not null || input.BaseRevision is not null && !Revision(input.BaseRevision)
            || input.ExpectedRefRevision is not null && (!Revision(input.ExpectedRefRevision) || input.ExpectedRefRevision != input.BaseRevision)
            || input.Changes is not { Count: >= 1 and <= 64 } || input.Changes.Any(change => change is null || !Permits(change.Path))
            || input.Changes.Select(change => change.Path).Distinct(StringComparer.Ordinal).Count() != input.Changes.Count
            || string.IsNullOrWhiteSpace(input.Message) || input.Message.Length > 512 || input.Message.Any(char.IsControl)) throw new GitOperationException("git_changeset_invalid");
        var bytes = 0;
        foreach (var change in input.Changes)
        {
            if (change.ContentBase64 is null) continue;
            if (change.ContentBase64.Length > 4 * ((MaximumBytes + 2) / 3)) throw new GitOperationException("git_changeset_too_large");
            try { bytes += Convert.FromBase64String(change.ContentBase64).Length; }
            catch (FormatException) { throw new GitOperationException("git_changeset_invalid"); }
            if (bytes > MaximumBytes) throw new GitOperationException("git_changeset_too_large");
        }
    }

    internal async Task<string?> ResolveAsync(string reference, CancellationToken token)
    {
        var result = await RunAsync(["ls-remote", "--exit-code", "--refs", Repository, reference], token, permitFailure: true);
        if (result.ExitCode == 2 && result.Output.Length == 0) return null;
        if (result.ExitCode != 0) throw new GitOperationException("git_remote_unavailable");
        var fields = result.Text.Trim().Split('\t');
        if (fields is not [var revision, var name] || name != reference || !Revision(revision)) throw new GitOperationException("git_remote_response_invalid");
        return revision;
    }

    internal async Task FetchAsync(string revision, CancellationToken token)
    {
        await InitializeAsync(token);
        await RunAsync(["fetch", "--quiet", "--no-tags", "--no-recurse-submodules", Repository, revision], token);
        var fetched = (await RunAsync(["rev-parse", "--verify", "FETCH_HEAD^{commit}"], token)).Text.Trim();
        if (fetched != revision) throw new GitOperationException("git_revision_changed");
    }

    internal async Task<bool> AncestorAsync(string older, string newer, CancellationToken token) =>
        (await RunAsync(["merge-base", "--is-ancestor", older, newer], token, permitFailure: true)).ExitCode == 0;

    internal async Task<IReadOnlyList<GitRepositoryFile>> ReadAsync(GitRepositoryInput input, CancellationToken token)
    {
        var files = new List<GitRepositoryFile>(); var bytes = 0;
        foreach (var path in input.Paths!.Order(StringComparer.Ordinal))
        {
            var entry = await EntryAsync(input.Revision!, path, token);
            if (entry is null) throw new GitOperationException("git_file_not_found");
            if (entry.Value.Mode is not ("100644" or "100755")) throw new GitOperationException("git_file_type_denied");
            var blob = await RunAsync(["cat-file", "blob", entry.Value.Revision], token, maximumOutput: MaximumBytes);
            bytes += blob.Output.Length; if (bytes > MaximumBytes) throw new GitOperationException("git_output_too_large");
            files.Add(new(path, entry.Value.Revision, Convert.ToBase64String(blob.Output), entry.Value.Mode == "100755"));
        }
        return files;
    }

    internal async Task<string> DiffAsync(GitRepositoryInput input, CancellationToken token)
    {
        var arguments = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--full-index", "--binary", input.BaseRevision!, input.Revision!, "--" };
        arguments.AddRange(input.Paths ?? paths);
        return Convert.ToBase64String((await RunAsync(arguments, token, maximumOutput: MaximumBytes)).Output);
    }

    internal async Task<string> BuildAsync(GitRepositoryInput input, long timestamp, string key, string digest, CancellationToken token)
    {
        await InitializeAsync(token);
        if (input.BaseRevision is { } baseline) await RunAsync(["read-tree", baseline], token);
        else await RunAsync(["read-tree", "--empty"], token);
        foreach (var change in input.Changes!.OrderBy(change => change.Path, StringComparer.Ordinal))
        {
            if (change.ContentBase64 is null) { await RunAsync(["update-index", "--force-remove", "--", change.Path], token); continue; }
            var oid = (await RunAsync(["hash-object", "-w", "--stdin", "--no-filters"], token, Convert.FromBase64String(change.ContentBase64))).Text.Trim();
            var oldMode = input.BaseRevision is { } parent ? (await EntryAsync(parent, change.Path, token))?.Mode : null;
            var mode = change.Executable switch { true => "100755", false => "100644", null => oldMode == "100755" ? "100755" : "100644" };
            await RunAsync(["update-index", "--add", "--cacheinfo", mode + "," + oid + "," + change.Path], token);
        }
        var tree = (await RunAsync(["write-tree"], token)).Text.Trim();
        var date = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) + " +0000";
        var identity = new Dictionary<string, string>(environment, StringComparer.Ordinal)
        {
            ["GIT_AUTHOR_NAME"] = "Agentstration", ["GIT_COMMITTER_NAME"] = "Agentstration",
            ["GIT_AUTHOR_EMAIL"] = "operations@agentstration.invalid", ["GIT_COMMITTER_EMAIL"] = "operations@agentstration.invalid",
            ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date
        };
        var arguments = new List<string> { "commit-tree", tree };
        if (input.BaseRevision is { } parentRevision) arguments.AddRange(["-p", parentRevision]);
        var message = Encoding.UTF8.GetBytes(input.Message + "\n\nAgentstration-Operation: " + key + "\nAgentstration-Intent: " + digest + "\n");
        var commit = (await runner.RunAsync(directory, arguments, source.MaximumRepositoryBytes, token, input: message, environment: identity)).Text.Trim();
        if (!Revision(commit)) throw new GitOperationException("git_revision_invalid");
        return commit;
    }

    internal Task<GitCommandResult> PushAsync(GitRepositoryInput input, string candidate, CancellationToken token) => RunAsync(
        ["push", "--porcelain", "--force-with-lease=" + input.RefName + ":" + (input.ExpectedRefRevision ?? ""), Repository, candidate + ":" + input.RefName], token, permitFailure: true);

    internal async Task<bool> VerifyPublicationAsync(GitRepositoryInput input, string candidate, string key, string digest, CancellationToken token)
    {
        var metadata = (await RunAsync(["show", "--no-patch", "--format=%P%n%B", candidate], token)).Text.Split('\n');
        return metadata.Length >= 4 && metadata[0] == (input.BaseRevision ?? "")
            && metadata.Contains("Agentstration-Operation: " + key, StringComparer.Ordinal)
            && metadata.Contains("Agentstration-Intent: " + digest, StringComparer.Ordinal);
    }

    private async Task<(string Mode, string Revision)?> EntryAsync(string revision, string path, CancellationToken token)
    {
        var entry = (await RunAsync(["ls-tree", "-z", revision, "--", path], token)).Text;
        if (entry.Length == 0) return null;
        var elements = entry.TrimEnd('\0').Split('\t');
        if (elements.Length != 2 || elements[1] != path) throw new GitOperationException("git_file_type_denied");
        var properties = elements[0].Split(' ');
        return properties.Length == 3 && Revision(properties[2]) ? (properties[0], properties[2]) : throw new GitOperationException("git_response_invalid");
    }
    private async Task InitializeAsync(CancellationToken token)
    {
        if (initialized) return;
        await RunAsync(["init", "--bare", "--quiet", "--object-format=sha1", "."], token); initialized = true;
    }
    private Task<GitCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken token, byte[]? input = null, int maximumOutput = 16 * 1024, bool permitFailure = false) =>
        runner.RunAsync(directory, arguments, source.MaximumRepositoryBytes, token, input: input, environment: environment,
            maximumOutputBytes: maximumOutput, permitFailure: permitFailure);
    private bool Permits(string? path) => ValidPath(path) && paths.Any(prefix => prefix == "." || path == prefix || path!.StartsWith(prefix + "/", StringComparison.Ordinal));
    internal static bool Revision(string? value) => value is { Length: 40 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool ValidPath(string? value) => value is { Length: > 0 and <= 512 } && !value.Any(char.IsControl) && !value.Contains('\\') && !value.Contains(':')
        && value.Split('/').All(segment => segment is not ("" or "." or "..") && !segment.Equals(".git", StringComparison.OrdinalIgnoreCase));
    internal static bool ValidRef(string? value) => value is { Length: > 11 and <= 256 } && value.StartsWith("refs/heads/", StringComparison.Ordinal)
        && !value.EndsWith('/') && !value.EndsWith('.') && !value.Split('/').Any(segment => segment.Length == 0 || segment.StartsWith('.') || segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        && !value.Contains("..", StringComparison.Ordinal) && !value.Contains("@{", StringComparison.Ordinal)
        && !value.Any(character => char.IsControl(character) || " ~^:?*[\\".Contains(character));
    public ValueTask DisposeAsync()
    {
        environment.Clear();
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }
}
