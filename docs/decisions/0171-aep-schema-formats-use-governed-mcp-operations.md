# ADR-0171: AEP Schema formats use governed MCP operations

## Status

Accepted

## Context

ADR-0169 and ADR-0170 establish a format-neutral Schema resource, an explicit provider registry, and a built-in JSON Schema handler that must work without AEP. Extensions still need a way to contribute additional formats, or alternative implementations of an existing format, without loading extension code into the Agentstration process or creating another invocation protocol.

A Schema format operation is an internal service capability. Publishing it as an Agent Tool would incorrectly make it assignable to agents and would bypass the Schema registry's capability, availability and ambiguity rules.

## Decision

AEP `1.0` registers the `aep.schema-formats` capability and `schema-format` contribution kind. Each contribution declares a stable id, format, display name, version, supported dialects, default marker, and mappings for both `validateDefinition` and `validateInstance`. Each operation maps to a declared MCP server and tool. MCP remains authoritative for invocation and result transport.

Agentstration discovers these contributions only from enabled, visible `ExtensionRegistration` resources. The registration remains the owner of the endpoint, expected extension identity, scope and credential. The adapter reuses the governed AEP discovery and MCP transport, applies bounded operation timeouts, validates the structured result, and registers provider identities as `<extensionId>/<contributionId>@<version>` through the Schema format registry.

Discovery retains the last known contribution descriptors so that a temporarily unreachable extension remains explicitly visible as unavailable. Resolution continues to follow ADR-0170: the built-in provider stays the standalone default, a caller can pin an exact provider, and competing unpinned providers cannot silently replace one another.

Schema-format MCP operations are not Tool contributions, are not projected into the Tool catalog, and cannot be assigned to an Agent. An extension may separately publish ordinary Tool contributions, which retain their existing lifecycle and governance.

## Consequences

- Extensions can contribute multiple Schema formats without introducing an in-process plug-in boundary.
- Definition and instance validation reuse AEP registration identity, credentials, MCP transport and failure semantics.
- Installing an extension cannot disable or silently replace the native JSON Schema handler.
- Provider unavailability, malformed output and timeouts fail explicitly at the Schema boundary.
- Extension authors must expose bounded structured validation results and both required operation mappings.
