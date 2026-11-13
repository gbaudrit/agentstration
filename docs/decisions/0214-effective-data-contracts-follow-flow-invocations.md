# ADR-0214: Effective data contracts follow Flow invocations

## Status

Proposed

## Context

Feature [#1146](https://github.com/gbaudrit/agentstration/issues/1146) extends
[ADR-0213](0213-artifact-transformations-own-scoped-content-processing.md). An
Artifact manifest describes references, whereas its data contract describes the
stored content. Conflating these boundaries causes JSON errors far from their
origin. An undeclared Agent or nested Flow also needs the parent's expectation
without changing its resource definition or adding arbitrary business arguments.

## Decision

Use the existing Schemas module and built-in bounded JSON Schema handler for
concrete data-contract validation. Distinguish an absent declaration, a fixed
schema/MIME declaration, and an explicitly dynamic declaration resolved from the
input data contract or parent output expectation. Missing dynamic context fails;
absence does not silently become a persisted dynamic declaration. A resolved
contract records its source, exact schema/MIME digest and validation evidence.
Unsupported definitions fail explicitly; no schema network lookup is performed.
Authorized opaque Secret references are validated as symbolic strings at durable
Flow boundaries. Value-dependent constraints are deferred until the existing
secret-resolution boundary has the value; other fields remain fully validated.
Validation diagnostics never resolve or disclose a credential.

Flow publication snapshots declared Agent and direct Tool schemas and resolved
called-Flow schemas. Existing published versions are immutable. A final Agent's
fixed output becomes an undeclared Flow's effective output only through unchanged
forwarding. An explicit output mapping prevents automatic inheritance. Tools
called directly require fixed input and output schemas, including an explicitly
broad JSON schema where appropriate. A changed Tool contract requires republishing
the calling Flow. Tool calls made by Agents retain their existing Tool protocol.

An invocation carries trusted input-contract and output-expectation context across
undeclared child Flow inputs. Explicit child inputs stop implicit inheritance.
Agent system instructions receive this invocation context, separately from the
business input. Fixed Agent outputs take precedence over inferred expectations;
the Worker parses and validates their response before forwarding it. A declared
`outputContract` input parameter receives `{ schema, mediaType }` only when that
parameter exists in a Tool or called Flow input schema. An explicitly supplied
conflicting value fails. Other parameter names and Tool resource definitions are
unchanged. A Tool with exactly one business parameter can receive the complete
upstream value under that parameter when no mapping is authored.

The Artifact transform expectation applies to the final selected content, after
result mapping. It does not impose that shape on every intermediate Agent.
Access mode validates the completed output draft; the final chat acknowledgement
is separate. A fixed producer output cannot become dynamic by input inheritance.
The authority validates actual staged/stored content before recording contract
validation evidence. Inline direct handoffs retain concrete metadata; they still
create no intermediate durable content. Snapshot publication preserves evidence.

Contract failures carry a stable code, boundary, safe declared-data path,
expected/received type or MIME and originating run/step. Child, processing and root
execution preserve the cause even through an error Output. The AWP authority
returns governed HTTP 422 diagnostics, and failed assignment details survive both
SQLite and PostgreSQL persistence. Diagnostics contain no instance values.
Designer inspection shows declaration metadata; runtime inspection shows concrete
execution metadata. These are not interchangeable evidence.

## Consequences

Existing undeclared Flows remain supported. Direct Tool use now requires explicit
schemas and can require republishing; no released-version migration is needed for
this development stack. Validation proves format compatibility, not semantic
correctness or anonymization quality. Existing content size, UTF-8, expiry,
Workspace authorization and execution-owned capability limits continue to apply.
A provider-specific structured-output API is optional: system instructions and
platform validation remain the portable baseline.

Reusable browser coverage is tracked separately by native child
[#1147](https://github.com/gbaudrit/agentstration/issues/1147), pending maintainer
UX validation and merge. Existing regression and component checks remain required.

External MCP/AEP Tool output schemas remain provider-owned and describe
`structuredContent`. A Flow keeps the full MCP result envelope; its port contract
wraps the business schema under `structuredContent` and validation checks that
nested content. Internal Tools keep their ordinary result shape. The platform
does not rewrite external Tool declarations or unwrap their execution results.
