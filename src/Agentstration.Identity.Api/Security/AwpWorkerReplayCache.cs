using System.Collections.Concurrent;

namespace Agentstration.Identity.Api.Security;

public interface IAwpWorkerReplayCache
{
    bool TryUse(Guid credentialId, string nonce, DateTimeOffset expiresAt);
}

public sealed class AwpWorkerReplayCache(TimeProvider timeProvider) : IAwpWorkerReplayCache
{
    private readonly ConcurrentDictionary<string, long> entries = new(StringComparer.Ordinal);
    private int calls;

    public bool TryUse(Guid credentialId, string nonce, DateTimeOffset expiresAt)
    {
        if (Interlocked.Increment(ref calls) % 128 == 0)
        {
            var now = timeProvider.GetUtcNow().UtcTicks;
            foreach (var entry in entries.Where(entry => entry.Value <= now)) entries.TryRemove(entry.Key, out _);
        }
        return entries.TryAdd($"{credentialId:D}:{nonce}", expiresAt.UtcTicks);
    }
}
