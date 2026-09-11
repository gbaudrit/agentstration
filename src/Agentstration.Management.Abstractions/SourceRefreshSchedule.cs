using System.Security.Cryptography;
using System.Text;

namespace Agentstration.Management.Abstractions;

public static class SourceRefreshSchedule
{
    public static DateTimeOffset GetNextDue(
        DateTimeOffset lastAttempt,
        int consecutiveFailures,
        SourceRefreshPolicy policy,
        string scheduleKey) =>
        GetNextDue(lastAttempt, consecutiveFailures, policy.MaximumAttempts,
            TimeSpan.FromSeconds(policy.IntervalSeconds), TimeSpan.FromSeconds(policy.InitialBackoffSeconds),
            TimeSpan.FromSeconds(policy.MaximumBackoffSeconds), TimeSpan.FromSeconds(policy.JitterSeconds), scheduleKey);

    public static DateTimeOffset GetNextDue(
        DateTimeOffset lastAttempt,
        int consecutiveFailures,
        SourceRegistryRefreshPolicy policy,
        string scheduleKey) =>
        GetNextDue(lastAttempt, consecutiveFailures, policy.MaximumAttempts, policy.Interval,
            policy.InitialBackoff, policy.MaximumBackoff, policy.Jitter, scheduleKey);

    private static DateTimeOffset GetNextDue(
        DateTimeOffset lastAttempt,
        int consecutiveFailures,
        int maximumAttempts,
        TimeSpan interval,
        TimeSpan initialBackoff,
        TimeSpan maximumBackoff,
        TimeSpan jitter,
        string scheduleKey)
    {
        var delay = consecutiveFailures is > 0 and < int.MaxValue && consecutiveFailures < maximumAttempts
            ? TimeSpan.FromTicks(Math.Min(maximumBackoff.Ticks,
                (long)Math.Min(long.MaxValue, initialBackoff.Ticks * Math.Pow(2, consecutiveFailures - 1))))
            : interval + DeterministicJitter(scheduleKey, jitter);
        return lastAttempt + delay;
    }

    private static TimeSpan DeterministicJitter(string key, TimeSpan maximum)
    {
        if (maximum == TimeSpan.Zero) return TimeSpan.Zero;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var maximumSeconds = checked((uint)maximum.TotalSeconds);
        return TimeSpan.FromSeconds(BitConverter.ToUInt32(digest) % (maximumSeconds + 1));
    }
}
