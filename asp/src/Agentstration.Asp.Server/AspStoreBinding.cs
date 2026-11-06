using Agentstration.Asp.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Asp.Server;

// The callback must authenticate/authorize EVERY call from trusted transport state.
// A caller-selected target is a selector, never proof of permission.
public delegate ValueTask<AspAccessContext> AspAuthorize(
    RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken);

// Host configuration maps an exact logical target to its physical provider.
public sealed record AspStoreBinding(
    AspStoreRef Store, AspAccessPolicy Policy, IAspObjectReader Provider, AspAuthorize Authorize);
