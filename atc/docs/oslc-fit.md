# OSLC Change Management fit assessment

Baseline: official [OSLC CM specification](https://docs.oasis-open-projects.org/oslc-op/cm/v3.0/change-mgt-spec.html)
and [CM resource constraints](https://docs.oasis-open-projects.org/oslc-op/cm/v3.0/os/change-mgt-shapes.html),
reviewed for #1378 on 2026-11-26.

OSLC CM models change requests with native status/state, parent relationships,
loosely related change requests and links to other lifecycle resources. Its state
change model can use OSLC Actions; a status string is not a generic writable workflow.
It distinguishes closed, fixed and verified concepts.

| Concern | ATC decision |
| --- | --- |
| Provider-neutral tracking resource | Use an opaque qualified item identity; do not require a repository or product Work aggregate |
| Parent versus related | Preserve separate relation kinds and native inverse evidence |
| Native status and transitions | Retain native state/completion and advertised transition IDs; no universal status coercion |
| Cross-domain lifecycle links | Keep explicit identity references and separate tracking from forge/work orchestration |
| RDF resource shapes and OSLC HTTP services | Not adopted as the ATC wire: current consumers require typed JSON MCP Tools, explicit logical binding authority and recovery receipts |
| Mutation replay and uncertain native effects | Specify operation-key/intent/receipt semantics explicitly; OSLC vocabulary alone does not supply these guarantees |

ATC reuses these distinctions conceptually, without claiming OSLC conformance or
inventing OSLC URIs for its new MCP operations. An actual OSLC provider can adapt its
native resources/actions through ATC while declaring only executable capabilities.
The SDK does not require an OSLC client/server stack or translate every provider into
one universal state/hierarchy model.
