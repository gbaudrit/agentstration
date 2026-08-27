# AEP protocol specification

## Versions

The protocol version is `2026-08-01`. Protocol, SDK, extension, and consuming-product versions are independent.

## Discovery

Every extension exposes:

```http
GET /.well-known/aep
```

The response is an `AepManifest` containing `protocolVersion`, extension identity, and a map of versioned capabilities. Capability names are open-ended and namespaced; clients must not infer support from extension identity.

```json
{
  "protocolVersion": "2026-08-01",
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
- `aep.tools`
- `aep.configuration`

Unknown names are preserved. Each capability evolves through its own version and may declare an endpoint and metadata. The manifest is the only input an Inspector needs to decide which explorers to display.

The model-provider capability currently uses `/aep/model-providers`. AEP tool contributions may map to MCP servers; MCP remains authoritative for tool schema and invocation.

### Versioned configuration

An extension that advertises `aep.configuration` exposes an `AepConfigurationCatalog` at the capability endpoint, conventionally `/aep/configuration`. An option set identifies one contribution and one scope, declares a preferred authoring version, and publishes all supported immutable versions with their JSON Schema and SHA-256 digest.

Native request options carry `optionSet`, exact `version`, `schemaDigest`, and an object-valued `values` member. Servers reject an unknown set, removed version, changed digest, or schema-invalid value before invoking the contribution. Changing a schema requires a new option-set version; changing the preferred version does not migrate pinned requests.

An extension may register directed migration edges between versions of the same option set. The catalog publishes those edges in `migrations`. A client requests an explicit migration at `/aep/configuration/migrate` with the source set, version, digest, values, and target version. The server selects a path, validates the source and every intermediate result, and returns a complete target `AepVersionedOptions` envelope. There is no implicit migration during discovery or contribution invocation.

## Security

Endpoints must not embed credentials. Clients and inspectors must redact authorization, cookies, tokens, passwords, API keys, and properties explicitly described as secret. Protocol traces are diagnostic data and must be bounded and treated as sensitive.
