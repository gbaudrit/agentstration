# ToolDefinitions and parameter relays

A `ToolDefinition` publishes a product-owned Tool. `definition.toolKind` selects `flow`, `relay` or `acquisition`. A relay fixes selected inputs of an existing governed Tool and exposes the remaining contract. It is independent of ASP and storage providers.

## Independent publications and consumers

Create/update drafts through `/api/tooldefinitions`. Select an explicit `major.minor.patch` version, then publish with `POST /api/tooldefinitions/{name}/versions` and `{ "version": "1.0.0" }`. Service/declarative callers may set `publish: true` to save and publish. Saving alone does not publish.

List immutable publications at `GET /api/tooldefinitions/{name}/versions` and consumers at `GET /api/tooldefinitions/{name}/usages`, using the definition's `namespace` query parameter and existing authenticated scope/resource permissions.

The Usages view lists published Agent revisions, ToolSet versions and Flow versions in the definition's owner scope and authorized descendant scopes. Results show each consumer's actual owner scope and exact relay version. Existing scope-inventory permissions bound discovery; unauthorized tenants and workspaces are never included. Adoption remains explicit in each consumer's own scope.

Each publication produces an ordinary Tool named `agentstration.<definition-name>.v<version>`. Its MCP name is `<definition-name>.v<version>` (prefixed by the namespace when needed). Select that Tool in Agent, ToolSet and Flow authoring.

Relay and source versions are independent: relays `1.1.0` and `1.2.0` may both target source `2.4.0` with different fixed inputs; relay `1.3.0` may target source `3.0.0`. Changed configuration cannot reuse a published number. A cached schema does not preserve an old external implementation: when a provider cannot invoke historical versions, changed source/provider revisions make the pin explicitly stale.

Publishing leaves consumers unchanged, including after a literal-only change. A direct Agent dependency needs a new Agent revision; a ToolSet needs a new ToolSet version followed by its consumers' explicit adoption; a direct Flow dependency needs a new Flow version. Only changed dependency chains need publication. Accepted executions retain references, while live disablement, revocation or unavailability can still stop calls.

## Relay YAML

The identifiers below are synthetic. Use the exact scope, UID and generation returned for the selected Tool; do not guess them.

```yaml
apiVersion: agentstration.io/v1
kind: ToolDefinition
metadata:
  name: hr-documents
  namespace: default
definition:
  toolKind: relay
  displayName: Read HR documents
  version: 1.0.0
  publish: false
  invocationTimeoutSeconds: 30
  target:
    name: documents.read
    namespace: default
    scopeRef: /workspaces/11111111-1111-1111-1111-111111111111
    uid: 22222222-2222-2222-2222-222222222222
    generation: 7
  bindings:
    - kind: literal
      path: /store
      value:
        id: hr-documents
    - kind: parameter
      path: /arguments/prefix
      parameter:
        address:
          namespace: default
          kind: Parameter
          name: hr-prefix
        scopeRef: /workspaces/11111111-1111-1111-1111-111111111111
```

For a Tool declaring a sensitive string input, the binding may instead be:

```yaml
bindings:
  - kind: secret
    path: /accessCode
    secret:
      address:
        namespace: default
        kind: Secret
        name: document-access-code
      scopeRef: /workspaces/11111111-1111-1111-1111-111111111111
```

This does not introduce credentials into ASP schemas or move Connected Service/Storage connection configuration into a relay. Bind only inputs actually declared by the Tool.

Flow definitions use `toolKind: flow` and their `flow` reference. Acquisition definitions use `toolKind: acquisition` and an acquisition replay binding, without a required executable Flow field. The existing `/from-acquisition/{id}` application/API path creates and publishes the first acquisition Tool version.

A Flow-backed source retains its declared Flow version strategy. An exact Flow reference fixes that dependency; an active-version reference deliberately follows the Flow's active version under the existing Flow rules. Choose an exact Flow reference when the relay must reproduce a specific Flow implementation.

## Connected Services, Storage and acquisition composition

The integration baseline includes [#1060](https://github.com/gbaudrit/agentstration/pull/1060), [#1061](https://github.com/gbaudrit/agentstration/pull/1061), [#1062](https://github.com/gbaudrit/agentstration/pull/1062), [#1063](https://github.com/gbaudrit/agentstration/pull/1063), [#1080](https://github.com/gbaudrit/agentstration/pull/1080) and the subsequent [#1079](https://github.com/gbaudrit/agentstration/pull/1079) Storage type/grant administration. Relays reuse their shared value editor and existing resource authorization, including UID-bound generic Discover/Use overlays and conditional access-policy writes. They add no Connected Service or Storage connection-binding resolver.

Connected Service operation discovery/activation does not invoke an operation or publish a Tool. [#1041](https://github.com/gbaudrit/agentstration/issues/1041) owns the explicit Operation → Acquisition → Data Source → Storage composition and any operation-to-Tool exposure. Once that path supplies an ordinary governed Tool, a relay can target it through the same exact Tool reference as any other source. No `operation` ToolDefinition variant or second acquisition mechanism is introduced here.

An existing acquisition Tool remains executable through the current acquisition replay service. A relay around it delegates to that Tool, preserving its acquisition provenance and admission checks, with independent relay/source publication versions. Storage connection bindings, protected Secret grants, native export mappings and Data Source envelopes remain outside model-controlled relay arguments. Storage authorization follows its existing resource Use policy; Connected Service, Parameter and Secret checks remain independently enforced.

This boundary is covered against the merged acquisition implementation. The parallel #1041 implementation has no published integration branch at this reconciliation baseline, so combined runtime validation remains pending its availability.

## Values and validation

- **Literal:** structured JSON stored in the publication, including whole objects/arrays. Missing, empty string and explicit null differ; null must satisfy the source contract.
- **Parameter:** an exact authorized reference to an existing scalar Parameter; resolve and validate its current value at each call. Parameters do not gain object/array storage here.
- **Secret:** an exact authorized configured reference; resolve as a UTF-8 string only at execution, without arbitrary JSON parsing. Editors show reference/availability, not value.

Changing the value of the same Parameter or rotating the same Secret affects subsequent calls after revalidation without relay/consumer republication. Changing reference identity, binding kind, literal or source requires a new publication. Deleted/recreated resources do not satisfy old UID pins. Missing, unauthorized or invalid values fail without fallback. Drafts and immutable publications participate in Parameter/Secret usage reporting.

Durable Flow inputs retain symbolic Secret references, never resolved values. Trusted references carry the exact Secret UID, scope and original consumer context; public input JSON cannot grant resolution authority. Mappings and child Flows preserve the references. Each server-side Tool attempt resolves and validates the current value again, including after recovery or rotation. Completed steps are not repeated. Missing, revoked, replaced or invalid references fail explicitly without an earlier-value fallback. The Worker, Work/Flow/step state and execution material receive symbolic inputs. Exact Secret echoes become references and embedded echoes are redacted before outputs cross the durable boundary. This applies to nested relays and Knowledge dispatch as well as ordinary Flow-backed Tools. See [ADR-0203](../decisions/0203-durable-flow-inputs-retain-live-secret-references.md).

Paths are non-root JSON Pointers over declared object properties: `/a~1b~0c` addresses `a/b~c`. Reject duplicates, parent/child overlaps and duplicate JSON keys. Traversal through arrays or nullable/non-object ancestors is unsupported; fix the whole value instead. No text interpolation is performed.

The derived schema removes fixed fields and updates required ancestors. If reconstruction creates an originally optional object, its remaining required siblings are still required from the caller. Ancestors containing fixed inputs reject undeclared aliases. Callers cannot supply a fixed field, even with the same value, or replace its parent with null/scalars.

The profile reuses the existing Tool validator: declared object properties/required inputs, primitive type unions, arrays/items, enum, string lengths/pattern/URI and numeric bounds. References, conditional/composition schemas, pattern/schema-valued additional properties and unsupported constraints fail explicitly rather than being silently advertised as validated.

Limits: 64 bindings, twelve object levels and 1,024 characters per path, eight relay levels, 65,536 JSON characters for source schema/fixed configuration/reconstructed request, and the configured 1–900 second timeout. Scope, Principal and correlation are trusted context outside model-controlled arguments. Relay simulation validates the exact source, current binding values and source governance without invoking the source implementation. Source approvals/hooks remain enforced. Secret values are absent from exports/previews/receipts; inner argument capture is suppressed and sensitive failure diagnostics are redacted.

## Console workflow

### Hide an optional input

The relay editor offers **Hide** (French: **Masquer**) for optional source
properties. Hiding an object removes its entire subtree from the caller schema
and hides its child controls. The source receives no value for that property
and retains its normal default behavior. This differs from a literal `null` or
an empty object, which are actual values and must satisfy the source contract.

The persisted binding is explicit:

```yaml
bindings:
  - kind: omitted
    path: /acquisition
```

A required input needs a caller value or a configured binding; it cannot be
hidden on its own. An optional parent may be hidden even when its children are
required whenever that parent is present. Hidden paths reject caller overrides.
Omitting a nested field does not create its parent or change the parent's
requiredness. Old publications retain their old schema: publish a new relay
version and explicitly adopt it in Agents, ToolSets or Flows.

### Edit and publish

Choose **New definition** from **Tools > Definitions** or a Tool detail page. The **Define** step collects the identity, namespace and independent initial version, then offers **From a Flow** or **Tool relay**. Its **Scope** picker reuses the Flow creation control and the current context's authorized ownership targets. Definitions may belong to Instance, Tenant or Workspace. Source catalogs, bound values and draft creation use that exact owner scope; creating the draft opens its editor in the corresponding context. Changing the owner clears source configuration while retaining identity fields. The **Configure** step adapts to the chosen kind. Opening from a Tool detail preselects Relay and its source without skipping the identity step.

Administrative execution reuses [ADR-0185](../decisions/0185-administrative-execution-uses-governed-operational-workspaces.md): definitions and publications retain their Instance/Tenant owner, while Work and Flow Runs are stored in the corresponding protected operational Workspace. `LogicalScopeRef` preserves the administrative authorization scope independently of physical placement. A relay forwards the trusted operational context through the existing pipeline; a Flow-backed Tool uses the shared execution-scope accessor and root Flow submission path. Selecting Instance never rebinds a definition or its execution to a business Workspace.

For a relay, select the source Tool and an exact version/revision, configure caller-provided, omitted or literal/Parameter/Secret inputs, and inspect derived input/source output contracts. Multiple source publications require an explicit version selection. These choices use the shared `ValueBindingRequirementEditor`, including contextual creation and exact scoped references. Each input's schema is supplied as `LiteralSchema`; literals are actual JSON values without an envelope. Missing configuration, an empty string and explicit null remain distinct. A fixed literal must be configured even for an optional source property; invalid or malformed raw JSON blocks draft creation, saving and publication rather than saving the last parsed value. For a Flow Tool, select a published Flow and its exact version and inspect its read-only contracts. Returning to Define retains the configuration. **Create draft** saves the definition and opens its editor; publication remains a separate explicit action. Acquisition Tools are still created from successful acquisitions.

The definition exposes **Configuration**, **Versions** and **Usages**. **New version** retains the source. **Update source** prepares a new draft and deliberate source selection; the latest source is never adopted automatically. Missing/renamed paths remain visible and block publication until explicitly discarded/rebound. Review the version pair and changes before publication, then use Usages to plan consumer adoption.

Namespace, owner scope, unavailable selections, reference status and effective approval remain visible. The editor is localized in en-US/fr-FR. The new reusable scenario [#1050](https://github.com/gbaudrit/agentstration/issues/1050) starts only after implemented-UX validation and merge; approving the mockup does not satisfy that gate.

## Development state

The old ToolDefinition shape was unreleased. Update local manifests/fixtures to `toolKind`; no legacy reader or automatic history rewrite is provided. Deliberately update old documents or reseed a disposable development instance. Retained execution/acquisition history must not be silently deleted.

See [ADR-0202](../decisions/0202-tool-definitions-publish-independent-governed-relay-versions.md) and [Tool providers](../concepts/tool-providers.md).
