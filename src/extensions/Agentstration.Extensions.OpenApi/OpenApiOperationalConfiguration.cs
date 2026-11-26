using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.OpenApi;

/// <summary>One atomically replaced installation policy. Persist only this non-secret allowlist.</summary>
public sealed class OpenApiOperationalConfiguration : IAepOperationalConfiguration
{
    private sealed record State(Guid ConfigurationId, long Generation, string Revision, string[] Domains);
    private static readonly JsonElement DomainSchema = JsonSerializer.SerializeToElement(new
    {
        type = "array", maxItems = 128, items = new { type = "string", minLength = 1, maxLength = 253 }
    });
    public static AepOperationalConfigurationDescriptor Descriptor { get; } = AepOperationalConfigurationDescriptor.Create(
        "io.agentstration.openapi/installation", "1.0.0",
        [new("AllowedDomains", DomainSchema, true, AepValueProtection.Standard,
            [AepConfigurationValueSource.Literal, AepConfigurationValueSource.Parameter],
            AepConfigurationScope.Process, AepConfigurationApplyMode.Immediate)]);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly OpenApiOptions initial;
    private readonly string stateFile;
    private State? active;

    public OpenApiOperationalConfiguration(IOptions<OpenApiOptions> options, IConfiguration configuration, IHostEnvironment environment)
    {
        initial = options.Value;
        var enrollmentFile = configuration["Aep:SharedKeyFile:StateFile"] ?? configuration["Aep:PairingCode:StateFile"];
        stateFile = configuration["OpenApi:ConfigurationStateFile"]
            ?? (enrollmentFile is not null ? enrollmentFile + ".openapi-configuration.json"
                : Path.Combine(environment.ContentRootPath, ".aep", "openapi-configuration.json"));
        if (initial.DomainCeiling.Length > 128 || initial.DomainCeiling.Any(d => string.IsNullOrWhiteSpace(d)
            || d.Length > 253 || d != d.Trim() || Uri.CheckHostName(d.TrimEnd('.')) == UriHostNameType.Unknown))
            throw new InvalidOperationException("OpenApi:DomainCeiling contains an invalid domain.");
        ValidateDomains(initial.AllowedDomains);
        if (File.Exists(stateFile))
        {
            var info = new FileInfo(stateFile);
            if (info.Length > AepOperationalConfigurationProtocol.MaximumBytes) throw new InvalidOperationException("OpenAPI configuration state is too large.");
            active = JsonSerializer.Deserialize<State>(File.ReadAllBytes(stateFile), AepProtocol.JsonOptions)
                ?? throw new InvalidOperationException("OpenAPI configuration state is invalid.");
            ValidateDomains(active.Domains);
        }
    }

    public string[] AllowedDomains => (Volatile.Read(ref active)?.Domains ?? initial.AllowedDomains).ToArray();

    public AepOperationalConfigurationState Read()
    {
        var snapshot = Volatile.Read(ref active);
        var domains = snapshot?.Domains ?? initial.AllowedDomains;
        return new(Descriptor, snapshot?.Revision, domains.Length != 0,
            new Dictionary<string, JsonElement> { ["AllowedDomains"] = JsonSerializer.SerializeToElement(domains) });
    }

    public async ValueTask<AepOperationalConfigurationState> ApplyAsync(AepOperationalConfigurationRequest request, CancellationToken cancellationToken)
    {
        var value = request.Values.Single(v => v.RequirementId == "AllowedDomains");
        if (value.InlineValue is not { ValueKind: JsonValueKind.Array } json) throw new AepOperationalConfigurationException("configuration_invalid");
        var domains = json.Deserialize<string[]>() ?? [];
        ValidateDomains(domains);
        domains = domains.Select(d => d.TrimEnd('.').ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        await gate.WaitAsync(cancellationToken);
        try
        {
            var previous = active;
            if (previous?.Revision == request.Revision)
            {
                if (previous.ConfigurationId != request.ConfigurationId || previous.Generation != request.Generation
                    || !previous.Domains.SequenceEqual(domains)) throw new AepOperationalConfigurationException("configuration_conflict", true);
                return Read();
            }
            if (previous?.Revision != request.ExpectedRevision
                || previous is not null && (previous.ConfigurationId != request.ConfigurationId || request.Generation < previous.Generation))
                throw new AepOperationalConfigurationException("configuration_conflict", true);
            var candidate = new State(request.ConfigurationId, request.Generation, request.Revision, domains);
            var full = Path.GetFullPath(stateFile);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await file.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(candidate, AepProtocol.JsonOptions), cancellationToken);
                    await file.FlushAsync(cancellationToken);
                }
                File.Move(temporary, full, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Volatile.Write(ref active, candidate);
            return Read();
        }
        finally { gate.Release(); }
    }

    private void ValidateDomains(string[] domains)
    {
        if (domains.Length > 128 || domains.Any(d => string.IsNullOrWhiteSpace(d) || d.Length > 253
            || d != d.Trim() || Uri.CheckHostName(d.TrimEnd('.')) == UriHostNameType.Unknown))
            throw new AepOperationalConfigurationException("configuration_domains_invalid");
        if (initial.DomainCeiling.Length > 0 && domains.Any(d => !initial.DomainCeiling.Any(c =>
            d.TrimEnd('.').Equals(c.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            || d.TrimEnd('.').EndsWith("." + c.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))))
            throw new AepOperationalConfigurationException("configuration_ceiling_denied");
    }
}
