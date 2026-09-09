using System.Text;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Tools.SourceRegistry;

public static class SourceRegistryCli
{
    public const int SuccessExitCode = 0;
    public const int ValidationExitCode = 1;
    public const int UsageExitCode = 2;
    public const int InputExitCode = 3;
    public const int CancelledExitCode = 130;

    private const string Usage = """
        Usage:
          agentstration-source-registry source <digest|validate> <source.yaml>
          agentstration-source-registry registry validate <registry.yaml> --publication-root <root> --base-uri <uri>
          agentstration-source-registry registry build <registry.yaml> --publication-root <root> --base-uri <uri> --output <root>
        """;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (arguments.Count == 1 && arguments[0] is "--help" or "-h")
        {
            await output.WriteLineAsync(Usage);
            return SuccessExitCode;
        }

        if (arguments.Count >= 2 && arguments[0] == "registry" && arguments[1] is "validate" or "build")
            return await RunRegistryAsync(arguments, output, error, cancellationToken);

        if (arguments.Count != 3 || arguments[0] != "source" || arguments[1] is not ("digest" or "validate"))
        {
            await error.WriteLineAsync(Usage);
            return UsageExitCode;
        }

        var path = arguments[2];
        var displayPath = DisplayPath(path);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await ReadUtf8Async(path, cancellationToken);
            var parsed = new SourceManifestReader().Read(content);
            if (arguments[1] == "digest")
            {
                await output.WriteLineAsync(parsed.Digest);
            }
            else
            {
                await output.WriteLineAsync(
                    $"{displayPath}: valid SourceVersion {parsed.Manifest.Definition.Publisher.Name}/{parsed.Manifest.Metadata.Name}@{parsed.Manifest.Definition.Version} {parsed.Digest}");
            }
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync($"{displayPath}: operation_cancelled: Operation cancelled.");
            return CancelledExitCode;
        }
        catch (SourceValidationException exception)
        {
            await error.WriteLineAsync($"{displayPath}: {exception.Code}: {exception.Message}");
            return ValidationExitCode;
        }
        catch (DecoderFallbackException)
        {
            await error.WriteLineAsync($"{displayPath}: source_manifest_encoding_invalid: Source Version manifests must use valid UTF-8.");
            return ValidationExitCode;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await error.WriteLineAsync($"{displayPath}: source_manifest_input_error: {SafeInputMessage(exception)}");
            return InputExitCode;
        }
    }

    private static async Task<int> RunRegistryAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryParseRegistryArguments(arguments, out var registryPath, out var publicationRoot, out var baseUri, out var outputRoot))
        {
            await error.WriteLineAsync(Usage);
            return UsageExitCode;
        }

        var displayPath = DisplayPath(registryPath);
        try
        {
            var service = new SourceRegistryPublicationService();
            var validation = arguments[1] == "validate"
                ? await service.ValidateAsync(registryPath, publicationRoot, baseUri, cancellationToken)
                : await service.BuildAsync(registryPath, publicationRoot, baseUri, outputRoot!, cancellationToken);
            var action = arguments[1] == "validate" ? "valid" : "built";
            await output.WriteLineAsync(
                $"{displayPath}: {action} SourceRegistry {validation.Registry.Manifest.Metadata.Name} {validation.Registry.RegistryDigest} publishers={validation.Registry.Manifest.Definition.Publishers.Count} sources={validation.SourceCount} versions={validation.VersionCount}");
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync($"{displayPath}: operation_cancelled: Operation cancelled.");
            return CancelledExitCode;
        }
        catch (SourceValidationException exception)
        {
            await error.WriteLineAsync($"{displayPath}: {exception.Code}: {exception.Message}");
            return ValidationExitCode;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await error.WriteLineAsync($"{displayPath}: source_registry_input_error: {SafeInputMessage(exception)}");
            return InputExitCode;
        }
    }

    private static bool TryParseRegistryArguments(
        IReadOnlyList<string> arguments,
        out string registryPath,
        out string publicationRoot,
        out Uri baseUri,
        out string? outputRoot)
    {
        registryPath = arguments.Count > 2 ? arguments[2] : string.Empty;
        publicationRoot = string.Empty;
        baseUri = null!;
        outputRoot = null;
        if (arguments.Count < 7 || (arguments.Count - 3) % 2 != 0) return false;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 3; index < arguments.Count; index += 2)
        {
            if (arguments[index] is not ("--publication-root" or "--base-uri" or "--output")
                || !options.TryAdd(arguments[index], arguments[index + 1])) return false;
        }
        if (!options.TryGetValue("--publication-root", out var rootValue)
            || !options.TryGetValue("--base-uri", out var baseValue)
            || !Uri.TryCreate(baseValue, UriKind.Absolute, out var parsedBaseUri)) return false;
        publicationRoot = rootValue;
        baseUri = parsedBaseUri;
        var build = arguments[1] == "build";
        var hasOutput = options.TryGetValue("--output", out var outputValue);
        if (build != hasOutput) return false;
        outputRoot = outputValue;
        return options.Count == (build ? 3 : 2) && !string.IsNullOrWhiteSpace(registryPath);
    }

    private static async Task<string> ReadUtf8Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[SourceManifestReader.MaximumManifestBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }

        if (length > SourceManifestReader.MaximumManifestBytes)
            throw new SourceValidationException(
                "source_manifest_size_limit",
                $"Source Version manifests cannot exceed {SourceManifestReader.MaximumManifestBytes} UTF-8 bytes.");

        var content = bytes.AsSpan(0, length);
        if (content.StartsWith(Encoding.UTF8.Preamble)) content = content[Encoding.UTF8.Preamble.Length..];
        return StrictUtf8.GetString(content);
    }

    private static string DisplayPath(string path) => path.Replace('\\', '/');

    private static string SafeInputMessage(Exception exception) => exception switch
    {
        FileNotFoundException => "The manifest file was not found.",
        DirectoryNotFoundException => "The manifest directory was not found.",
        UnauthorizedAccessException => "The manifest file cannot be read.",
        _ => "The manifest path cannot be read."
    };
}
