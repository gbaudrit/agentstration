using System.Security.Claims;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Aep;
using Agentstration.Asp.Server;
using Microsoft.Extensions.Options;
using OpenDAL;
using OpenDAL.ServiceConfig;

namespace Agentstration.Extensions.OpenDal;

public sealed class OpenDalBindingFactory(IOptionsMonitor<OpenDalOptions> options,
    OpenDalExecutionGate execution, IHttpClientFactory clients) : IAspAepBindingFactory
{
    // Placement is fixed for this process; authorization can be revoked through monitored configuration.
    private readonly string root = options.CurrentValue.RootDirectory;
    public IReadOnlyList<AepStorageTypeContribution> Contributions => OpenDalStorageTypes.Contributions;

    public async ValueTask<AspStoreBinding> CreateAsync(AepStorageBindingRequest request, ClaimsPrincipal authenticatedCaller,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AspValidation.Require(options.CurrentValue.Authorizes(authenticatedCaller), "forbidden", "The transport identity is not authorized.");
        var version = Contributions.SingleOrDefault(c => c.Id == request.ContributionId)?.Versions
            .SingleOrDefault(v => v.Version == request.Version);
        AspValidation.Require(version is not null && AepStorageProtocol.Digest(version) == request.Digest,
            "incompatible_provider", "The exact Storage implementation is unavailable.");
        var store = AspValidation.Parse<AspStoreRef>(request.Store);
        AspValidation.Store(store);
        var envelope = AspValidation.Parse<OpenDalEnvelope>(request.Envelope).ToPolicy();
        var rule = AspValidation.Parse<AspAccessPolicy>(request.DatasetRule);
        AspValidation.Policy(envelope);
        AspValidation.Policy(rule);
        AspValidation.Require(rule.Prefix.StartsWith(envelope.Prefix, StringComparison.Ordinal),
            "forbidden", "The dataset rule widens the Storage envelope.");
        var policy = new AspAccessPolicy(envelope.CanList && rule.CanList, envelope.CanStat && rule.CanStat,
            envelope.CanRead && rule.CanRead, envelope.CanWrite && rule.CanWrite, rule.Prefix,
            Math.Min(envelope.MaxReadBytes, rule.MaxReadBytes), Math.Min(envelope.MaxWriteBytes, rule.MaxWriteBytes),
            Math.Min(envelope.MaxPageSize, rule.MaxPageSize), Maximum(envelope.KeyFrom, rule.KeyFrom), Minimum(envelope.KeyBefore, rule.KeyBefore));
        AspValidation.Policy(policy);
        var subject = authenticatedCaller.FindFirstValue(ClaimTypes.NameIdentifier)!;
        OpenDalObjectProvider provider;
        if (request.ContributionId == OpenDalStorageTypes.FileSystemId)
        {
            AspValidation.Require(request.Configuration.ValueKind == JsonValueKind.Object
                && !request.Configuration.EnumerateObject().Any() && request.Connection.Count == 0,
                "invalid_request", "The filesystem accepts no physical or connection overrides.");
            provider = OpenDalFileSystem.Create(root, store, execution);
        }
        else provider = await RemoteAsync(request, store, version!.ConfigurationSchema, cancellationToken);
        var caps = provider.Capabilities;
        policy = policy with
        {
            CanList = policy.CanList && caps.List,
            CanStat = policy.CanStat && caps.Stat,
            CanRead = policy.CanRead && caps.Read,
            CanWrite = policy.CanWrite && caps.Write
        };
        return new AspStoreBinding(store, policy, provider, (call, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            AspValidation.Require(options.CurrentValue.Authorizes(call.User)
                && call.User!.FindFirstValue(ClaimTypes.NameIdentifier) == subject,
                "forbidden", "The transport identity is no longer authorized.");
            return ValueTask.FromResult(new AspAccessContext(store, subject, "opendal-grant-v1", policy));
        });
    }

    private async Task<OpenDalObjectProvider> RemoteAsync(AepStorageBindingRequest request, AspStoreRef store, JsonElement schema, CancellationToken ct)
    {
        var destination = request.ContributionId == "opendal.s3" ? "bucket" : "container";
        JsonElement target = default;
        AspValidation.Require(request.ContributionId is "opendal.s3" or "opendal.azblob"
            && request.Configuration.EnumerateObject().All(p => p.Name == destination || p.Name == "root")
            && request.Configuration.TryGetProperty(destination, out target) && target.ValueKind == JsonValueKind.String
            && target.GetString() is { Length: > 0 and <= 256 }, "invalid_request", "Invalid native backend configuration.");
        var configuredRoot = request.Configuration.TryGetProperty("root", out var prefix) && prefix.ValueKind == JsonValueKind.String ? prefix.GetString()! : "";
        AspValidation.Prefix(configuredRoot);
        var nativeRoot = (configuredRoot.Length == 0 ? "" : configuredRoot.TrimEnd('/') + "/") + OpenDalFileSystem.Partition(store) + "/";
        using var http = clients.CreateClient("opendal-secret-access");
        var resolver = new AepValueResolver(request.Connection, http);
        async Task<string> Read(string id)
        {
            using var value = await resolver.ResolveAsync(id, ct);
            var text = value.ReadString();
            AspValidation.Require(text is { Length: > 0 and <= 8192 } && !text.Any(char.IsControl), "invalid_request", "Invalid connection value.");
            return text;
        }
        try
        {
            var endpoint = await Read("endpoint");
            AspValidation.Require(Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo)
                && (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback), "invalid_request", "Backend endpoints require HTTPS or loopback HTTP.");
            Func<Operator> open;
            if (request.ContributionId == "opendal.s3")
            {
                var region = await Read("region"); var accessKey = await Read("access-key"); var secret = await Read("secret-key");
                open = () => new Operator(new S3ServiceConfig
                {
                    Bucket = target.GetString(),
                    Root = nativeRoot,
                    Endpoint = endpoint,
                    Region = region,
                    AccessKeyId = accessKey,
                    SecretAccessKey = secret,
                    DisableConfigLoad = true,
                    DisableEc2Metadata = true
                });
            }
            else
            {
                var account = await Read("account"); var secret = await Read("account-key");
                open = () => new Operator(new AzblobServiceConfig
                {
                    Container = target.GetString(),
                    Root = nativeRoot,
                    Endpoint = endpoint,
                    AccountName = account,
                    AccountKey = secret
                });
            }
            return new(store, request.ContributionId, schema, open, execution);
        }
        catch (Exception error) when (error is AepProtocolException or HttpRequestException or OpenDALException)
        { throw new AspException("binding_refused", "The protected OpenDAL connection could not be configured."); }
    }

    private static string? Maximum(string? a, string? b) => a is null ? b : b is null ? a : string.CompareOrdinal(a, b) >= 0 ? a : b;
    private static string? Minimum(string? a, string? b) => a is null ? b : b is null ? a : string.CompareOrdinal(a, b) <= 0 ? a : b;
}
