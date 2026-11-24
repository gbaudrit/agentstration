using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.Git;

public sealed partial class GitSourceProvider(IOptions<GitSourceProviderOptions> configuredOptions) : IAepSourceProvider
{
    public const string ContributionId = "git";
    private const int MaximumDiagnosticCharacters = 16 * 1024;
    private readonly GitSourceProviderOptions options = configuredOptions.Value;

    public AepSourceProviderDescriptor Descriptor { get; } = new(
        ContributionId,
        "Git",
        "Resolves an explicit public Git ref and materializes its exact commit as a bounded ZIP archive.");

    public async Task<AepSourceResolveResponse> ResolveAsync(
        AepSourceResolveRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = ParseConfiguration(request.Configuration);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.ResolveTimeoutSeconds));
        await using var workspace = CreateWorkspace();
        try
        {
            await InitializeAndFetchAsync(workspace.RepositoryPath, configuration.Repository, configuration.Reference, timeout.Token);
            var revision = await RunGitAsync(
                workspace.RepositoryPath,
                ["rev-parse", "--verify", "FETCH_HEAD^{commit}"],
                options.MaximumRepositoryBytes,
                timeout.Token);
            revision = revision.Trim();
            if (!CommitSha().IsMatch(revision))
                throw Invalid("git_revision_invalid", "Git did not resolve the configured ref to a full commit SHA.");
            return new(revision.ToLowerInvariant(), new("git-commit-sha1", revision.ToLowerInvariant()));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new AepServerException("git_resolve_timeout", "Git ref resolution exceeded its configured time limit.", 504);
        }
    }

    public async Task<AepSourceMaterializeResponse> MaterializeAsync(
        AepSourceMaterializeRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = ParseConfiguration(request.Configuration);
        if (request.Limits.MaxArchiveBytes <= 0
            || request.Limits.MaxEntries <= 0
            || request.Limits.MaxExpandedBytes <= 0
            || request.Limits.TimeoutSeconds <= 0)
            throw Invalid("invalid_limits", "All Git materialization limits must be positive.");
        if (!CommitSha().IsMatch(request.Revision))
            throw Invalid("git_revision_invalid", "Materialization requires a full 40-character Git commit SHA.");

        await using var workspace = CreateWorkspace();
        await InitializeAndFetchAsync(workspace.RepositoryPath, configuration.Repository, request.Revision, cancellationToken);
        var fetchedRevision = (await RunGitAsync(
            workspace.RepositoryPath,
            ["rev-parse", "--verify", "FETCH_HEAD^{commit}"],
            options.MaximumRepositoryBytes,
            cancellationToken)).Trim();
        if (!string.Equals(request.Revision, fetchedRevision, StringComparison.OrdinalIgnoreCase))
            throw new AepServerException("source_revision_mismatch", "Git fetched a different commit than the exact requested revision.");

        var treeish = request.Revision;
        if (configuration.RootPath is not null)
        {
            treeish = $"{request.Revision}:{configuration.RootPath}";
            var objectType = (await RunGitAsync(
                workspace.RepositoryPath,
                ["cat-file", "-t", treeish],
                options.MaximumRepositoryBytes,
                cancellationToken)).Trim();
            if (!string.Equals(objectType, "tree", StringComparison.Ordinal))
                throw Invalid("git_root_path_invalid", "The configured rootPath does not identify a directory in the resolved commit.");
        }

        await RunGitAsync(
            workspace.RepositoryPath,
            ["archive", "--format=zip", $"--output={workspace.ArchivePath}", treeish],
            options.MaximumRepositoryBytes,
            cancellationToken,
            workspace.ArchivePath,
            request.Limits.MaxArchiveBytes);

        var archiveInfo = new FileInfo(workspace.ArchivePath);
        if (!archiveInfo.Exists || archiveInfo.Length > request.Limits.MaxArchiveBytes)
            throw TooLarge("git_archive_too_large", "The Git archive exceeds the negotiated compressed size limit.");

        var (entryCount, expandedBytes) = InspectArchive(workspace.ArchivePath, request.Limits);
        var content = await File.ReadAllBytesAsync(workspace.ArchivePath, cancellationToken);
        return new(
            request.Revision.ToLowerInvariant(),
            new("application/zip", content, expandedBytes, entryCount, AepContentIntegrity.Sha256(content)));
    }

    private GitChannelConfiguration ParseConfiguration(AepVersionedOptions configuration)
    {
        var values = configuration.Values;
        var repository = RequiredString(values, "repository");
        var reference = RequiredString(values, "ref");
        var rootPath = OptionalString(values, "rootPath");
        var credentialsReference = OptionalString(values, "credentialsRef");
        if (credentialsReference is not null)
            throw Invalid("git_credentials_unsupported", "Private Git repository credentials are not supported by this provider version.");
        return new(ValidateRepository(repository), ValidateReference(reference), ValidateRootPath(rootPath));
    }

    private string ValidateRepository(string repository)
    {
        if (Uri.TryCreate(repository, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                    throw Invalid("git_repository_invalid", "Git repository URLs must not contain credentials, query values, or fragments.");
                if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                    || IPAddress.TryParse(uri.Host, out var address) && !IsPublicAddress(address))
                    throw Invalid("git_repository_invalid", "Git repository URLs must target a public HTTPS host.");
                return uri.AbsoluteUri;
            }
            if (uri.IsFile && options.AllowLocalRepositories)
                return uri.AbsoluteUri;
        }
        if (options.AllowLocalRepositories && Path.IsPathFullyQualified(repository))
            return Path.GetFullPath(repository);
        throw Invalid("git_repository_invalid", "A public absolute HTTPS Git repository URL is required.");
    }

    private static string ValidateReference(string reference)
    {
        if (CommitSha().IsMatch(reference)) return reference.ToLowerInvariant();
        if ((!reference.StartsWith("refs/heads/", StringComparison.Ordinal)
                && !reference.StartsWith("refs/tags/", StringComparison.Ordinal))
            || reference.EndsWith('/')
            || reference.EndsWith('.')
            || reference.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("..", StringComparison.Ordinal)
            || reference.Contains("//", StringComparison.Ordinal)
            || reference.Contains("@{", StringComparison.Ordinal)
            || reference.Any(character => char.IsControl(character) || " ~^:?*[\\".Contains(character)))
            throw Invalid("git_ref_invalid", "Git ref must be refs/heads/*, refs/tags/*, or a full 40-character commit SHA.");
        return reference;
    }

    private static string? ValidateRootPath(string? rootPath)
    {
        if (rootPath is null) return null;
        var normalized = rootPath.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || Path.IsPathFullyQualified(rootPath)
            || normalized.Split('/').Any(segment => segment is "" or "." or "..")
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.Any(char.IsControl))
            throw Invalid("git_root_path_invalid", "Git rootPath must identify a descendant directory.");
        return normalized;
    }

    private async Task InitializeAndFetchAsync(
        string repositoryPath,
        string remoteRepository,
        string reference,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(repositoryPath);
        await RunGitAsync(repositoryPath, ["init", "--bare", "."], options.MaximumRepositoryBytes, cancellationToken);
        await RunGitAsync(
            repositoryPath,
            ["fetch", "--quiet", "--no-tags", "--depth=1", remoteRepository, reference],
            options.MaximumRepositoryBytes,
            cancellationToken);
    }

    private async Task<string> RunGitAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        long maximumRepositoryBytes,
        CancellationToken cancellationToken,
        string? boundedFile = null,
        long maximumFileBytes = long.MaxValue)
    {
        ValidateProviderOptions();
        var startInfo = new ProcessStartInfo
        {
            FileName = options.GitExecutable,
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";
        var hooksPath = Path.Combine(repositoryPath, ".disabled-hooks");
        var globalConfigPath = Path.Combine(repositoryPath, ".empty-gitconfig");
        Directory.CreateDirectory(hooksPath);
        File.WriteAllText(globalConfigPath, string.Empty);
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = globalConfigPath;
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("credential.helper=");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"core.hooksPath={hooksPath}");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(options.AllowLocalRepositories ? "protocol.file.allow=always" : "protocol.file.allow=never");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("protocol.ext.allow=never");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("submodule.recurse=false");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The Git process could not be started.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new AepServerException("git_unavailable", "The configured Git executable is unavailable.", 502, exception);
        }

        var standardOutput = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var standardError = ReadBoundedAsync(process.StandardError, cancellationToken);
        try
        {
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DirectorySize(repositoryPath) > maximumRepositoryBytes)
                    throw TooLarge("git_repository_too_large", "The fetched Git repository exceeds the configured size limit.");
                if (boundedFile is not null && File.Exists(boundedFile) && new FileInfo(boundedFile).Length > maximumFileBytes)
                    throw TooLarge("git_archive_too_large", "The Git archive exceeds the negotiated compressed size limit.");
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
            throw new AepServerException("git_command_failed", "Git could not resolve or materialize the requested content.", 422,
                new InvalidOperationException(TrimDiagnostic(error)));
        if (DirectorySize(repositoryPath) > maximumRepositoryBytes)
            throw TooLarge("git_repository_too_large", "The fetched Git repository exceeds the configured size limit.");
        return output;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        var result = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return result.ToString();
            if (result.Length < MaximumDiagnosticCharacters)
                result.Append(buffer, 0, Math.Min(read, MaximumDiagnosticCharacters - result.Length));
        }
    }

    private static (int EntryCount, long ExpandedBytes) InspectArchive(
        string archivePath,
        AepSourceMaterializationLimits limits)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > limits.MaxEntries)
            throw TooLarge("git_archive_too_many_entries", "The Git archive exceeds the negotiated entry limit.");
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            ValidateArchiveEntry(entry.FullName);
            if (entry.Length > limits.MaxExpandedBytes - expandedBytes)
                throw TooLarge("git_archive_expanded_too_large", "The Git archive exceeds the negotiated expanded size limit.");
            expandedBytes += entry.Length;
        }
        return (archive.Entries.Count, expandedBytes);
    }

    private static void ValidateArchiveEntry(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/')
            || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..")
            || normalized.Contains(':', StringComparison.Ordinal))
            throw Invalid("git_archive_entry_invalid", "Git produced an archive containing an unsafe entry path.");
    }

    private GitWorkspace CreateWorkspace()
    {
        ValidateProviderOptions();
        var root = string.IsNullOrWhiteSpace(options.TemporaryDirectory)
            ? Path.Combine(Path.GetTempPath(), "agentstration-git-source")
            : Path.GetFullPath(options.TemporaryDirectory);
        return new(Path.Combine(root, Guid.NewGuid().ToString("N")));
    }

    private void ValidateProviderOptions()
    {
        if (string.IsNullOrWhiteSpace(options.GitExecutable)
            || options.MaximumRepositoryBytes <= 0
            || options.ResolveTimeoutSeconds <= 0)
            throw new InvalidOperationException("Git Source Provider options are invalid.");
    }

    private static long DirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try { return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length); }
        catch (DirectoryNotFoundException) { return 0; }
        catch (FileNotFoundException) { return 0; }
    }

    private static string RequiredString(JsonElement values, string name) =>
        OptionalString(values, name) ?? throw Invalid("git_configuration_invalid", $"Git option '{name}' is required.");

    private static string? OptionalString(JsonElement values, string name)
    {
        if (!values.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
            throw Invalid("git_configuration_invalid", $"Git option '{name}' must be a non-empty string.");
        return property.GetString()!.Trim();
    }

    private static string TrimDiagnostic(string value) =>
        value.Length <= MaximumDiagnosticCharacters ? value : value[..MaximumDiagnosticCharacters];

    private static AepServerException Invalid(string code, string message) => new(code, message, 422);
    private static AepServerException TooLarge(string code, string message) => new(code, message, 413);

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return false;
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return !address.IsIPv6Multicast;
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0
            && bytes[0] != 10
            && bytes[0] != 127
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168)
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            && bytes[0] < 224;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitSha();

    private sealed record GitChannelConfiguration(string Repository, string Reference, string? RootPath);

    private sealed class GitWorkspace(string path) : IAsyncDisposable
    {
        public string RepositoryPath { get; } = Path.Combine(path, "repository.git");
        public string ArchivePath { get; } = Path.Combine(path, "content.zip");

        public ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return ValueTask.CompletedTask;
        }
    }
}
