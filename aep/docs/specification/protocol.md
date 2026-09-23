# AEP protocol specification

## Versions

The protocol version is `2026-09-22`. Protocol, SDK, extension, and consuming-product versions are independent.

## Discovery

Every extension exposes:

```http
GET /.well-known/aep
```

The response is an `AepManifest` containing `protocolVersion`, extension identity, and a map of versioned capabilities. Capability names are open-ended and namespaced; clients must not infer support from extension identity.

```json
{
  "protocolVersion": "2026-09-22",
  "extension": { "id": "sample.hello", "name": "Hello", "version": "1.0.0" },
  "capabilities": {
    "aep.health": { "version": "1.0", "endpoint": "/aep/health" }
  }
}
```

`GET /.well-known/agentstration` is a temporary compatibility alias and is not the canonical discovery route.

## Core health

`GET /aep/health` returns an `AepHealth` document. The host may additionally expose platform readiness endpoints such as `/health`; those are outside the protocol.

## Capabilities

Initial registered names are:

- `aep.health`
- `aep.model-provider`
- `aep.source-provider`
- `aep.tools`
- `aep.configuration`
- `aep.value-requirements`
- `aep.bound-values`
- `aep.secret-access`

Unknown names are preserved. Each capability evolves through its own version and may declare an endpoint and metadata. The manifest is the only input an Inspector needs to decide which explorers to display.

The model-provider capability currently uses `/aep/model-providers`. AEP tool contributions may map to MCP servers; MCP remains authoritative for tool schema and invocation.

The model-provider capability is version `2.0`. `POST /aep/model-providers/{providerId}/models` returns bounded model observations. Each descriptor has a provider-local `id`, a `displayName`, an optional safe `identity` (`publisher`, `model`, and `version`) and an optional typed `specification`. The specification directly carries observed `input`, `output`, `features`, and `limits`; it does not contain a generic capabilities dictionary or provider metadata bag.

Content types are `text`, `image`, and `audio`. Initial features are `streaming`, `tools`, `structuredOutput`, and `reasoning`. Feature support is `unknown`, `unsupported`, `native`, `emulated`, or `partial`. A missing feature or `unknown` support means it was not observed; it must not be interpreted as unsupported. Tool modes, structured-output formats, and reasoning efforts (`none`, `minimal`, `low`, `medium`, and `high`) are typed maps so future properties can be added to each entry. Limits are positive token counts when observed.

Responses contain at most 1,000 models. Identifiers and display names are at most 256 characters; identity parts are at most 128 characters; strings are trimmed and contain no control characters. Content lists and feature-detail maps are bounded to 16 unique entries. Duplicate model identifiers, non-positive limits, and details attached to an explicitly unsupported feature are invalid. Both the server SDK and canonical client enforce these invariants.

### Value requirements and bound values

An extension may declare logical, contribution-scoped Value Requirements in the discovery manifest. When the list is nonempty, it advertises `aep.value-requirements` and `aep.bound-values` version `1.0`. These capabilities have no separate discovery endpoint: `valueRequirements` is the complete declaration. Extensions with no requirements omit both capabilities and the list.

```json
{
  "capabilities": {
    "aep.value-requirements": { "version": "1.0" },
    "aep.bound-values": { "version": "1.0" }
  },
  "valueRequirements": [
    { "contributionKind": "model-provider", "contributionId": "foundry", "id": "credential", "required": true, "type": "string", "protection": "secured", "description": "API credential" },
    { "contributionKind": "model-provider", "contributionId": "foundry", "id": "project-endpoint", "required": true, "type": "string", "protection": "standard", "format": "uri" },
    { "contributionKind": "model-provider", "contributionId": "foundry", "id": "authentication-mode", "required": true, "type": "string", "protection": "standard", "allowedValues": ["ApiKey", "ManagedIdentity", "WorkloadIdentity", "Development"] }
  ]
}
```

Each identifier is a stable contribution-contract name: 1–64 characters, beginning with a lowercase ASCII letter, then lowercase letters, digits, `.`, `_` or `-`. Duplicate identifiers are rejected within one contribution. A requirement declares scalar type (`string`, `integer`, `number`, or `boolean`), optional format, required status and `standard` or `secured` protection. It contains no Parameter or Secret name, resource reference, scope or value.

A standard requirement may declare one to 64 typed `allowedValues`, with a maximum combined serialized size of 65,536 bytes. Every entry must match the declared scalar type and values must be unique; string comparison is ordinal and case-sensitive. An absent list leaves the value unconstrained. A secured requirement cannot declare allowed values, and a constrained requirement must use an inline bound value rather than a Secret grant. These rules keep validation deterministic without resolving or exposing Secret material.

Model discovery uses `POST /aep/model-providers/{providerId}/models` with an `AepBoundValuesRequest`. Chat and streaming carry the same `boundValues` list in `AepChatRequest`. Each entry is exactly one inline JSON scalar or one Secret grant. Inline values are bounded to 65,536 serialized bytes. A secured requirement rejects inline values, and an inline value outside its requirement's allowed set is rejected. Before contribution invocation, the server rejects duplicate, unknown, missing, malformed, oversized, mistyped, disallowed and wrong-protection entries with value-free error messages.

### Bound Secret access

An extension that supports secured bound values advertises `aep.secret-access` version `1.0` in its manifest capabilities. `AepValueResolver.ResolveAsync(requirementId)` provides one logical lookup for inline and secured values. An inline value is returned locally. For a secured entry, the resolver uses `AepSecretAccessClient` to redeem its grant. Each grant supplies `version`, `endpoint`, `extensionId`, `requirementId`, `executionId`, and an opaque `secretCapability`; it never supplies a Secret or Vault name. The host replies with `version` and `secretValueBase64`; disposing the resolved value erases its owned Secret byte buffer.

The host callback URL must use HTTPS, except for loopback HTTP. The host limits values to 65,536 bytes, consumes each capability once, and revokes unused capabilities at the end of the call. Errors use AEP's `AepErrorResponse` envelope and value-free codes such as `capability_invalid`, `capability_expired`, `context_mismatch`, `access_denied`, `secret_unavailable`, `vault_unavailable`, `secret_value_invalid`, and `secret_access_version_unsupported`. Callback grants are independent of `IAepAccessTokenProvider` and `StaticBearer` host-to-extension authentication. If a secured binding is supplied to an extension that does not advertise `aep.secret-access` version `1.0`, invocation fails closed.

### Source providers

An extension that advertises `aep.source-provider` exposes descriptors at `/aep/source-providers`. Each `source-provider` contribution uses an immutable configuration option set whose scope is `source-channel`.

`POST /aep/source-providers/{providerId}/resolve` accepts the complete versioned Channel configuration envelope and returns an immutable provider revision plus provider-defined integrity algorithm and digest. `POST /aep/source-providers/{providerId}/materialize` accepts the same configuration, the exact resolved revision, and positive limits for archive bytes, entry count, expanded bytes, and execution time. It returns an opaque archive with the exact revision, media type, declared sizes, and SHA-256 content digest.

The server intersects client limits with its own configured maxima. Revision mismatches, digest mismatches, invalid option envelopes, provider timeouts, and exceeded limits fail closed through stable AEP errors. Consumers must also enforce the declared limits and integrity before extracting an archive. Archive paths and contents are not interpreted by AEP.

Source providers own acquisition only. Source identity, version and Channel lifecycle, snapshot retention, path isolation, and interpretation of Agentstration catalogs, Bootstrap Profiles, or Packs remain consumer responsibilities. Source acquisition is not an MCP Tool capability.

### Versioned configuration

An extension that advertises `aep.configuration` exposes an `AepConfigurationCatalog` at the capability endpoint, conventionally `/aep/configuration`. An option set identifies one contribution and one scope, declares a preferred authoring version, and publishes all supported immutable versions with their JSON Schema and SHA-256 digest. Model-provider options use `model-profile`; Source Provider options use `source-channel`.

Native request options carry `optionSet`, exact `version`, `schemaDigest`, and an object-valued `values` member. Servers reject an unknown set, removed version, changed digest, or schema-invalid value before invoking the contribution. Changing a schema requires a new option-set version; changing the preferred version does not migrate pinned requests.

An extension may register directed migration edges between versions of the same option set. The catalog publishes those edges in `migrations`. A client requests an explicit migration at `/aep/configuration/migrate` with the source set, version, digest, values, and target version. The server selects a path, validates the source and every intermediate result, and returns a complete target `AepVersionedOptions` envelope. There is no implicit migration during discovery or contribution invocation.

## Security

Endpoints must not embed credentials. Clients and inspectors must redact authorization, cookies, tokens, passwords, API keys, and complete `boundValues` properties, including inline nonsecret values. Protocol traces are diagnostic data and must be bounded and treated as sensitive.

Extensions may require workload authentication for the complete AEP protocol surface. The initial SDK profile uses an opaque static Bearer with at least 256 bits of random secret material. The server stores token digests, compares them in constant time, and creates a workload principal with a stable client ID, non-secret token ID, and explicit permissions. Multiple active token IDs allow overlap during rotation.

When this profile is enabled, `/.well-known/aep`, its compatibility alias, `/aep/health`, and every discovery, configuration, migration, provider, chat, streaming, and source-provider operation require the `aep.invoke` permission. Missing or invalid authentication returns `401`; an authenticated workload without the permission returns `403`. A minimal host `/health` endpoint may remain anonymous because it is outside AEP and exposes no extension details.

Authentication is optional at SDK composition level: a host may instead apply another ASP.NET Core scheme and policy. Human cookies, personal access tokens, and interactive identity are never AEP workload credentials.

Clients resolve every protocol and discovered capability path against the registered base URI and reject a change of scheme, host, or port before constructing the request. Automatic redirects must be disabled by the HTTP transport, so an Authorization header cannot cross a transport transition. Remote endpoints require HTTPS; HTTP is limited to explicitly allowed local development hosts.

The secure .NET transport resolves DNS for each new pooled connection and rejects unspecified, multicast, link-local, cloud-metadata, and—unless explicitly allowed for a named local service—private addresses. Connection lifetime is bounded so DNS policy is re-evaluated. Unary response size, streaming line size, update count, total streamed characters, connection time, and caller cancellation are bounded. TLS certificate and hostname validation use the platform defaults and are never bypassed.
