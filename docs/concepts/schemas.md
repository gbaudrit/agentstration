# Schemas

A Schema is a reusable, scoped contract resource. Its identity, ownership scope, namespace, format and dialect remain stable while its draft content can be edited with optimistic concurrency. Publishing creates an immutable version containing an independent content snapshot and deterministic digest.

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
