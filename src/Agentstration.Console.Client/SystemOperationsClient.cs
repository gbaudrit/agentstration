using System.Runtime.CompilerServices;
using Agentstration.Resources;
using Agentstration.SystemOperations.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentstration.Web.Console;

public interface ISystemOperationsClient
{
    IAsyncEnumerable<SystemOperationsFrame> WatchAsync(ResourceScopeRef context, SystemOperationsQuery query, CancellationToken ct);
}
public sealed class SystemOperationsClient(HttpClient http, IConsoleRealtimeConnectionConfigurator session) : ISystemOperationsClient
{
    public async IAsyncEnumerable<SystemOperationsFrame> WatchAsync(ResourceScopeRef context, SystemOperationsQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var endpoint = new Uri(QueryHelpers.AddQueryString(new Uri(http.BaseAddress!, "hubs/system-operations").AbsoluteUri, "context", context.Value));
        await using var connection = new HubConnectionBuilder().WithUrl(endpoint, options => session.Configure(endpoint, options)).Build();
        await connection.StartAsync(ct);
        await foreach (var frame in connection.StreamAsync<SystemOperationsFrame>("WatchAsync", query, ct)) yield return frame;
    }
}

/// <summary>One selected identity/session. Duplicate frames are harmless; gaps require a fresh stream.</summary>
public sealed class SystemOperationsBuffer
{
    private Guid? epoch;
    private long sequence;
    public SystemOperationalFacts? Facts { get; private set; }
    public DateTimeOffset? SampledAt { get; private set; }
    public bool Apply(SystemOperationsFrame frame, string root)
    {
        if (epoch == frame.Epoch && frame.Sequence <= sequence) return true;
        if (frame.Clear) { Clear(); return false; }
        if (epoch is null)
        {
            if (frame.Kind != "snapshot" || frame.Sequence != 1 || frame.BaseSequence != 0) return false;
        }
        else if (epoch != frame.Epoch || frame.Sequence != sequence + 1 || frame.BaseSequence != sequence)
        { Clear(); return false; }
        if (frame.Kind is not ("snapshot" or "delta" or "heartbeat") || frame.Kind != "heartbeat" && frame.Upsert?.NodeId != root)
        { Clear(); return false; }
        epoch = frame.Epoch; sequence = frame.Sequence; SampledAt = frame.SampledAt;
        if (frame.Kind != "heartbeat") Facts = frame.Upsert;
        return true;
    }
    public void Clear() { epoch = null; sequence = 0; Facts = null; SampledAt = null; }
}
