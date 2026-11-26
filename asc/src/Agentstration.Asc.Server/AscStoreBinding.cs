using Agentstration.Asc.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Asc.Server;

// The callback must authenticate/authorize EVERY call from trusted transport state.
// A caller-selected target is a selector, never proof of permission.
public delegate ValueTask<AscAccessContext> AscAuthorize(
    RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken);

// Host configuration maps an exact logical target to its physical provider.
public sealed record AscStoreBinding(
    AscStoreRef Store, AscAccessPolicy Policy, IAscObjectReader Provider, AscAuthorize Authorize);
