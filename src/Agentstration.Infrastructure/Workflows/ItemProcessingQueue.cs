using System.Threading.Channels;
using Agentstration.Application;
using Agentstration.Domain;

namespace Agentstration.Infrastructure.Workflows;

public sealed class ItemProcessingQueue : IItemProcessingQueue
{
    private readonly Channel<(WorkspaceId, ItemId)> _channel = Channel.CreateBounded<(WorkspaceId, ItemId)>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true
    });

    public ValueTask EnqueueAsync(WorkspaceId workspaceId, ItemId itemId, CancellationToken cancellationToken) => _channel.Writer.WriteAsync((workspaceId, itemId), cancellationToken);
    public ValueTask<(WorkspaceId WorkspaceId, ItemId ItemId)> DequeueAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAsync(cancellationToken);
}
