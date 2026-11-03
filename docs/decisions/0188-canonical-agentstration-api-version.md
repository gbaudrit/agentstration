# ADR-0188: Agentstration resources and AEP use agentstration.io/v1

## Status

Accepted — 2026-11-03

## Context

[#374](https://github.com/gbaudrit/agentstration/issues/374) identifies inconsistent schema identifiers: resources already use `agentstration.io/v1`, Runtime Runs use a dated identifier, and autonomous AEP uses another dated identifier. Date-named aliases also obscure the actual resource wire value. The maintainer confirmed that no prior contract has shipped and no backward compatibility is required.

## Decision

- `agentstration.io/v1` is the only accepted and emitted Agentstration resource API and AEP protocol version. It identifies a stable schema, independently of product, package and release dates.
- Agentstration resources, including Runtime Runs, use `ResourceApiVersions.CoreV1` directly. Remove date-named aliases and the separate Runtime and Workplace version catalogues.
- AEP owns `AepProtocol.Version` with the same literal wire value. It remains autonomous and does not depend on Agentstration Resources, Management, Runtime, Infrastructure or Web.
- Keep `apiVersion` and `protocolVersion` property names, resource kinds, routes, ETags and identities unchanged. Capability versions, model identities and third-party protocol versions retain their own meaning.
- AEP clients and validators reject unsupported protocol identifiers through their existing error paths. Runtime Run documents and explicit Run API version queries reject noncanonical identifiers with `api_version_not_supported`.
- Do not add dated aliases, fallback negotiation, temporary legacy acceptance or data migration. Rebuild development peers together; recreate disposable development Runtime data containing dated versions.
- Contract and architecture tests pin the canonical wire identifiers and prohibit dated API-version constants.

## Consequences

Serialization, persistence, discovery, clients, SDKs, CLI, Inspector and official extensions converge on one schema identifier. Breaking changes to this schema require an explicit future version decision. Sharing the wire value does not create a shared assembly dependency or merge resource and AEP lifecycle ownership.

This decision supersedes only the dated API/protocol-version nomenclature in ADR-0030, ADR-0121 and ADR-0137. Their dependency, security and model-observation decisions remain accepted. Historical ADR text and release notes retain the identifiers observed at their publication time.
