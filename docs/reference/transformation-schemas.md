# Transformation Schemas

An XML-to-JSON mapping is a normal, independent `Schema` with format
`xml-json-transformation`. Its draft and published versions are available through
the Schema Console/API before any named function exists. See
[ADR-0259](../decisions/0259-transformation-schemas-have-explicit-function-exposure.md).

The local implementation supports standalone XSD 1.0 (`xml-xsd`, native content
is an XML string), JSON Schema, and a bounded XML child/attribute mapping format.
It requires no extension, external endpoint, PostgreSQL or LLM.

## Bootstrap example

The same `rss-2-0-schemas` and `rss-2-0-functions` manifests can be installed at
Instance, Tenant or Workspace scope. The catalog ships them with
`targetScope: instance`. Before previewing an installation, set the existing
`definition.targetScope` field in **both** `profile.yaml` descriptors to the
chosen owner:

| `targetScope` | Target selected in the existing Bootstrap procedure |
| --- | --- |
| `instance` | None |
| `tenant` | Explicit Tenant |
| `workspace` | Explicit Tenant and Workspace |

The existing fixed-scope profile mechanism
([ADR-0077](../decisions/0077-bootstrap-profiles-are-explicit-administrative-applications.md))
reads the ownership scope from these descriptors. Resource manifests stay
identical, omit their installation-specific owner, and keep their
contract references relative to that owner. The engine reference remains the
exact Instance-owned `xml-to-json-builtin@1.0.0`. The preview digest includes the
profile contents, scope and target; change the descriptors **before** previewing.
Legacy flat applications require one shared scope and target. The existing Console
plan can combine per-profile targets ([ADR-0248](../decisions/0248-manual-bootstrap-plans-preserve-profile-targets.md));
keep these two sample profiles at the same target so their relative references
resolve. Changing `targetScope` does not move an existing installation to another owner.

Apply `rss-2-0-schemas` through **System > Bootstrap profiles**. This RSS 2.0
profile creates and publishes `rss-2-0-xml`, `rss-2-0-json` and `rss-2-0-mapping` at
`1.0.0`; it creates no `rss-2-0-xml-to-json` function. `definition.publishVersion` is
an optional Bootstrap instruction. Reapplying equivalent declarations preserves
their identities and versions. Changed definitions at the same identity produce
a conflict instead of replacing the operator's draft.

Then either apply `rss-2-0-functions` to the same target, or open `rss-2-0-mapping`
under **Schemas > Versions**, select `1.0.0`, and use **Expose as function**.
The two profiles may also be composed in declaration-first order in one explicit
application. Tenant and Workspace applications require their explicit target. Reapplying either
profile preserves the existing identities, published versions and accepted pins.

Ownership and use authorization are separate. A single Instance installation can
be shared with selected descendants without copying its Schemas or Function.
Schema `usePolicy.grants` identify authorized Tenant/Workspace scopes and may
include their descendants; the Function uses the existing resource access grants
(`discover`/`use`). All accepted dependencies must allow the consumer as well.
Ordinary RBAC permissions still apply. Installing a profile grants no additional
cross-scope use automatically. Install separate copies only for independently
owned versions or mappings.

Choose the function's technical name, display name and exact version. The action
requires `resources/write` and authorized use of every dependency. The API is:

```http
POST /api/transformation-functions/expose-schema
Content-Type: application/json
```

```json
{
  "name": "rss-2-0-xml-to-json",
  "displayName": "RSS 2.0 XML to JSON",
  "version": "1.0.0",
  "scopeRef": "/instance",
  "namespace": "default",
  "schema": { "name": "rss-2-0-mapping", "version": "1.0.0" }
}
```

The function inspection/export includes its accepted declaration, input/output
contracts and governed engine with exact versions, UIDs and digests. Equivalent
exposure retries reuse the existing function version. A conflicting version is
rejected; publish and explicitly expose a new version to adopt changed rules or
contracts. New publications never update an already selected consumer.

## Declaration and rules

The declaration's native JSON has `engine` (a governed FunctionReference), `input`
and `output` (exact SchemaVersionReferences), and `rules`. Omitted ownership scope
and namespace resolve relative to the declaration, never a downstream consumer.
The input selects a whole XSD; JSON output may select a supported sub-schema.
The example references the platform engine `xml-to-json-builtin@1.0.0`, whose
compiled implementation is `xml.to_json@1.0.0`.

Rules contain `namespaces` (prefix-to-URI aliases) and a root object rule. Every
rule declares `path` and `type`. Objects/arrays have a `fields` object whose keys
name output fields. For example:

```json
{
  "namespaces": { "ext": "urn:example:metadata" },
  "root": {
    "path": "/catalog", "type": "object",
    "fields": {
      "version": { "path": "@version", "type": "string" },
      "entries": {
        "path": "entry", "type": "array",
        "fields": {
          "id": { "path": "@id", "type": "string" },
          "score": { "path": "ext:score", "type": "integer", "optional": true }
        }
      }
    }
  }
}
```

Only child QName paths, document-root `/`, terminal `@attribute` or `text()`,
and `.` are supported. Prefixes must be declared; unprefixed names mean no XML
namespace. Wildcards, `//`, predicates and expression functions are rejected.
Objects/scalars require exactly one selected node. Missing optional fields are
omitted; empty present strings remain empty. Arrays preserve document order and
remain arrays for zero, one and many nodes. Ambiguous scalar paths fail.

Conversions are explicit: `string`, `integer`, `number`, `boolean`, `dateTime`.
Integers are signed 64-bit; decimals use invariant decimal notation, without
locale or exponent inference; booleans use XML lexical values. Dates accept
RFC 1123, explicit numeric-offset timestamps or ISO timestamps with a timezone,
and normalize to UTC. Opaque identifiers remain strings. Escaped HTML and CDATA
remain data; nested markup is rejected as a scalar, never rendered or executed.

XML instance bytes and structural processing use the configurable operator ceilings
below (10 MiB by default for document bytes). Rule authoring still allows 128 nodes,
nesting 8 and 512-character/16-segment paths. Mapping work and array cardinality
use the XML settings; encoded output uses the function value budget. Exceeded
limits reject the whole result.
DTDs, external entities/resolvers, XSD import/include/redefine and regex patterns
are rejected. No schemaLocation is fetched implicitly.

## Source and Flow execution

The exposed function has a JSON invocation contract `{ "value": "<xml>…</xml>" }`.
That wire contract is separate from the native XSD content contract. Input XML is
validated against its frozen XSD before conversion. The converted object is
validated against its frozen JSON Schema afterward.

For HTTP Data Source acquisition, select the exposed function and its exact
version directly as the first transformation. When the acquisition contract
requires exactly the two string fields `mediaType` and `content` and refuses
additional properties, a first function expecting a single string value receives
`content`. Literal configuration parameters remain supported. The Console explains
this selection; it is recorded as `functionInputSource: acquisitionContent` in the
accepted composition receipt and compiled into the immutable invocation mapping.
The raw response is still validated against the acquisition contract first, and
the function validates its text content against the native XML contract.

Optionally follow the conversion with `json-explode-builtin@1.0.0`, `path: /items`,
and `itemSchema.reference` selecting `rss-2-0-json@1.0.0` at
`/properties/items/items`. This uses one logical Artifact for the resulting records.

Object-valued functions receive the complete response. Later functions receive the
previous result; ordinary objects containing a `content` field are never unwrapped.
Missing, optional, nullable or non-string response content does not trigger this
adaptation. Explicit `json-select-builtin@1.0.0` with `path: /content` remains valid
and is required for other envelope shapes. An incompatible edit fails before
persistence. Existing accepted compositions retain their original selection;
receipts predating this feature continue to use the whole previous result. Changing
the sequence explicitly resolves and freezes a new selection. See [ADR-0260](../decisions/0260-acquisition-text-content-is-selected-at-admission.md).

Ordinary Flow `transform` steps select the same function and explicitly map its
`value` argument. Knowledge binding/before-publication transformations receive
Artifact manifests: use the existing `foreachArtifacts` content-processing Flow
region to read the authorized input, select its XML content and invoke the same
function, then return the converted JSON. A retained JSON input is an object-record
collection; select `/0/content` from a singleton acquisition envelope. This
explicit Flow can be selected at either Knowledge transformation placement;
`json.explode` can follow it directly on the resulting Artifact manifest.

Whole-feed publication produces one logical Artifact with one object record.
Explicit explosion preserves one logical Artifact and produces N document rows
or N physical local JSON files, with the exact selected item contract and member
lineage. Document queries can filter/page these rows. Local Storage supports
authorized member reads and gains no query language. Existing preparation,
sealing, replay and activation rules protect the active publication on failure.
An empty feed maps successfully to `items: []`; explicit explosion rejects that
empty selection under the existing non-empty-record policy.

Current dependency permissions/availability are rechecked at execution. A revoked,
missing or replaced dependency fails; no newer version is used as fallback.
Historic publications retain their original evidence and content.

The separate reusable UX scenario [#1215](https://github.com/gbaudrit/agentstration/issues/1215)
remains blocked until maintainer UX validation and implementation merge.

## RSS 2.0 structural validation profile

The catalog names include the feed format version: `rss-2-0-xml`,
`rss-2-0-json`, `rss-2-0-mapping` and `rss-2-0-xml-to-json`. Their Agentstration
resource versions are all `1.0.0`; that version is independent of RSS `2.0`.
This is an unreleased replacement of the former synthetic `rss-*` examples.
Reseed/rebootstrap the instance explicitly; existing immutable versions and
consumer snapshots are not silently replaced or rebound.

The shipped XSD is an Agentstration-authored, permissive structural contract
based on the [RSS 2.0 specification](https://www.rssboard.org/rss-specification)
and [RSS Best Practices Profile](https://www.rssboard.org/rss-profile). It is not
an official RSS XSD or a copy of a third-party validator. Passing this contract
does not certify full RSS 2.0 conformance.

The contract requires one `rss` root with `version="2.0"`, one `channel`, its
`title`, `link` and `description`, and at least a `title` or `description` per
item. Standard channel/item metadata may appear in different orders; scalar
duplicates are rejected. Required attributes on `source`, `enclosure` and
`cloud`, and required children of `image` and `textInput`, remain structural
requirements. Undeclared no-namespace elements are rejected. Namespace-qualified
extensions are accepted without resolving or validating their contents. There
is no schema ceiling of 100 items; operator array quotas remain independent.
The XSD does not enforce a single occurrence of empty `skipHours`/`skipDays`
containers. Their child counts remain bounded at 24 and 7 respectively.

Publisher values are text at this validation stage. Image size limits, numeric
ranges, date/email/URL syntax, GUID permalink semantics, protocol vocabularies
and duplicate hour/day values do not block structural validation. In particular,
an image width of `256`, as observed in Google News, is accepted even though the
RSS specification limits it to `144`. These deviations are not emitted as
warnings or silently corrected; this profile deliberately does not assess them.
Malformed XML, DTDs/external entities and operator quota violations still fail.

The generic mapping and output contract remain separate validation stages.
Opaque GUIDs stay strings; missing GUIDs/titles are permitted when an item has
a description. The JSON item projection uses the flat object contract supported
by the ASP document profile: its individual fields are optional, and the input
XSD enforces the title-or-description requirement. It does not use `anyOf`/`oneOf`
combinators that would prevent admitting the item contract as a document collection.
`guidIsPermaLink` is emitted only for an explicit attribute;
RSS defines the omitted attribute as true. Item links, source titles and
`dc:creator` are optional output fields. Explicit boolean/date conversions can
still fail on unconvertible values after structural validation, with no partial
output. The mapping supports the documented date formats above rather than
every possible RSS date spelling. The generic engine contains no RSS-specific
code and the deterministic tests use synthetic documents, not live news content.

## XML processing settings and limit diagnostics

The unreleased installation is reseeded for this change. Engine/provider/function
versions stay at `1.0.0`; no compatibility version or Bootstrap workflow is added.
The authority reads the following settings at startup:

```json
{
  "Agentstration": {
    "XmlContent": {
      "MaximumDocumentBytes": 10485760,
      "MaximumDepth": 32,
      "MaximumNodes": 8192,
      "MaximumAttributesPerElement": 64,
      "MaximumSelectedNodes": 8192,
      "MaximumArrayItems": 100,
      "MaximumMappingOperations": 32768
    },
    "FunctionContent": { "MaximumValueBytes": 10485760 }
  }
}
```

`MaximumDocumentBytes` measures XML instance UTF-8 bytes. The same parser settings
apply to XSD instance validation and transformation. The separate XSD definition
profile keeps its 64 KiB authoring ceiling. Mapping output and native invocation
JSON use `FunctionContent.MaximumValueBytes`; escaping and wrappers can increase
serialized size relative to XML bytes. There is no additional 65536-character
invocation restriction. Schema `maxLength`, XSD cardinalities, raw HTTP, Flow,
Storage and remote protocol limits remain independent and still apply.

All operator values must be positive. Restart the authority after changing them;
for example `Agentstration__XmlContent__MaximumDocumentBytes` overrides document
bytes through an environment variable. Raising an array-item quota does not widen
the input XSD or the output JSON Schema. The RSS 2.0 profile itself has no item
count ceiling; the default engine quota remains 100 array items.

A limit error shows the measured quantity, permitted bound, unit, causal function
step and exact server setting when applicable. XML UTF-8 bytes, serialized JSON
bytes, text characters, node counts and mapping operations are distinct. Values
and source content are omitted. Schema length errors refer to their declared
schema constraints rather than suggesting a server setting can override them.
Old recorded errors without measurements remain readable. See [ADR-0261](../decisions/0261-xml-processing-uses-operator-content-settings.md).

## AEP descriptor discovery

Finite REST requests and individual MCP calls share AEP descriptor discovery for
their operation. The same applies to a Tool invocation or a background recovery
pass. Repeated and concurrent requests for the same registration, generation,
configuration, expected identity, credentials source and caller context use one
pending discovery. A new operation discovers again; there is no cross-request TTL
or credential-value cache. Streaming HTTP sessions do not own a long-lived cache;
each MCP call owns its operation instead.

Only discovery descriptors are shared. Schema resolution, authorization,
definition/instance validation, provider selection and ambiguity rules remain
unchanged. An individual caller can cancel its wait without cancelling another
waiter; operation cancellation cancels the shared discovery. Each operation retains
at most 256 discovery identities, then performs additional discoveries normally.
This reduces repeated AEP HTTP requests while inspecting transformation contracts;
it does not make Data Source history a metadata-only read or remove content I/O.

## Diagnosing rejected XML

`transformation_input_invalid` is a contract failure, not a generic worker failure.
New executions retain a bounded structured cause through the function, Runtime
Worker and Flow Run error details. The localized panel distinguishes malformed XML
or prohibited markup, an XSD mismatch, and an invalid standalone XSD definition.
It shows XML node path and line/column when available, declared type/structure,
and the exact accepted XSD name/version and immutable evidence. Declared structure
is bounded context from the XSD; it is not a reproduction of an untrusted parser
message or a claim that all other fields have been validated successfully.

For a well-formed document, compare the indicated position with that exact XSD:
check qualified names, element order, required attributes, types and cardinalities.
Correct the producer or explicitly adopt a matching input profile. Mapping runs
only after XML validation succeeds; changing a byte quota cannot fix an XSD mismatch.
For malformed XML, fix syntax/escaping at the reported position; DTDs and external
resolution remain prohibited. No rejected scalar values, attribute values, XML
snippets or raw framework messages are retained in the structured diagnostic.
Historical failures whose details were discarded cannot be reconstructed; rerun
with the updated implementation to capture the cause. No Schema/Function version
or Bootstrap procedure change is required for this diagnostic improvement.
