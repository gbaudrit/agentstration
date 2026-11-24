# ADR-0256: Transformation Schemas have explicit function exposure

## Status

Proposed

## Context

[#1214](https://github.com/gbaudrit/agentstration/issues/1214) requires an independent, reusable declaration before a named function exists. ADRs 0169–0171 provide native-content Schema versions and format providers; ADR-0233 provides native functions and acceptance snapshots. ADRs 0226 and 0235 already own JSON sub-schema selection and record publication.

## Decision

Reuse `Schema` / `SchemaVersion` with format `xml-json-transformation` and dialect `agentstration.io/xml-json-transformation/v1`. Its JSON native content declares exact published input/output Schema references, an exact governed engine FunctionReference, and bounded declarative rules. Draft authoring validates syntax through the format registry. Dependencies are resolved and authorized when explicitly exposing the published declaration; declaration publication itself does not expose a function. No new resource table, parallel mapping registry or relational migration is needed.

`TransformationFunctionExposure` is a Bootstrap instruction, not another persisted business resource. The Console action and `POST /api/transformation-functions/expose-schema` call the same function application service. Exposure appends an immutable function version; equivalent retries preserve identity and ETag, conflicting versions fail. Bootstrap `Schema` supports the optional `definition.publishVersion` instruction, removed by ordinary Schema deserialization. The three declarations can be bootstrapped without an exposure instruction. Planning uses preceding Schema documents without writing resources.

The function version stores server-resolved declaration, engine, input and output snapshots with exact scope, namespace, UID, generation, version and hashes. Flow/Source acceptance retains the resolved function through existing mechanisms. Execution checks current dependency authorization/availability and compares exact evidence; it never follows an active or newer version. A new declaration or contract requires explicit exposure/adoption. Inspection and ordinary export retain the evidence. Descendant use checks apply to every dependency, including when an instance declaration is exposed by a workspace.

The initial local provider is `builtin/xml-xsd@1.0`, format `xml-xsd`, XSD 1.0. Native schema and instance content are JSON strings carrying XML, not JSON Schema. `xml.to_json@1.0.0` is a compiled native engine in the product Functions module; the independent native SDK remains product-neutral. The exposed function accepts the JSON wire object `{ "value": "<rss>...</rss>" }` and returns the chosen JSON object. The application validates XML content against the frozen XSD before mapping and the output against the frozen JSON Schema afterward. The HTTP acquisition envelope requires explicit `json.select` of `/content` first. XSDs never describe invocation envelopes.

Rules use only child QName paths, a leading `/` for document roots, terminal attributes (`@url`) or `text()`, and `.` for the current node. Namespaces are explicit prefix/URI aliases; unprefixed paths select no-namespace elements. There are no predicates, descendant axes, extension functions, expressions, scripts or remote resources. Objects require exactly one selected node. Missing optional scalar/object fields are omitted; missing required or ambiguous scalar paths fail. Empty present strings remain empty. Arrays contain objects and always remain arrays, including zero/one entries, in document order. Opaque GUIDs and HTML in escaped text/CDATA remain strings. Nested markup is rejected as a scalar. Numeric and boolean conversion is explicit and culture invariant; dates require an explicit RSS RFC 1123/numeric offset or ISO timestamp and normalize to UTC RFC 3339. No locale inference or current-time default is used.

XML is limited to 64 KiB, depth 32, 8192 reader nodes and 64 attributes per element. Rules have at most 128 nodes, nesting 8 and paths of 512 characters / 16 segments. A mapping allows 32768 operations, at most 100 selected array members and 64 KiB of output; cancellation is checked while parsing/mapping. DTDs, entity expansion, external resolvers, XSD import/include/redefine and XSD regex patterns are rejected. Validation errors are bounded and do not echo source content. The provider is intentionally a standalone RSS profile validator, not a general XML platform.

Whole-feed JSON uses existing singleton object publication. Explicit `json.explode` selects `/items` with the exact JSON item sub-schema and retains one logical Artifact with multiple document rows or local JSON files. Empty feeds map to `items: []`; optional explosion then fails the existing non-empty publication contract. Existing preparation/sealing/activation and replay rules apply without changes. No successful partial publication or synthetic empty-feed item is introduced.

Initialization target 11 adds the engine and inherited grants to existing local instances. No external service, network, LLM or PostgreSQL is required. PostgreSQL evidence remains opt-in and distinct from deterministic document fixtures. Deferred UX scenario #1215 stays gated on explicit maintainer UX validation and implementation merge; #1214 must not auto-close.

## Consequences

The resource choice preserves existing Schema editing/version inspection and the existing Function selectors for Data Sources, Knowledge Sources and ordinary Flows. The executable first vertical deliberately limits XML expressiveness. Broader XML formats, XSLT and automatic schema conversion require separate owning decisions. A format provider being installed does not grant use of its schemas or expose any user-named function.
