# Native transformation functions

Transformation functions are reusable scoped resources. The initial execution kind is native .NET code. Scripts, remote engines and custom DLL loading are not implemented.

The platform automatically provisions the standard native FunctionKind and five TransformationFunctions at Instance scope during startup, including when file Bootstrap is disabled. They are marked `builtIn`, managed by Core (`functions.native`), and read-only in the Console. No file profile or manual Bootstrap action is required. Tenants and their descendant Workspaces inherit Discover/Use grants; the catalogue is not copied into each Workspace. Startup reconciliation preserves resource identities and immutable versions and refuses to adopt a user resource at a reserved builtin identity.

Inspect the catalogue under `/function-kinds` and `/transformation-functions`. Standard function resource names use the established `-builtin` suffix, for example `json-select-builtin` and `artifact-explode-builtin`; implementation identifiers remain `json.select` and `artifact.explode`. Additional user-defined functions can select a registered implementation/version through the resource API, Console or generic Bootstrap handlers. Published versions cannot be rewritten; add a version and explicitly select it in consumers. Disabling a user-managed function or kind blocks further invocations under current authorization.

| Implementation | Business input | Output |
| --- | --- | --- |
| `json.select` | `{value, path}` | Value selected by RFC 6901 JSON Pointer |
| `json.project` | `{value, properties:[{name,path}]}` | Object with the selected/renamed properties |
| `collection.filter` | `{value:[...], path, equals}` | Elements whose selected value equals the given JSON value |
| `collection.distinct` | `{value:[...], path}` | First element for each semantically equal selected JSON value |
| `artifact.explode` | `{value:{artifacts:[...]}, path, mode?, itemSchema?, maximumOutputs?}` | Manifest of independently publishable JSON items |

The empty Pointer selects the root. Escape `/` as `~1` and `~` as `~0`. Missing properties, invalid array indices and malformed escapes fail; there is no original-value fallback. Filter and distinct preserve order. Functions have a 1 MiB JSON invocation/result limit; individual functions and consumers may impose smaller bounds.

Each function has an optional resource `description`, editable for user declarations. The standard descriptions are reconciled automatically at Instance scope and shown in English or French after selection in source sequences and the ordinary Transform inspector. Descriptions are presentation metadata: editing one does not change an accepted function version or implementation contract.

## Calling a function

The ordinary **Transform** step offers a function selector and an input mapping. The mapping supplies its one business argument, for example:

```json
{"value":"${transition.output}","path":"/results"}
```

In source sequences, use **Add a function** alongside the existing Flow action. Select the function/version and fill its parameter form. The preceding value supplies `value`; literal parameters such as `path` cannot replace it. Function and Flow entries can be mixed, reordered and saved. Knowledge's **Transformations before publication** (`postAggregate`) and individual Data Source bindings use this same editor. No wrapper Flow needs to be created.

## Exploding acquired content

For acquired JSON `{ "results": [{"id":"a"},{"id":"b"}] }`, select `artifact.explode` and set `path` to `/results`. `arrayItems` is the default mode. `objectValues` instead expands an object's values in ordinal key order and retains each key in provenance. Selection with `json.select` keeps a selected array as one value; explosion explicitly produces multiple Artifact entries.

Declare `itemSchema` to validate each item's content. The acquisition/envelope schema is not copied onto items. Outputs retain parent identity/digest, path, key and transformation evidence. Empty collections, duplicate object keys, invalid paths, mismatching schemas and output limits fail explicitly. At most 100 output Artifacts are publishable to Knowledge, each at most 64 KiB; a function invocation also bounds aggregate output content to 512 KiB. A larger source still has to satisfy its acquisition and read limits.

Inside **For each Artifact**, a content-processing branch can pass `{"value":{"content":"${input.content}"},"path":"/results"}` to the same function. Return its manifest to the bottom result connector. The owner validates and prepares all children before registering them; it does not first capture another durable Artifact containing the complete batch. An explicit output contract applies to each expanded child. Retained input Artifacts remain unchanged.

Knowledge binding transformations run after a retained acquisition is stored, and before Snapshot output publication. Direct input and expanded inline outputs remain transient until final Snapshot publication. Expiry, cancellation or restart never silently reacquires direct input or falls back to storage. Failed publication preserves the active Snapshot and its historical references.

## Explicit iteration

**For each** receives a mapped JSON array and invokes the connected process branch for each element. Each child receives the element directly; results preserve input order, including with bounded concurrency. An empty collection returns `[]`. Item/result schemas and maximum count/concurrency can be configured. Explicit API-authored text/batch partitioning is owned by this step rather than Transform.

**For each Artifact** accepts a reference manifest and supports bounded content mode or scoped progressive access. Both steps expose bottom **Process** and **Processing result** connectors plus ordinary completed/error continuations. Their branches may contain Agents, Tools, functions and child Flows. Execution uses the existing causal implicit child runs.

See [ADR-0226](../decisions/0226-native-function-catalogue-and-explicit-iteration.md) and [Artifact processing](artifact-transformations.md).
