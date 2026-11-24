using System.Diagnostics;
using Agentstration.Aep.AspNetCore;

namespace Agentstration.Extensions.Git;

internal sealed class GitCommandRunner(GitSourceProviderOptions options, bool allowHttpLoopback = false, bool protectRemote = false)
{
    internal async Task<GitCommandResult> RunAsync(string directory, IReadOnlyList<string> arguments, long maximumRepositoryBytes,
        CancellationToken token, string? boundedFile = null, long maximumFileBytes = long.MaxValue,
        byte[]? input = null, IReadOnlyDictionary<string, string>? environment = null, int maximumOutputBytes = 16 * 1024,
        bool truncateOutput = false, bool permitFailure = false)
    {
        var start = new ProcessStartInfo
        {
            FileName = options.GitExecutable, WorkingDirectory = directory, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = input is not null, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        var hooks = Path.Combine(directory, ".disabled-hooks"); Directory.CreateDirectory(hooks);
        var config = Path.Combine(directory, ".empty-gitconfig"); await File.WriteAllTextAsync(config, string.Empty, token);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = config;
        foreach (var setting in new[] { "credential.helper=", $"core.hooksPath={hooks}", "protocol.allow=never", "protocol.https.allow=always",
            options.AllowLocalRepositories ? "protocol.file.allow=always" : "protocol.file.allow=never",
            allowHttpLoopback ? "protocol.http.allow=always" : "protocol.http.allow=never", "protocol.ext.allow=never", "submodule.recurse=false",
            protectRemote ? "http.followRedirects=false" : "http.followRedirects=initial", "http.sslVerify=true", "commit.gpgSign=false", "tag.gpgSign=false", "core.fsmonitor=false" })
        { start.ArgumentList.Add("-c"); start.ArgumentList.Add(setting); }
        start.ArgumentList.Add("--literal-pathspecs");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var pair in environment ?? new Dictionary<string, string>()) start.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Git process could not start.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw new AepServerException("git_unavailable", "The configured Git executable is unavailable.", 502); }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var output = ReadAsync(process.StandardOutput.BaseStream, maximumOutputBytes, truncateOutput, lifetime.Token);
        // Consume but never retain provider stderr: it can contain downstream credentials.
        var diagnostic = DrainAsync(process.StandardError.BaseStream, lifetime.Token);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, token);
                await process.StandardInput.BaseStream.FlushAsync(token); process.StandardInput.Close();
            }
            while (!process.HasExited)
            {
                token.ThrowIfCancellationRequested();
                if (output.IsFaulted) await output;
                if (DirectorySize(directory) > maximumRepositoryBytes) throw new GitOperationException("git_repository_too_large");
                if (boundedFile is not null && File.Exists(boundedFile) && new FileInfo(boundedFile).Length > maximumFileBytes)
                    throw new GitOperationException("git_archive_too_large");
                await Task.Delay(50, token);
            }
            await process.WaitForExitAsync(token);
            var bytes = await output; await diagnostic;
            if (DirectorySize(directory) > maximumRepositoryBytes) throw new GitOperationException("git_repository_too_large");
            if (process.ExitCode != 0 && !permitFailure) throw new GitOperationException("git_command_failed");
            return new(process.ExitCode, bytes);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await lifetime.CancelAsync();
            try { await Task.WhenAll(output, diagnostic); } catch (Exception error) when (error is OperationCanceledException or IOException or GitOperationException) { }
            throw;
        }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int maximum, bool truncate, CancellationToken token)
    {
        using var result = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (result.Length + read > maximum && !truncate) throw new GitOperationException("git_output_too_large");
            var retained = Math.Min(read, maximum - (int)result.Length);
            if (retained > 0) await result.WriteAsync(buffer.AsMemory(0, retained), token);
        }
        return result.ToArray();
    }
    private static async Task DrainAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[8192]; while (await stream.ReadAsync(buffer, token) != 0) { }
    }
    private static long DirectorySize(string path)
    {
        try { return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length); }
        catch (DirectoryNotFoundException) { return 0; } catch (FileNotFoundException) { return 0; }
    }
}
