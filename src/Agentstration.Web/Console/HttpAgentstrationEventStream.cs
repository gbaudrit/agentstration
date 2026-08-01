using System.Runtime.CompilerServices;
using Agentstration.Web.Components.Models;

namespace Agentstration.Web.Console;

public sealed class HttpAgentstrationEventStream : IAgentstrationEventStream
{
    public Task<IReadOnlyList<EventListItem>> GetRecentEventsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<EventListItem>>([]);
    }

    public async IAsyncEnumerable<EventListItem> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }
}

