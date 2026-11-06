using Agentstration.Asp.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Asp.Server;

// The callback must authenticate/authorize EVERY call from trusted transport state.
// It must never construct a grant from business arguments or a caller-supplied store ID.
public delegate ValueTask<AspAccessContext> AspAuthorize(
    RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken);

public sealed record AspToolBinding(
    string Name, AspStoreRef Store, AspAccessPolicy Policy, IAspObjectReader Provider, AspAuthorize Authorize);
