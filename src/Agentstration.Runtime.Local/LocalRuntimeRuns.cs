using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Local;

public sealed class LocalRuntimeRunQueue : IRuntimeRunQueue
{
    private readonly Channel<RuntimeRunQueueItem> queue = Channel.CreateBounded<RuntimeRunQueueItem>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(RuntimeRunQueueItem item, CancellationToken cancellationToken) => queue.Writer.WriteAsync(item, cancellationToken);

    public async IAsyncEnumerable<RuntimeRunQueueItem> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in queue.Reader.ReadAllAsync(cancellationToken)) yield return item;
    }
}

public sealed class LocalRuntimeRunCancellationRegistry : IRuntimeRunCancellationRegistry, IDisposable
{
    private readonly ConcurrentDictionary<RuntimeRunKey, CancellationTokenSource> sources = new();

    public CancellationToken Register(RuntimeRunKey key, CancellationToken stoppingToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!sources.TryAdd(key, source))
        {
            source.Dispose();
            throw new InvalidOperationException($"Runtime run '{key.RunId}' is already executing in Workspace '{key.WorkspaceId}'.");
        }
        return source.Token;
    }

    public bool Cancel(RuntimeRunKey key)
    {
        if (!sources.TryGetValue(key, out var source)) return false;
        source.Cancel();
        return true;
    }

    public void Complete(RuntimeRunKey key)
    {
        if (sources.TryRemove(key, out var source)) source.Dispose();
    }

    public void Dispose()
    {
        foreach (var source in sources.Values) source.Dispose();
        sources.Clear();
    }
}
