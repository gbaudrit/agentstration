# ADR-0193: ASP Tools select logical targets and Agentstration owns parameter relays

## Status

Accepted

## Context

The maintainer clarified #1038/#1032 after reviewing the initial ASP implementation:
extensions must expose generic Tools callable by any authorized consumer, including
Agents granted direct access. Selecting a logical target and authenticating its
caller are separate responsibilities. The product's ability to relay any existing
Tool with fixed parameters belongs in Agentstration, not in the ASP SDK.

ADR-0192 placed target pinning in each extension-side MCP Tool. That conflicts with
the clarified generic extension contract and makes Tool definitions grow with the
number of storage targets. Different physical backends also need provider-defined
configuration without embedding a universal bucket/path/table schema in ASP.

## Decision

Supersede ADR-0192's per-target Tool model. Publish one generic MCP operation group
for the ASP content profile. Every invocation has `{ store, arguments }`, where
`store` is an exact logical `AspStoreRef` (store, binding, configuration revision)
and `arguments` contains typed operation inputs. A caller-selected target is a
selector, never authority. Resolve it against extension-controlled mappings and
reauthorize every operation before invoking the provider. Unknown targets/revisions
and unauthorized selections fail explicitly.

The server SDK registers `AspStoreBinding` entries, each mapping an exact logical
reference to a provider and maximum access policy. Intersect that policy with the
caller's current grant. Providers enforce the resulting context even when several
targets share physical placement. Signed references and cursors remain bound to
exact target, subject, authorization revision and effective restrictions.

Keep logical identifiers opaque and physical configuration provider-owned. An
extension may define JSON configuration for object stores, files, relational stores
or other backends through its own versioned schema. ASP defines no closed backend
enum or universal physical locator. A binding revision identifies the exact configured
mapping. AEP/configuration integration owns provisioning and discovery; content
Tools select configured targets and cannot create or remap them through arguments.
Only implemented operation profiles are advertised. SQL placement does not imply
a query capability, and this delivery still implements only `asp.content/1`.

Agentstration's parameter relay is a generic product Tool capability owned by #1040. It may fix
ASP target selection or parameters of unrelated Tools, hide fixed parameters from
Agent input, reject overrides and invoke the original Tool under normal governance.
ASP supplies no relay component, product Tool model or automatic publication. An
Agent with explicit rights to a generic extension Tool may call it directly.

Retain ADR-0192's independent packages, bounded content operations, conditional
writes and retry semantics, offline defaults and explicit absence of deletion,
stable-version and retention guarantees. The normative revised wire profile is in
[the ASP specification](https://github.com/gbaudrit/agentstration/blob/main/asp/docs/protocol.md).

## Consequences

- One stable Tool schema serves multiple logical targets and physical providers.
- Native MCP callers select targets explicitly; authorization remains server-owned.
- The typed client selects a target and inserts it into each request without
  becoming a product Tool relay.
- Product relay implementation and storage resource/AEP integration remain separate
  from the SDK; no additional product process or UX dependency is introduced here.
- The initial unpublished ASP input schemas change; release consumers must use the
  revised envelope. Existing Agentstration product APIs and Artifacts are unchanged.
