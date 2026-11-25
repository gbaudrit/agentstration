using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentstration.Coding.Contracts;
using Agentstration.Work;

namespace Agentstration.Coding;

/// <summary>Pure bounded materialization. Exporting does not run a CLI, resolve Secrets, write Git or change local trust.</summary>
public sealed partial class CodexHarnessMaterializer
{
    public const string Id = "codex";
    public const string Version = "1.0.0";
    public const string ConfigurationPath = ".codex/config.toml";
    public const string ProvenancePath = "agentstration-harness.provenance.json";
    public const int MaximumInstructionsBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool ValidNative(HarnessNativeConfigurationDefinition? value) => value is { Materializer: Id, MaterializerVersion: Version, Settings: { } settings }
        && settings.Model is { Length: > 0 and <= 128 } && settings.Model.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or ':')
        && settings.SandboxMode is "read-only" or "workspace-write" && settings.ApprovalPolicy is "on-request" or "never" or "untrusted";

    public Result<HarnessExportPreview> Build(HarnessProfileVersionResource profile, HarnessExportRequest request)
    {
        if (!ValidNative(profile.NativeConfiguration.Definition) || profile.Definition.Instructions is null
            || Encoding.UTF8.GetByteCount(profile.Definition.Instructions) > MaximumInstructionsBytes
            || !SafePath(profile.Definition.InstructionsPath) || profile.Definition.InstructionsPath != "AGENTS.md") return Failure("harness_configuration_invalid");
        if (request.Bindings is null || request.Bindings.Count > 32 || request.Bindings.Any(pair => !WorkProjectDefinition.ValidName(pair.Key)
            || pair.Value is null || pair.Value.Length > 8_000 || pair.Value.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')))
            || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request.Bindings, Json)) > 16 * 1024) return Failure("harness_bindings_invalid");
        var missing = false;
        var instructions = Placeholder().Replace(profile.Definition.Instructions, match =>
        {
            if (request.Bindings.TryGetValue(match.Groups[1].Value, out var value)) return value;
            missing = true; return "";
        });
        if (missing || instructions.Contains("{{", StringComparison.Ordinal) || instructions.Contains("}}", StringComparison.Ordinal)) return Failure("harness_binding_missing");
        instructions = instructions.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd() + "\n";
        if (Encoding.UTF8.GetByteCount(instructions) > MaximumInstructionsBytes) return Failure("harness_instructions_limit");
        var settings = profile.NativeConfiguration.Definition.Settings;
        var config = $"model = {Quote(settings.Model)}\nsandbox_mode = {Quote(settings.SandboxMode)}\napproval_policy = {Quote(settings.ApprovalPolicy)}\n\n[sandbox_workspace_write]\nnetwork_access = false\n";
        var owner = $"{profile.Namespace}/{profile.ProfileName}@{profile.Version}:{profile.ContentDigest}";
        var files = new List<HarnessExportFile> { File("AGENTS.md", instructions, owner), File(ConfigurationPath, config, owner) };
        var provenance = new HarnessProvenance("1.0", profile.Namespace, profile.ProfileName, profile.Version, profile.ContentDigest,
            Id, Version, profile.NativeConfiguration.Name, profile.NativeConfiguration.Version, profile.NativeConfiguration.ContentDigest,
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToDictionary(file => file.Path, file => file.Digest, StringComparer.Ordinal));
        files.Add(File(ProvenancePath, JsonSerializer.Serialize(provenance, Json) + "\n", owner));
        files = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
        var ownership = ValidateOwnership(files, request.ExistingFiles);
        if (ownership is not null) return Failure(ownership);
        var digest = WorkPlanDefinition.Digest(files.Select(file => new { file.Path, file.Digest, file.OwnerProfile }).ToArray());
        return Result<HarnessExportPreview>.Success(new(provenance, files, digest,
            ["input_tokens", "cached_input_tokens", "output_tokens", "reasoning_output_tokens"],
            ["provider_cost", "billing_currency", "provider_duration", "unreported_usage"]));
    }
    public static IReadOnlyList<string> Placeholders(string instructions) => Placeholder().Matches(instructions).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
    public static string ContentDigest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    public static string ProfileDigest(HarnessProfileDefinition definition, HarnessNativeConfigurationPin native) =>
        ContentDigest(new { Definition = definition, Native = new { native.Namespace, native.Name, native.Version, native.ContentDigest } });
    public static bool SafePath(string? path) => path is { Length: > 0 and <= 240 } && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':')
        && !path.Any(char.IsControl) && path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..")
            && !segment.Equals(".git", StringComparison.OrdinalIgnoreCase) && !segment.EndsWith('.') && !segment.EndsWith(' ')
            && !Device().IsMatch(segment));
    private static string? ValidateOwnership(IReadOnlyList<HarnessExportFile> files, IReadOnlyList<HarnessExistingFile>? existing)
    {
        if (existing is null || existing.Count > 128 || existing.Any(file => file is null || !SafePath(file.Path) || file.Kind is not ("file" or "directory" or "symlink")
                || file.OwnerProfile?.Length is > 512 || file.Kind == "file" && !WorkSharingFormat.Digest(file.Digest))
            || existing.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != existing.Count) return "harness_existing_files_invalid";
        if (existing.Any(file => file.Path.Equals("AGENTS.override.md", StringComparison.OrdinalIgnoreCase))) return "harness_instruction_override_conflict";
        foreach (var file in existing)
        {
            foreach (var output in files)
            {
                if (file.Path.StartsWith(output.Path + "/", StringComparison.OrdinalIgnoreCase)) return "harness_file_directory_conflict";
                if (output.Path.StartsWith(file.Path + "/", StringComparison.OrdinalIgnoreCase))
                {
                    if (file.Kind != "directory") return "harness_parent_path_conflict";
                    if (!output.Path.StartsWith(file.Path + "/", StringComparison.Ordinal)) return "harness_path_case_conflict";
                }
                if (!file.Path.Equals(output.Path, StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Path != output.Path) return "harness_path_case_conflict";
                if (file.Kind != "file" || file.OwnerProfile != output.OwnerProfile || file.Digest != output.Digest) return "harness_file_ownership_conflict";
            }
        }
        return null;
    }
    private static HarnessExportFile File(string path, string content, string owner) => new(path, content,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), owner);
    private static string Quote(string value) => JsonSerializer.Serialize(value);
    private static Result<HarnessExportPreview> Failure(string code) => Result<HarnessExportPreview>.Failure(code);
    [GeneratedRegex(@"\{\{([a-z][a-z0-9-]{0,63})\}\}", RegexOptions.CultureInvariant)] private static partial Regex Placeholder();
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Device();
}
