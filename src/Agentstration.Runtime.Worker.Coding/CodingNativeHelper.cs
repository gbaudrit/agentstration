using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;

namespace Agentstration.Runtime.Worker.Coding;

internal static class CodingNativeHelper
{
    internal static async Task<int> RunAsync(TimeProvider clock, CancellationToken token)
    {
        var input = await ReadInputAsync(token);
        var invocation = JsonSerializer.Deserialize<CodingHelperInvocation>(input, CodingSandbox.Json);
        if (invocation is null || !CodingExecutionValidation.Material(invocation.Material)) return 78;
        var material = invocation.Material; var environment = invocation.Environment;
        var observations = new List<CodingConsumptionObservation>(); var checks = new List<CodingCheckEvidence>();
        var secrets = new List<string>(); int? cliExit = null; string? patch = null; var summary = ""; string? diagnostic = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(material.Configuration.TimeoutSeconds), clock);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            if (Directory.GetCurrentDirectory() != "/work" || !Directory.Exists("/source")) throw new CodingProcessException("coding_sandbox_context_invalid");
            var bare = await GitAsync(environment, "/source", ["rev-parse", "--is-bare-repository"], 1024, stopping.Token);
            if (bare.Output.Trim() != "true") throw new CodingProcessException("coding_bare_cache_required");
            var origin = await GitAsync(environment, "/source", ["remote", "get-url", "origin"], 2048, stopping.Token);
            if (origin.Output.Trim() != material.Repository.CloneUrl.AbsoluteUri) throw new CodingProcessException("coding_checkout_identity_mismatch");
            await GitAsync(environment, "/work", ["clone", "--quiet", "--no-local", "--no-hardlinks", "--no-checkout", "/source", "/work"], 4096, stopping.Token);
            await GitAsync(environment, "/work", ["checkout", "--quiet", "--detach", material.Input.BaseRevision], 4096, stopping.Token);
            var head = await GitAsync(environment, "/work", ["rev-parse", "HEAD"], 1024, stopping.Token);
            if (head.Output.Trim() != material.Input.BaseRevision) throw new CodingProcessException("coding_checkout_revision_mismatch");
            await MaterializeAsync(material, stopping.Token);
            if (environment.HasNativeAuth) await PrepareAccountAsync(environment, secrets, stopping.Token);
            var arguments = environment.CliPrefixArguments.Concat(new[]
            {
                "-c", "projects.\"/work\".trust_level=\"trusted\"", "exec", "--json", "--ephemeral", "--ignore-user-config",
                "--cd", "/work", "--output-last-message", "/evidence/last-message.txt", "-"
            }).ToArray();
            var reader = new CodingUsageReader(material.Coordinates, clock);
            var command = CodingSandbox.Nested(environment.CliExecutable, arguments, environment, true);
            var result = await BoundedProcess.RunAsync(command, material.Instruction, 512 * 1024, async (line, cancellation) =>
            {
                var observation = reader.Read(line);
                if (observation is null) return;
                observations.Add(observation); await EmitAsync("consumption", observation, cancellation);
            }, stopping.Token);
            cliExit = result.ExitCode;
            if (File.Exists("/evidence/last-message.txt")) summary = await ReadFileAsync("/evidence/last-message.txt", 8 * 1024, stopping.Token);
            await GitAsync(environment, "/work", ["add", "--intent-to-add", "--all"], 4096, stopping.Token);
            var diff = await GitAsync(environment, "/work", ["diff", "--binary", "--no-ext-diff", "--no-textconv", "--no-renames", "HEAD", "--"], CodingExecutionProtocol.MaximumPatchBytes, stopping.Token);
            patch = diff.Output;
            if (cliExit != 0) diagnostic = "coding_cli_failed";
            if (diagnostic is null)
            {
                if (material.Configuration.Checks.Count == 0) checks.Add(new("checks-unavailable", null, false, "checks_not_configured"));
                foreach (var check in material.Configuration.Checks)
                {
                    if (!environment.CheckExecutables.TryGetValue(check.Command, out var executable)) throw new CodingProcessException("coding_check_not_configured");
                    var process = CodingSandbox.Nested(executable, check.Arguments, environment, false);
                    var checkedResult = await BoundedProcess.RunAsync(process, null, 64 * 1024, null, stopping.Token);
                    checks.Add(new(check.Name, checkedResult.ExitCode, true));
                    if (checkedResult.ExitCode != 0) diagnostic = "coding_checks_failed";
                }
            }
            // Tests may produce code or change tracked fixtures; retain the final complete bounded diff.
            await GitAsync(environment, "/work", ["add", "--intent-to-add", "--all"], 4096, stopping.Token);
            patch = (await GitAsync(environment, "/work", ["diff", "--binary", "--no-ext-diff", "--no-textconv", "--no-renames", "HEAD", "--"],
                CodingExecutionProtocol.MaximumPatchBytes, stopping.Token)).Output;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { diagnostic = "coding_execution_timed_out"; }
        catch (CodingProcessException exception) { diagnostic = exception.Code; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { diagnostic = "coding_native_io_failed"; }
        if (secrets.Any(value => summary.Contains(value, StringComparison.Ordinal) || patch?.Contains(value, StringComparison.Ordinal) == true))
        { patch = null; summary = ""; diagnostic = "coding_native_credential_output_refused"; }
        if (observations.Count == 0)
        {
            var unavailable = new CodingConsumptionObservation(material.Coordinates.InvocationId.ToString("N") + ":unavailable", material.Coordinates,
                CodingConsumptionAvailability.Unavailable, null, null, null, null, clock.GetUtcNow(), "usage_unreported");
            observations.Add(unavailable); await EmitAsync("consumption", unavailable, token);
        }
        var digest = patch is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(patch)));
        await EmitAsync("result", new CodingNativeResult(material.Coordinates, material.Input.BaseRevision, patch, digest, summary, cliExit, checks, observations, diagnostic), token);
        return 0;
    }

    private static async Task MaterializeAsync(CodingExecutionMaterial material, CancellationToken token)
    {
        var paths = new[] { "AGENTS.md", "AGENTS.override.md", ".codex", ".codex/config.toml", CodexHarnessMaterializer.ProvenancePath };
        string? owner = null;
        var provenance = new FileInfo(Path.Combine("/work", CodexHarnessMaterializer.ProvenancePath));
        if (provenance.Exists && provenance.LinkTarget is null)
        {
            using var document = JsonDocument.Parse(await ReadFileAsync(provenance.FullName, 16 * 1024, token));
            var root = document.RootElement;
            if (root.TryGetProperty("profile", out var profile) && root.TryGetProperty("profileVersion", out var version)
                && root.TryGetProperty("profileDigest", out var digest) && root.TryGetProperty("namespace", out var ns))
                owner = $"{ns.GetString()}/{profile.GetString()}@{version.GetString()}:{digest.GetString()}";
        }
        var existing = new List<HarnessExistingFile>();
        // Enumerate actual root spellings so a case-only collision is not hidden on a case-sensitive host.
        foreach (var entry in Directory.EnumerateFileSystemEntries("/work").Where(path => paths.Any(value => value.Split('/')[0].Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))))
        {
            await ObserveAsync(entry, existing, owner, token);
            if (Directory.Exists(entry) && new DirectoryInfo(entry).LinkTarget is null)
                foreach (var child in Directory.EnumerateFileSystemEntries(entry)) await ObserveAsync(child, existing, owner, token);
        }
        var request = new HarnessExportRequest(material.Harness.Namespace, material.Harness.ProfileName, material.Harness.Version,
            material.Harness.ContentDigest, material.Input.Bindings, existing);
        var preview = new CodexHarnessMaterializer().Build(material.Harness, request);
        if (!preview.IsSuccess) throw new CodingProcessException(preview.ErrorCode!);
        foreach (var file in preview.Value!.Files)
        {
            var path = Path.Combine("/work", file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, file.Content, new UTF8Encoding(false), token);
        }
    }

    private static async Task ObserveAsync(string path, List<HarnessExistingFile> files, string? owner, CancellationToken token)
    {
        var relative = Path.GetRelativePath("/work", path).Replace('\\', '/');
        var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null) { files.Add(new(relative, null, null, "symlink")); return; }
        if (info is DirectoryInfo) { files.Add(new(relative, null, null, "directory")); return; }
        var content = await ReadBytesAsync(path, 32 * 1024, token);
        files.Add(new(relative, Convert.ToHexStringLower(SHA256.HashData(content)), owner));
    }

    private static async Task PrepareAccountAsync(CodingNativeEnvironment environment, List<string> secrets, CancellationToken token)
    {
        var content = await ReadFileAsync("/native-auth.json", 16 * 1024, token);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("OPENAI_API_KEY", out var key) || key.ValueKind != JsonValueKind.String
            || key.GetString() is not { Length: >= 8 and <= 4096 } value || value.Any(char.IsControl)
            || root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind != JsonValueKind.Null)
            throw new CodingProcessException("coding_native_auth_mode_unsupported");
        secrets.Add(value);
        var directory = Path.Combine(environment.HomeDirectory, ".codex"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "auth.json"), content, new UTF8Encoding(false), token);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.Combine(directory, "auth.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task<BoundedProcessResult> GitAsync(CodingNativeEnvironment environment, string directory, IReadOnlyList<string> arguments, int maximumBytes, CancellationToken token)
    {
        var start = new ProcessStartInfo(environment.GitExecutable) { WorkingDirectory = directory };
        CodingSandbox.CleanEnvironment(start, environment.HomeDirectory);
        foreach (var value in new[] { "-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false", "-c", "credential.helper=", "-c", "uploadpack.packObjectsHook=" }) start.ArgumentList.Add(value);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var result = await BoundedProcess.RunAsync(start, null, maximumBytes, null, token);
        if (result.ExitCode != 0) throw new CodingProcessException("coding_git_operation_failed");
        return result;
    }
    private static async Task<string> ReadFileAsync(string path, int maximumBytes, CancellationToken token)
        => new UTF8Encoding(false, true).GetString(await ReadBytesAsync(path, maximumBytes, token));
    private static async Task<byte[]> ReadBytesAsync(string path, int maximumBytes, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || info.Length > maximumBytes) throw new CodingProcessException("coding_native_file_invalid");
        return await File.ReadAllBytesAsync(path, token);
    }
    private static async Task<string> ReadInputAsync(CancellationToken token)
    {
        using var input = Console.OpenStandardInput(); using var buffer = new MemoryStream(); var block = new byte[4096];
        while (true) { var count = await input.ReadAsync(block, token); if (count == 0) break; if (buffer.Length + count > 192 * 1024) throw new CodingProcessException("coding_material_limit"); await buffer.WriteAsync(block.AsMemory(0, count), token); }
        return new UTF8Encoding(false, true).GetString(buffer.ToArray());
    }
    private static async Task EmitAsync<T>(string kind, T value, CancellationToken token)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { kind, value }, CodingSandbox.Json).AsMemory(), token);
        await Console.Out.FlushAsync(token);
    }
}
