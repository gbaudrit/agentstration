# Forge contract over MCP

The standalone Agentstration Forge Contract SDK implements
`agentstration.forge.contracts.mcp.v1` (`AFC.mcp.v1`). It supplies typed MCP client/server
adapters, optional existing-AEP declarations and deterministic authenticated fixtures.
See the [SDK README](https://github.com/gbaudrit/agentstration/blob/main/afc/README.md),
[normative specification](https://github.com/gbaudrit/agentstration/blob/main/afc/docs/contract.md)
and [ADR-0288](../decisions/0288-forge-operations-are-an-independent-contract-over-mcp.md).

Callers resolve native repository/ref identity, create/read change requests and request
revision-bound review/check observations through explicit capabilities. Current target
permissions are checked even during recovery. Exact expected SHAs do not establish
native atomicity by themselves; uncertain writes retain their stable operation key and
may include partial artifact evidence rather than silently creating another request.

Tracking, forge and Git transport remain separate. An external tracking reference is
an association, not a tracker permission. Native approval/check evidence never accepts
local work or authorizes merge. The fixture needs neither ATC nor a vendor/LLM account.

This increment supplies the independently packable contract foundation. #951 owns
Project connection adoption, #958/#959 vendor adaptation and #1349 product delivery
integration. The sample has bounded process-local receipts and restart fencing; it is
not production persistence or a guarantee about live vendor behavior. No Console or
Workplace journey, REST adapter or new product process is introduced.
