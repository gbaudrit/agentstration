using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.SystemOperations.Contracts;

namespace Agentstration.SystemOperations;

/// <summary>One bounded session: full resynchronization, deltas, then ordered heartbeats.</summary>
public sealed class SystemOperationsStreamState
{
    private readonly Guid epoch = Guid.NewGuid();
    private long sequence;
    private string? previous;
    public SystemOperationsFrame Next(SystemOperationalSnapshot snapshot)
    {
        var fingerprint = snapshot.Facts is null ? "unavailable" : Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            snapshot.Facts with { WindowStart = DateTimeOffset.UnixEpoch, SampledAt = DateTimeOffset.UnixEpoch })));
        var kind = sequence == 0 ? "snapshot" : previous != fingerprint ? "delta" : "heartbeat";
        var frame = new SystemOperationsFrame(epoch, sequence + 1, sequence, kind, snapshot.SampledAt,
            kind == "heartbeat" ? null : snapshot.Facts, snapshot.Facts is null);
        ++sequence; previous = fingerprint;
        return frame;
    }
    public SystemOperationsFrame Stop(DateTimeOffset sampledAt, string kind) => new(epoch, sequence + 1, sequence, kind, sampledAt, Clear: true);
}
