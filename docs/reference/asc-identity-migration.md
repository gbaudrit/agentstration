# ASC identity replacement

The unreleased storage SDK now implements Agentstration Storage Contract over MCP,
displayed as `ASC.mcp.v1`. See [ADR-0264](../decisions/0264-storage-contract-identity-and-mcp-exposure.md)
and [issue #1377](https://github.com/gbaudrit/agentstration/issues/1377).

## Identity inventory

| Previous identity | Current identity |
| --- | --- |
| `asp/`, `Asp.slnx` | `asc/`, `Asc.slnx` |
| `Agentstration.Asp.*`, `Asp*`, `IAsp*` | `Agentstration.Asc.*`, `Asc*`, `IAsc*` |
| `AspProtocol` | `AscContract` |
| Agentstration Storage Protocol | Agentstration Storage Contract |
| No common exposure declaration | `agentstration.storage.contracts.mcp.v1`, revision `1.0`, MCP `2026-07-28` |
| `asp` mapping property | `asc`, with required `contract`, `revision`, `mcpProtocolVersion` |
| `aspVersion` response property | `ascVersion` |
| `asp.content/1`, `asp.documents/1` | `asc.content/1`, `asc.documents/1` |
| `asp.document-publication/1` | `asc.document-publication/1` |
| Default `asp_*`, `aspdoc_*` Tools | `asc_*`, `ascdoc_*` |
| `X-Agentstration-Asp-Binding` | `X-Agentstration-Asc-Binding` |
| `AGENTSTRATION_TEST_ASP_POSTGRES` | `AGENTSTRATION_TEST_ASC_POSTGRES` |

Storage-specific `asp` strings in reference/session domains, examples, test
databases and build/deployment paths become `asc` too. The SDK JSON schemas now
use ASC public type names, profile constants and response properties; regenerate
content fixtures with the LocalStorage sample's `--schemas` option. Schema and
contribution digests are recomputed from the new canonical shapes, never copied
from old descriptors. Operation suffixes, limits and error codes remain invariant.

AEP `aep.storage-types@1.0`, `aep.storage-documents@1.0`, contribution kind
`storage-type` and `/aep/storage/bindings` remain discovery/configuration contracts.
OData query language identity and document model pins remain independent. Only
explicit capabilities and query profiles authorize supported behavior.

## Updating development state

Start from a reset development state, with the product, SDK and providers built
from the same revision, and bootstrap a fresh instance using the current
configuration. No migration of existing ASP state is provided.

## Historical references

Historical ADRs, release notes and AI defect records intentionally
retain ASP terminology. ASP.NET Core, its Identity tables and Aspire are unrelated
and retain their existing names.

The intentional storage-specific historical references are ADR-0192, ADR-0193,
ADR-0199, ADR-0200, ADR-0201, ADR-0202, ADR-0205, ADR-0206, ADR-0207, ADR-0212,
ADR-0220, ADR-0221, ADR-0223, ADR-0226, ADR-0236 and ADR-0237, plus their entries
in the ADR index. ADR-0264 and this inventory use ASP only to explain the
replacement. Other remaining ASP matches refer to ASP.NET Core or Aspire.
