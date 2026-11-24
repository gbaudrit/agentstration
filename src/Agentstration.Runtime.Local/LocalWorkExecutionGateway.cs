using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agentstration.Work;

namespace Agentstration.Runtime.Local;

public sealed record LocalWorkExecution(WorkExecutionRequest Request, WorkExecutionAccepted Accepted);

public interface ILocalWorkExecutionQueue
{
    IAsyncEnumerable<LocalWorkExecution> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class LocalWorkExecutionGateway(TimeProvider timeProvider) : IWorkExecutionGateway, ILocalWorkExecutionQueue
{
    private readonly ConcurrentDictionary<WorkExecutionId, LocalWorkExecution> _pending = new();
    private readonly ConcurrentDictionary<WorkExecutionId, byte> _queued = new();
    private readonly Channel<LocalWorkExecution> _queue = Channel.CreateBounded<LocalWorkExecution>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    public async Task<WorkExecutionAccepted> RequestExecutionAsync(WorkExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = request.RequestedExecutionId ?? WorkExecutionId.New();
        var accepted = new WorkExecutionAccepted(id, request.RequestedAgentId, timeProvider.GetUtcNow(), id.Value);
        accepted = _pending.GetOrAdd(id, new LocalWorkExecution(request, accepted)).Accepted;
        await Task.CompletedTask;
        return accepted;
    }

    public async Task ConfirmQueuedAsync(WorkExecutionAccepted accepted, CancellationToken cancellationToken)
    {
        if (!_pending.TryGetValue(accepted.ExecutionId, out var execution) || !_queued.TryAdd(accepted.ExecutionId, 0)) return;
        try { await _queue.Writer.WriteAsync(execution, cancellationToken); }
        catch { _queued.TryRemove(accepted.ExecutionId, out _); throw; }
    }

    public async IAsyncEnumerable<LocalWorkExecution> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var execution in _queue.Reader.ReadAllAsync(cancellationToken))
        {
            yield return execution;
            _pending.TryRemove(execution.Accepted.ExecutionId, out _);
            _queued.TryRemove(execution.Accepted.ExecutionId, out _);
        }
    }
}
