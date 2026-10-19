# ADR-0169: Schemas are format-neutral versioned resources

## Status

Accepted

## Context

Flows, Tools and Agents currently embed JSON-shaped contracts in their own definitions. Issue #754 introduces reusable contracts while preserving the platform's provider-neutral boundaries. The resource lifecycle must exist before format handlers, consumer bindings or AEP-contributed formats, and the built-in JSON Schema path must not require an AEP extension.

## Decision

Agentstration owns a plural `Schemas` resource family. A `Schema` is an editable scoped draft identified by the ordinary resource API version, kind, namespace, name and immutable ownership scope. Its definition contains a display name, optional description, opaque format identifier, optional dialect identifier, one native standalone document and the existing explicit descendant-use policy.

The native document uses a JSON value as the transport envelope, not as the definition of the schema language. JSON Schema documents are therefore preserved as objects while future text or binary-safe encodings can be represented without changing resource identity. Multi-file composition, schema imports and transitive dependency resolution are not part of this decision.

The format and dialect form the interpretation identity of a Schema and cannot change after creation. A different interpretation requires a new Schema identity. The built-in `json-schema` identifier is recognized by the standalone product without AEP. Semantic validation remains a format-handler concern and is introduced separately; resource storage does not embed JSON Schema validation rules.

Publishing creates an immutable `SchemaVersion` in the same exact ownership scope and namespace. The version records the source UID and generation, format, dialect, an independent copy of the native content, publication time and a deterministic SHA-256 digest over a property-order-independent canonical JSON representation. Repeating publication with the same version and digest is idempotent; a different digest for an existing version is rejected.

`Schema` and `SchemaVersion` use the existing generic resource store and its optimistic ETags. SQLite and PostgreSQL require no family-specific table or migration. JSON and YAML export use the common resource manifest serializer, so native content remains within the ordinary resource envelope. Import and bootstrap handlers may compose the same family service without receiving direct storage access.

## Consequences

- Schema drafts can be authored and inspected before semantic handlers or consumer bindings exist.
- Published content remains available even when a draft changes or a format handler later becomes unavailable.
- Format-specific validation and visualization do not leak into the resource family, API endpoints or Console components.
- The first version deliberately supports one standalone native document and one built-in format identifier; additional format availability is supplied by the registry defined in a later decision.
- Descendant visibility does not grant use: instance- and Tenant-owned Schemas require the same explicit descendant-use grants as Parameters and Secrets.
