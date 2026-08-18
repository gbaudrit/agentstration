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
    private readonly Channel<LocalWorkExecution> _queue = Channel.CreateBounded<LocalWorkExecution>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    public async Task<WorkExecutionAccepted> RequestExecutionAsync(WorkExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var accepted = new WorkExecutionAccepted(WorkExecutionId.New(), request.RequestedAgentId, timeProvider.GetUtcNow(), Guid.NewGuid());
        if (!_pending.TryAdd(accepted.ExecutionId, new LocalWorkExecution(request, accepted))) throw new InvalidOperationException("The local execution identifier already exists.");
        await Task.CompletedTask;
        return accepted;
    }

    public async Task ConfirmQueuedAsync(WorkExecutionAccepted accepted, CancellationToken cancellationToken)
    {
        if (!_pending.TryRemove(accepted.ExecutionId, out var execution)) throw new InvalidOperationException($"Execution '{accepted.ExecutionId}' is not awaiting queue confirmation.");
        await _queue.Writer.WriteAsync(execution, cancellationToken);
    }

    public async IAsyncEnumerable<LocalWorkExecution> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var execution in _queue.Reader.ReadAllAsync(cancellationToken)) yield return execution;
    }
}
