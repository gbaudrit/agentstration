using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Local;

public sealed class LocalRuntimeRunQueue : IRuntimeRunQueue
{
    private readonly Channel<string> queue = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(string runId, CancellationToken cancellationToken) => queue.Writer.WriteAsync(runId, cancellationToken);

    public async IAsyncEnumerable<string> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var runId in queue.Reader.ReadAllAsync(cancellationToken)) yield return runId;
    }
}

public sealed class LocalRuntimeRunCancellationRegistry : IRuntimeRunCancellationRegistry, IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> sources = new(StringComparer.Ordinal);

    public CancellationToken Register(string runId, CancellationToken stoppingToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!sources.TryAdd(runId, source))
        {
            source.Dispose();
            throw new InvalidOperationException($"Runtime run '{runId}' is already executing.");
        }
        return source.Token;
    }

    public bool Cancel(string runId)
    {
        if (!sources.TryGetValue(runId, out var source)) return false;
        source.Cancel();
        return true;
    }

    public void Complete(string runId)
    {
        if (sources.TryRemove(runId, out var source)) source.Dispose();
    }

    public void Dispose()
    {
        foreach (var source in sources.Values) source.Dispose();
        sources.Clear();
    }
}
