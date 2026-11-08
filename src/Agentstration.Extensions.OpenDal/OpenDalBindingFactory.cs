using System.Security.Claims;
using Agentstration.Aep.Abstractions;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Aep;
using Agentstration.Asp.Server;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.OpenDal;

public sealed class OpenDalBindingFactory(IOptionsMonitor<OpenDalOptions> options, TimeProvider clock,
    OpenDalExecutionGate execution) : IAspAepBindingFactory
{
    // Placement is fixed for this process; authorization can be revoked through monitored configuration.
    private readonly string root = options.CurrentValue.RootDirectory;
    public IReadOnlyList<AepStorageTypeContribution> Contributions => OpenDalStorageTypes.Contributions;

    public ValueTask<AspStoreBinding> CreateAsync(AepStorageBindingRequest request, ClaimsPrincipal authenticatedCaller,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AspValidation.Require(options.CurrentValue.Authorizes(authenticatedCaller), "forbidden", "The transport identity is not authorized.");
        var version = Contributions.SingleOrDefault(c => c.Id == request.ContributionId)?.Versions
            .SingleOrDefault(v => v.Version == request.Version);
        AspValidation.Require(version is not null && AepStorageProtocol.Digest(version) == request.Digest,
            "incompatible_provider", "The exact Storage implementation is unavailable.");
        AspValidation.Require(request.ContributionId == OpenDalStorageTypes.FileSystemId,
            "unsupported_operation", "This backend is a descriptor only and cannot configure executable Storage.");
        AspValidation.Require(request.Configuration.ValueKind == System.Text.Json.JsonValueKind.Object
            && !request.Configuration.EnumerateObject().Any() && request.Connection.Count == 0,
            "invalid_request", "The filesystem implementation accepts no connection or physical configuration.");
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
        var provider = new OpenDalFileSystemProvider(root, store, clock, execution);
        return ValueTask.FromResult(new AspStoreBinding(store, policy, provider, (call, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            AspValidation.Require(options.CurrentValue.Authorizes(call.User)
                && call.User!.FindFirstValue(ClaimTypes.NameIdentifier) == subject,
                "forbidden", "The transport identity is no longer authorized.");
            return ValueTask.FromResult(new AspAccessContext(store, subject, "opendal-grant-v1", policy));
        }));
    }

    private static string? Maximum(string? a, string? b) => a is null ? b : b is null ? a : string.CompareOrdinal(a, b) >= 0 ? a : b;
    private static string? Minimum(string? a, string? b) => a is null ? b : b is null ? a : string.CompareOrdinal(a, b) <= 0 ? a : b;
}
