using System.Collections.Concurrent;

namespace Agentstration.Identity.Api.Security;

public interface IBffWorkloadReplayCache
{
    bool TryUse(string credentialId, string nonce, DateTimeOffset expiresAt);
}

public sealed class BffWorkloadReplayCache(TimeProvider timeProvider) : IBffWorkloadReplayCache
{
    private readonly ConcurrentDictionary<string, long> entries = new(StringComparer.Ordinal);
    private int calls;

    public bool TryUse(string credentialId, string nonce, DateTimeOffset expiresAt)
    {
        if (Interlocked.Increment(ref calls) % 128 == 0)
        {
            var now = timeProvider.GetUtcNow().UtcTicks;
            foreach (var entry in entries.Where(entry => entry.Value <= now))
                entries.TryRemove(entry.Key, out _);
        }
        return entries.TryAdd($"{credentialId}:{nonce}", expiresAt.UtcTicks);
    }
}
