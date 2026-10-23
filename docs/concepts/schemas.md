# Schemas

A Schema is a reusable, scoped contract resource. Its identity, ownership scope, namespace, format and dialect remain stable while its draft content can be edited with optimistic concurrency. Publishing creates an immutable version containing an independent content snapshot and deterministic digest.

## Consumer bindings

A Flow draft can keep its historical inline JSON Schema fields or use a `SchemaBinding`. A binding contains exactly one inline definition or one exact published Schema-version reference. References default to the Flow's Workspace and namespace when their scope or namespace is omitted; an explicit parent-scope reference is usable only when the Schema grants descendant use.

Flow publication resolves bindings using the format registry and requires `json-schema`, the format understood by Flow execution. The published Flow stores the effective JSON contract in its legacy runtime fields plus provenance containing the source address, version, UID, generation and digest. Execution never resolves a Schema again, so changing a Schema draft or losing an AEP format provider cannot change an already-published Flow version.

Missing versions, denied scopes, incompatible formats, unavailable providers and invalid definitions fail publication with stable diagnostics. Bindings do not compose or transform schemas.

## Agent and Tool invocation contracts

Agent and Agentstration-owned Tool definitions can bind their input and output contracts to either an inline JSON Schema or an exact published Schema version. The Console exposes the same selector used by Flow contracts, so a reusable contract can be pinned without copying its document.

Bindings are resolved when the editable resource is saved and, for Agents, snapshotted again when an immutable revision is created. The effective JSON Schema remains materialized beside its provenance so existing execution paths do not resolve mutable resources at runtime. Agent execution continues to exchange its current text input and output; these contracts govern authoring and invocation compatibility without changing the runtime protocol.

Externally discovered MCP or AEP Tool schemas remain provider-owned and read-only. Reusable Schema bindings apply only to Tool definitions owned by Agentstration.

The Console Schema library is available under **Design > Schemas**. The **Definition** tab owns identity and description, while the **Designer** tab edits the native document in three modes:

- **Visual** uses a compact recursive property tree and a single inspector for the selected node. Properties can be renamed, reordered among siblings, nested under objects, marked as required, and configured with primitive or collection types, descriptions, defaults, string enums, and basic numeric or string bounds.
- **Source** uses the local Monaco editor and preserves the complete native document.
- **Preview** renders a sample instance form and validates the sample against the current definition.

Definitions containing composition or reference constructs such as `$ref`, `$defs`, `oneOf`, `anyOf`, `allOf`, conditional schemas or tuple arrays stay in source mode. The Console never converts those documents through the visual model, so opening and saving source does not silently discard content.

## Built-in JSON Schema support

The standalone product registers a native `json-schema` provider and does not require AEP. The supported dialect is `https://json-schema.org/draft/2020-12/schema`.

The bounded handler validates object definitions and instances for `type`, `properties`, `required`, boolean `additionalProperties`, homogeneous `items`, `enum`, `default`, `examples`, descriptions and titles, numeric bounds, string and array length bounds, `pattern`, and the `uri` format. Unsupported keywords and dialects return path-specific diagnostics. This bounded behavior is deliberate: it is safer to report an unsupported construct than to claim validation while ignoring it.

Format resolution is capability-based. A provider has a stable identity and version, lists its dialects and capabilities, and reports availability. Multiple capable providers require one explicit default or an exact caller pin; registration order is never used as an implicit selection policy.

## AEP format contributions

An enabled AEP Extension Registration may contribute one or more additional Schema format handlers through the `aep.schema-formats` capability. Each contribution maps definition and instance validation to private MCP operations. These operations reuse the registration's scope, expected extension identity and credential, but they are not Agent Tools and never appear in the Tool catalog.

The adapter validates bounded structured results and enforces an operation timeout. If an extension that was discovered previously becomes unreachable, its providers remain inspectable as unavailable rather than disappearing silently. The built-in JSON Schema provider remains independent and the default; an AEP implementation for the same format must be selected by the registry's explicit default or exact provider-pin rules.
