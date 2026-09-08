using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;

namespace Aep.Samples.SourceProvider;

public sealed class Program
{
    private static readonly string[] RequiredSourceOptions = ["selector"];

    public static async Task Main(string[] args)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { selector = new { type = "string" } },
            required = RequiredSourceOptions,
            additionalProperties = false
        });
        var version = AepOptionSetVersionDescriptor.Create("1.0", schema);
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddAep(options =>
        {
            options.Extension = new("sample.source-provider", "Deterministic source sample", "1.0.0", "Provider-neutral AEP source sample.");
            options.OptionSets.Add(new(
                "sample/source-channel",
                AepContributionKinds.SourceProvider,
                "deterministic",
                AepOptionScopes.SourceChannel,
                version.Version,
                [version]));
        }).AddSourceProvider<DeterministicSourceProvider>();
        var app = builder.Build();
        app.MapAep();
        await app.RunAsync();
    }
}

public sealed class DeterministicSourceProvider : IAepSourceProvider
{
    public AepSourceProviderDescriptor Descriptor { get; } = new(
        "deterministic",
        "Deterministic source provider",
        "Returns an offline fixture archive for AEP conformance tests.");

    public Task<AepSourceResolveResponse> ResolveAsync(AepSourceResolveRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selector = request.Configuration.Values.GetProperty("selector").GetString()!;
        var revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(selector))).ToLowerInvariant();
        return Task.FromResult(new AepSourceResolveResponse(revision, new("sha256", revision)));
    }

    public Task<AepSourceMaterializeResponse> MaterializeAsync(AepSourceMaterializeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = Encoding.UTF8.GetBytes($"fixture:{request.Revision}");
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("source.txt", CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open();
            stream.Write(payload);
        }
        var content = buffer.ToArray();
        return Task.FromResult(new AepSourceMaterializeResponse(
            request.Revision,
            new("application/zip", content, payload.LongLength, 1, AepContentIntegrity.Sha256(content))));
    }
}
