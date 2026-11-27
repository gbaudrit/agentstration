using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace Agentstration.Infrastructure;

public sealed record AdditionalSettingsFile(string Identity, string Path);

/// <summary>The startup selection, captured before any additional provider is integrated.</summary>
public sealed record AdditionalSettingsSnapshot(IReadOnlyList<AdditionalSettingsFile> Files);

public static class AdditionalSettingsConfiguration
{
    public static AdditionalSettingsSnapshot LoadAgentstrationAdditionalSettings(
        this ConfigurationManager configuration, string contentRoot)
    {
        var paths = configuration.GetSection("Agentstration:AdditionalSettings").Get<string[]>() ?? [];
        if (paths.Length > 16) throw new InvalidOperationException("additional_settings_limit");
        var files = new List<AdditionalSettingsFile>();
        var providers = new List<IConfigurationRoot>();
        var identities = new HashSet<Guid>();
        foreach (var selected in paths)
        {
            var path = System.IO.Path.GetFullPath(selected, contentRoot);
            var filename = System.IO.Path.GetFileName(path);
            const string suffix = ".appsettings.json";
            if (!filename.EndsWith(suffix, StringComparison.Ordinal)
                || !Guid.TryParseExact(filename[..^suffix.Length], "D", out var identity)
                || !identities.Add(identity))
                throw new InvalidOperationException("additional_settings_identity_invalid");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1024 * 1024)
                throw new InvalidOperationException("additional_settings_file_invalid");
            for (FileSystemInfo? entry = info; entry is not null;
                 entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("additional_settings_link_invalid");
            // Use the native JSON provider, including its case-insensitive, colon-delimited
            // key semantics, comments, trailing commas and duplicate-key validation.
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            IConfigurationRoot isolated;
            try { isolated = new ConfigurationBuilder().AddJsonStream(stream).Build(); }
            catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException)
            { throw new InvalidOperationException("additional_settings_json_invalid", exception); }
            foreach (var key in isolated.AsEnumerable().Select(pair => pair.Key))
                if (Reserved(key, "Agentstration:Bootstrap") || Reserved(key, "Agentstration:AdditionalSettings"))
                    throw new InvalidOperationException("additional_settings_reserved_section");
            files.Add(new(identity.ToString("D"), path));
            providers.Add(isolated);
        }
        // Insert after ordinary JSON/user-secrets, before environment/arguments and
        // host-specific overrides. Do not reread a file after validating its bytes.
        var lastJson = configuration.Sources.Select((source, index) => (source, index))
            .Where(item => item.source is JsonConfigurationSource).Select(item => item.index).DefaultIfEmpty(-1).Max();
        var insertion = lastJson + 1;
        if (lastJson < 0)
        {
            insertion = configuration.Sources.Select((source, index) => (source, index))
                .Where(item => item.source is EnvironmentVariablesConfigurationSource or CommandLineConfigurationSource)
                .Select(item => item.index).DefaultIfEmpty(configuration.Sources.Count).Min();
        }
        foreach (var provider in providers)
            configuration.Sources.Insert(insertion++, new ChainedConfigurationSource { Configuration = provider, ShouldDisposeConfiguration = true });
        return new(files.ToArray());
    }

    private static bool Reserved(string key, string section) =>
        key.Equals(section, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase);
}
