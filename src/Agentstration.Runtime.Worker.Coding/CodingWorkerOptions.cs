using Agentstration.Coding;
using Agentstration.Coding.Contracts;

namespace Agentstration.Runtime.Worker.Coding;

public sealed class CodingWorkerOptions
{
    public const string SectionName = "Agentstration:CodingWorker";
    public string BubblewrapExecutable { get; set; } = "/usr/bin/bwrap";
    public string GitExecutable { get; set; } = "/usr/bin/git";
    public string CliExecutable { get; set; } = "/usr/local/bin/codex";
    public IReadOnlyList<string> CliPrefixArguments { get; set; } = [];
    public IReadOnlyList<string> ReadOnlyRuntimeDirectories { get; set; } = [];
    public string? NativeApiKeyAuthFile { get; set; }
    public IReadOnlyList<CodingRepositoryCache> Repositories { get; set; } = [];
    public IReadOnlyDictionary<string, string> CheckExecutables { get; set; } = new Dictionary<string, string>();
    public long MaximumWorktreeBytes { get; set; } = 64 * 1024 * 1024;
    public long MaximumCacheBytes { get; set; } = 256 * 1024 * 1024;

    public bool Validate()
    {
        if (!OperatingSystem.IsLinux() || !Executable(BubblewrapExecutable) || !Executable(GitExecutable) || !Executable(CliExecutable)
            || CliPrefixArguments.Count > 8 || CliPrefixArguments.Any(value => value is not { Length: <= 1024 } || value.Any(char.IsControl))
            || ReadOnlyRuntimeDirectories.Count > 8 || ReadOnlyRuntimeDirectories.Any(value => !DirectoryPath(value))
            || NativeApiKeyAuthFile is { } auth && (!Path.IsPathFullyQualified(auth) || !File.Exists(auth) || new FileInfo(auth).Length > 16 * 1024)
            || Repositories.Count is < 1 or > 16 || Repositories.Any(value => value is null || value.Provider is not { Length: > 0 and <= 64 }
                || value.NativeId is not { Length: > 0 and <= 256 } || !Origin(value.Origin) || !DirectoryPath(value.Directory))
            || Repositories.Select(value => value.Provider + ":" + value.NativeId).Distinct(StringComparer.Ordinal).Count() != Repositories.Count
            || CheckExecutables.Count > 8 || CheckExecutables.Any(pair => !Agentstration.Work.WorkProjectDefinition.ValidName(pair.Key) || !Executable(pair.Value))
            || MaximumWorktreeBytes is < 1024 * 1024 or > 256 * 1024 * 1024 || MaximumCacheBytes is < 1024 * 1024 or > 1024 * 1024 * 1024) return false;
        return true;
    }
    internal CodingRepositoryCache? Repository(CodingRepositoryIdentity identity) => Repositories.SingleOrDefault(value =>
        value.Provider == identity.Provider && value.NativeId == identity.NativeId && value.Origin == identity.CloneUrl.AbsoluteUri);
    private static bool Executable(string value) => Path.IsPathFullyQualified(value) && File.Exists(value);
    private static bool DirectoryPath(string value) => Path.IsPathFullyQualified(value) && Directory.Exists(value) && Path.GetFullPath(value).TrimEnd('/') != "/";
    private static bool Origin(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.Query.Length == 0;
}

public sealed record CodingRepositoryCache(string Provider, string NativeId, string Origin, string Directory);
internal sealed record CodingNativeEnvironment(string BubblewrapExecutable, string GitExecutable, string CliExecutable, IReadOnlyList<string> CliPrefixArguments,
    IReadOnlyDictionary<string, string> CheckExecutables, string HomeDirectory, bool HasNativeAuth);
internal sealed record CodingHelperInvocation(CodingExecutionMaterial Material, CodingNativeEnvironment Environment);
