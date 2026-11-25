using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Agentstration.Coding.Contracts;
using Microsoft.Extensions.Options;

namespace Agentstration.Runtime.Worker.Coding;

internal sealed class CodingSandbox(IOptions<CodingWorkerOptions> options, IOptions<RuntimeWorkerOptions> identity)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task<BoundedProcessResult> ExecuteAsync(CodingExecutionMaterial material,
        Func<string, CancellationToken, Task> onLine, CancellationToken token)
    {
        var configured = options.Value;
        var repository = configured.Repository(material.Repository) ?? throw new CodingProcessException("coding_checkout_not_configured");
        ValidateCache(repository.Directory, configured.MaximumCacheBytes, token);
        var assembly = typeof(CodingSandbox).Assembly.Location;
        var outputDirectory = Path.GetDirectoryName(assembly)!;
        var dotnet = DotnetHost(); var dotnetRoot = Path.GetDirectoryName(dotnet)!;
        var roots = configured.ReadOnlyRuntimeDirectories.Append(outputDirectory).Append(dotnetRoot).Distinct(StringComparer.Ordinal).ToArray();
        var sensitive = new[] { identity.Value.SharedKeyFile, identity.Value.CredentialStateFile, identity.Value.PairingCodeFile }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(Path.GetFullPath).ToArray();
        var systems = new[] { "/usr", "/bin", "/lib", "/lib64" }.Where(Directory.Exists).ToArray();
        foreach (var path in roots.Append(repository.Directory).Concat(sensitive)) RejectSymlinks(path);
        if (configured.NativeApiKeyAuthFile is { } nativeAuth) RejectSymlinks(nativeAuth);
        foreach (var path in roots.Append(repository.Directory).Concat(systems))
            if (sensitive.Any(value => IsInside(value, path))) throw new CodingProcessException("coding_sandbox_credential_overlap");
        if (configured.NativeApiKeyAuthFile is { } auth && sensitive.Contains(Path.GetFullPath(auth), StringComparer.Ordinal))
            throw new CodingProcessException("coding_sandbox_credential_overlap");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Path.IsPathFullyQualified(home) || home == "/") throw new CodingProcessException("coding_native_home_invalid");
        var start = new ProcessStartInfo(configured.BubblewrapExecutable);
        foreach (var value in new[] { "--die-with-parent", "--new-session", "--unshare-user", "--unshare-pid", "--unshare-ipc", "--unshare-uts" }) start.ArgumentList.Add(value);
        foreach (var path in systems) Mount(start, "--ro-bind", path, path);
        Add(start, "--dir", "/etc");
        foreach (var path in new[] { "/etc/ssl/certs", "/etc/ssl/openssl.cnf", "/etc/resolv.conf", "/etc/hosts", "/etc/nsswitch.conf", "/etc/passwd", "/etc/group" }
            .Where(path => File.Exists(path) || Directory.Exists(path))) Mount(start, "--ro-bind", path, path);
        foreach (var path in roots) Mount(start, "--ro-bind", path, path);
        Mount(start, "--ro-bind", repository.Directory, "/source");
        Add(start, "--proc", "/proc", "--dev", "/dev");
        Add(start, "--size", configured.MaximumWorktreeBytes.ToString(CultureInfo.InvariantCulture), "--tmpfs", "/work");
        Add(start, "--size", "8388608", "--tmpfs", "/tmp");
        Add(start, "--size", "8388608", "--tmpfs", home);
        Add(start, "--size", "262144", "--tmpfs", "/evidence");
        if (configured.NativeApiKeyAuthFile is { } account) Mount(start, "--ro-bind", account, "/native-auth.json");
        Add(start, "--chdir", "/work", "--", dotnet, assembly, "--native-helper");
        CleanEnvironment(start, home);
        var environment = new CodingNativeEnvironment(configured.BubblewrapExecutable, configured.GitExecutable, configured.CliExecutable, configured.CliPrefixArguments,
            configured.CheckExecutables, home, configured.NativeApiKeyAuthFile is not null);
        return await BoundedProcess.RunAsync(start, JsonSerializer.Serialize(new CodingHelperInvocation(material, environment), Json),
            512 * 1024, onLine, token);
    }

    internal static ProcessStartInfo Nested(string executable, IReadOnlyList<string> arguments, CodingNativeEnvironment environment, bool network)
    {
        var start = new ProcessStartInfo(environment.BubblewrapExecutable) { WorkingDirectory = "/work" };
        Add(start, "--die-with-parent", "--new-session", "--unshare-pid", "--ro-bind", "/", "/", "--proc", "/proc", "--bind", "/work", "/work",
            "--ro-bind", "/work/.git", "/work/.git", "--size", "4096", "--tmpfs", "/source");
        Add(start, "--bind", "/tmp", "/tmp");
        if (network)
        {
            Mount(start, "--bind", environment.HomeDirectory, environment.HomeDirectory);
            Mount(start, "--bind", "/evidence", "/evidence");
        }
        if (!network)
        {
            Add(start, "--unshare-net", "--size", "8388608", "--tmpfs", environment.HomeDirectory);
            if (environment.HasNativeAuth) Mount(start, "--ro-bind", "/dev/null", "/native-auth.json");
        }
        Add(start, "--", executable);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        CleanEnvironment(start, environment.HomeDirectory);
        return start;
    }

    internal static void CleanEnvironment(ProcessStartInfo start, string home)
    {
        // HOME retains the user's normal native path, mounted as a private filesystem in this namespace.
        start.Environment.Clear(); start.Environment["HOME"] = home; start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        start.Environment["LANG"] = "C.UTF-8"; start.Environment["TMPDIR"] = "/tmp";
        start.Environment["DOTNET_EnableDiagnostics"] = "0"; start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"; start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
    }
    private static string DotnetHost() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        ?? Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet"));
    private static void Mount(ProcessStartInfo start, string operation, string source, string target) => Add(start, operation, source, target);
    private static void Add(ProcessStartInfo start, params string[] arguments) { foreach (var argument in arguments) start.ArgumentList.Add(argument); }
    private static bool IsInside(string value, string directory) => value == Path.GetFullPath(directory)
        || value.StartsWith(Path.GetFullPath(directory).TrimEnd('/') + "/", StringComparison.Ordinal);
    private static void RejectSymlinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current)).LinkTarget is not null)
                throw new CodingProcessException("coding_sandbox_path_symlink");
    }
    private static void ValidateCache(string root, long maximum, CancellationToken token)
    {
        long bytes = 0; var count = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            token.ThrowIfCancellationRequested(); if (++count > 50_000) throw new CodingProcessException("coding_checkout_size_limit");
            if (!File.Exists(path)) continue;
            bytes += new FileInfo(path).Length; if (bytes > maximum) throw new CodingProcessException("coding_checkout_size_limit");
        }
    }
}
