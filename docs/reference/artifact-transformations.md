# Transform Artifact content in a Flow

The `artifactTransform` step processes an ordered list of Artifacts. The existing
`transform` step continues to map JSON values without loading stored files.

In the Designer, add **Transform artifact(s)**. Connect the ordinary input to the
step, connect its **process** output to the processing steps, and return their
successful result to the bottom **Processing result** port. Connect **completed**
to the next outer step and **error** to an error output. Processing may include
Agents, Tools, child Flows, conditions and mappings. It does not require another
published Flow or transformation step.

A transformation draft created from a source forwards its incoming successful
result at the final output, so inserting this step preserves its new Artifact
manifest. In an existing authored Flow, check the final Output mapping: use
`"${transition.output}"` to forward the connected step rather than a fixed earlier
mapping result. Existing authored drafts and publications are not rewritten.

## Selecting the input

The Artifact-list mapping defaults to `"${transition.output}"`. It accepts an
Artifact array, a manifest containing `artifacts`, or the exact source-composition
envelope `{ "value": ..., "parameters": {} }`. That envelope is unwrapped once.
Use `"${input.value}"` to explicitly select a source transformation's manifest.
An invalid explicit mapping fails; the step never substitutes a different input.

The separate **Processing input mapping** is optional. Empty or `null` passes the
complete per-Artifact processing envelope described below; it is not a missing
Artifact-list mapping.

## Content mode

Use **Content (text or JSON)** for a processor that accepts ordinary values.
`artifactsMapping` selects an Artifact array or an object with an `artifacts`
array. Each item identifies a sealed staged or immutable durable Artifact. Bounded
inline manifest content is supported as an exact staged input copy.

The processing input envelope contains `content`, `artifact`, `parameters`,
`inputAccess` and `outputAccess`. `content` is a string for UTF-8 text and a JSON
value for JSON media. Saved outer transformation parameters are available through
`input.parameters`. Configure the step's processing input mapping to adapt this
envelope to an existing Tool, without changing that Tool definition:

```json
{
  "text": "${input.content}",
  "language": "fr"
}
```

A processing Tool then receives `{ "text": "...", "language": "fr" }`. If it returns
`{ "redacted": "..." }`, set the owning step's result mapping to
`"${input.redacted}"`. Empty result mapping captures the entire successful result.
An Agent can return text directly. JSON output must be valid JSON; Markdown code
fences are not parsed. Empty output name/media type preserves the input values.

Content mode defaults to 65,536 input bytes; its configurable maximum is 1 MiB.
Oversized content fails explicitly. PDF, images and other binary formats require
an explicit extraction processor; reading Storage bytes is not text extraction.

## Access mode and large content

Use **Scoped input/output access** when processing chooses what to read. Its input
contains metadata and logical access descriptors, with no whole-document content.
The descriptors identify this execution context; they are not bearer credentials,
public download URLs or arbitrary Storage access.

Agents in that context automatically receive these invocation-only Tools:

| Tool | Behavior |
| --- | --- |
| `input_info` | Input name, media type, size and range unit |
| `input_read` | Bounded read using an offset or continuation cursor |
| `input_search_regex` | Regex matches with readable ranges and continuation |
| `output_info` | Current output revision, size and completion state |
| `output_write` | Append text or replace a selected range |
| `output_read` | Read back the current output revision |
| `output_search_regex` | Search the output for verification and corrections |
| `output_complete` | Declare a selected revision ready for finalization |

Ordinary Tools named `artifact.processing.<operation>` expose the same operations
to authored Tool steps. They use the trusted processing Flow context. Calls from
an unrelated or terminated execution fail; a Tool argument cannot select another
Artifact. Existing generic Tools remain usable in content mode, and a processor
that understands scoped access can use these operations in its processing context.
A Flow called inside processing inherits that context without another transform.

`input_read` and `output_read` accept `offset`, `length` or `cursor`. Positions and
regex match ranges use **UTF-16 character offsets**, not byte offsets. Reads default
to 4,096 characters and are bounded to 16,384; surrogate pairs remain intact.
Cursors stay attached to the exact input, or to one output revision. An output
cursor becomes invalid after an edit. A new acquisition cannot redirect input.

`output_write` requires `content`, `expectedRevision` and a unique `requestId`.
Omit `offset` to append. Supply `offset` and `deleteLength` to replace a range.
Retry a write with the same identifier and arguments; a changed replay fails.
The initial revision is zero. Reads and searches do not modify it. Complete with
`output_complete({ "expectedRevision": 3 })` after checking revision 3. A subsequent
correction creates a new revision and requires completion again.

The Agent decides whether to read first or search first and how to cover the
complete document. Example instructions for anonymization:

> Inspect the input, process its complete content using bounded reads, and write
> the anonymized output progressively. Preserve structure. Read/search the output
> to check for remaining personal data, correct it if necessary, then complete the
> latest output revision. Do not return the whole document in your final message.

These Tools restrict the access they provide. Other Tools already assigned to an
Agent continue to use their own grants; choosing an Agent with broader access
does not revoke those grants.

## Instance and Tenant execution

Instance- and Tenant-owned processing keeps its logical resource scope. Artifact
operations run inside the execution's trusted operational Workspace, using the
same Principal, Tenant, Workspace and authorization restrictions. That temporary
context is restored on success or refusal; it does not grant access to a caller-
selected Workspace or change the published Flow. Scoped content Tools support
these trusted administrative contexts as well as ordinary Workspace execution.

## Direct acquisitions without intermediate storage

A source using direct acquisition can use the same content/access processing
connections. Its input and editable output stay in the existing bounded,
process-local direct handoff cache. No staged or durable intermediate Artifact,
document-bearing runtime state, persisted Tool arguments or execution payload is
created. Reads, regex searches, revision-checked writes, correction and completion
keep the same behavior as retained processing.

The step returns an inline manifest for the next transformation or projection.
Only final Snapshot publication materializes that content in the selected output
Storage. The direct input and final inline output are bounded to 64 KiB each by
the current source publication contract; larger drafts cannot be finalized.
Drafts share the direct handoff admission limits (64 MiB per Workspace, 256 MiB
per process, 4,096 entries) and expire no later than 15 minutes after the owning
Flow was created. Capacity failure preserves accepted content. Aborted drafts
cannot be reopened. Cold or expired executions fail explicitly; they do not
reacquire or fall back to disk. This does not enable ordinary Artifact capture or
Flow-backed Tool Definitions for direct execution.

## Output, failures and limits

The output media type field offers common text and JSON MIME types and accepts a
custom value. Leave it empty to inherit the input type. The selected type must
match the produced content: an Agent returning plain text needs `text/plain`;
`application/json` and `+json` types require valid JSON without Markdown fences or
surrounding explanation. Invalid output reports `artifact_processing_json_invalid`
with that remedy; malformed input reports `artifact_processing_input_json_invalid`
and asks you to correct the source content or media type. Neither error includes
document content.

The Flow Run **Input / Output** tab shows the returned value and retained Artifacts
associated with that execution, including outputs linked by processing provenance
and existing Artifacts referenced by its output manifest.
Artifact links remain available after direct execution payloads expire. An empty
returned value and an expired temporary payload have distinct explanations; the
catalog does not reconstruct or persist expired direct content. Refresh the panel
if final Snapshot publication has completed after the Flow Run.

For retained execution, each successful input produces one new durable Artifact.
Originals remain intact. Direct execution returns inline content until final publication.
All processing regions must succeed and all outputs must be prepared before final
registration begins. The owning step persists the outputs and returns an ordinary
publishable Artifact manifest. Receipts link the producing Flow/step, processing
child execution, exact input digest and actual Storage materialization. The existing
Artifact durable backend uses the selected local default where available.

The step defaults to 32 input Artifacts and permits at most 128. Access input is
bounded to 16 MiB; output defaults to 8 MiB and permits at most 16 MiB, with at most
128 writes. Staging and Storage policies can impose smaller limits. Regex patterns
are limited to 1,024 characters, searches return at most 50 matches and have a
100 ms regex timeout. Contexts expire after one hour. Superseded or abandoned
staged revisions follow existing staging retention and cleanup.

An error Output without an explicit code/message preserves the originating step's
trusted error. An explicit code or message still overrides it.

A failed processor, incomplete output, invalid JSON, stale revision, foreign cursor,
unsupported media or oversized content prevents a successful step result. No
original-content fallback is implicit. Source publication still owns activation of
the resulting Snapshot. Storage/catalog failure during finalization can leave
orphan outputs; the operation does not promise a distributed transaction.

See [ADR-0213](../decisions/0213-artifact-transformations-own-scoped-content-processing.md)
and [source composition](../architecture/implementation/data-source-composition.md).


## Effective data contracts

`outputContract` describes the final Artifact **content**, separately from the
manifest/envelope schema. It is optional. Use a fixed declaration to constrain
selected content, or explicitly request dynamic resolution:

```yaml
outputContract:
  mode: Fixed
  mediaType: application/json
  schema:
    type: object
    required: [masked]
    additionalProperties: false
    properties:
      masked:
        type: boolean
```

```yaml
outputContract:
  mode: Dynamic
  source: input # or parent
```

An absent declaration remains absent. A dynamic declaration with no concrete
source fails with `data_contract_unresolved`. Unsupported schemas are rejected
instead of being treated as arbitrary JSON. Broad `{}` is an explicit fixed JSON
contract. A final processor's explicit output is authoritative; a contradictory
fixed transformation expectation fails with `data_contract_conflict`.

Content mode validates **after** `resultMapping`. With a mapping, the expectation
is an instruction about the final selected value; it does not constrain an
intermediate Agent's raw response. Access mode validates the completed draft,
including after corrections; the final chat message is not the Artifact body.

Agents can declare an existing fixed `outputSchema` or an explicit dynamic
`outputContract`. Fixed schemas take precedence. The invocation adds schema/MIME
instructions without editing the Agent resource, then validates and parses the
result. JSON must be complete JSON without prose or Markdown fences. For a string
result, choose a text MIME type. Dynamic mode is selected independently from
having no schema in the Agent contract editor.

A direct Tool call must declare fixed input and output schemas. The result is
validated by the authority before a successful step is reported. An undeclared
Flow/sub-Flow can forward trusted invocation context to its Agent; explicit Flow
input schemas stop this implicit inheritance. Where a Tool or sub-Flow input
schema declares an `outputContract` property, the platform can provide the concrete
`{ schema, mediaType }` parameter. It never adds this property to unrelated inputs.
An existing conflicting parameter fails rather than silently changing the contract.

Artifact receipts, inline handoffs and Snapshots can retain `dataContract` with
`schema`, `mediaType`, `origin`, `declaredMode`, `digest` and `validated` evidence.
Publication verifies actual stored content instead of trusting a manifest's flag.
Older Artifacts without metadata remain readable and are not guessed to have a
contract. Dynamic input inheritance requires concrete available metadata.

Hover a Designer port for a summary; its adjacent information control opens the
schema, mappings, MIME and bounds. This also works on a published definition in
read-only mode. Flow Run step diagnostics expose the effective execution contracts,
separately from business input/output. Artifact details expose retained data
contracts and provenance.

Contract errors identify the boundary, schema path, expected/received type or MIME,
and originating run/step, with a link to the source execution. They survive nested
Flows and the main Knowledge orchestration. No document value is included in these
diagnostics. `data_contract_json_invalid` explicitly explains JSON syntax versus
text output; `data_contract_violation` reports schema mismatch. Start a new run
after correcting the declaration, mapping or output format.

See [ADR-0214](../decisions/0214-effective-data-contracts-follow-flow-invocations.md).

An absent schema does not remove the output format constraint: the effective MIME
type is supplied as invocation context and JSON syntax is checked before capture.
This is a concrete format expectation, not an implicit dynamic declaration. Runtime
content-contract inspection deduplicates schemas and is bounded to 256 KiB; other
contracts remain available on the linked retained Artifacts.

External MCP/AEP Tool output schemas remain provider-owned and describe
`structuredContent`. A Flow keeps the full MCP result envelope; its port contract
wraps the business schema under `structuredContent` and validation checks that
nested content. Internal Tools keep their ordinary result shape. The platform
does not rewrite external Tool declarations or unwrap their execution results.

The `artifactTransform` input port describes the reference envelope: an Artifact
list, an `{ artifacts: [...] }` manifest, or the composition `{ value, parameters }`
wrapper. Its completed port returns a manifest. These structural contracts are
separate from each Artifact's content schema and MIME type; heterogeneous input
Artifacts keep their individual metadata, resolved at execution time. The output
MIME setting does not describe the incoming Artifacts.

Agent step settings show the same effective input and output schemas as their
ports. In a published Flow these are the pinned schemas, displayed read-only.
The Designer checks supported structural schemas after statically resolvable
mappings and flags connections whose compatibility is not guaranteed. Optional
fields and additional properties retain JSON Schema semantics. Dynamic contracts,
unsupported schemas and runtime-only expressions still require execution-time
validation; an absent warning is not proof that arbitrary output will validate.

Source transformation selection separates the Flow from its published version.
Versions appear by newest publication first, and reopening or refreshing the
catalog keeps the saved pin. Opening a selected version starts a read-only
Designer in another tab without replacing the source form or creating a
draft. The standard `postAggregate` point is presented as **Transformations
before publication**: it processes the gathered input results before Snapshot
creation. Its technical point name stays stable for existing references.

The shared execution-progress view shows completed root steps, elapsed time and
the active child Flow/step, with a link to that execution. Descendant inspection
is bounded and uses the root execution scope; unavailable child details leave the
parent visible. It does not start or retry work.

Contract failure panels distinguish the producing Flow/step from the Flow/step
that supplied the expected contract. The summary explains the mismatch once;
expected/received formats and correction guidance remain readable, while raw
codes, origins, rules, contracts and step payloads can be expanded separately.
Knowledge projection errors use the same localized explanation and retain links
to the causal execution when its details are accessible.

The Designer separates **data sent to the Agent** from the read-only input
contract. Its editable combo offers the incoming value, the current Flow input,
preceding results and, inside a valid Artifact processing region, content or
scoped input/output access. Unexecuted branches may not have a result at runtime.
Free expressions and literal text are supported; **Custom JSON** builds a
structured input without changing the Agent contract.

New Agent steps default to **Call an Agent**. Their title describes the step's
role independently of the selected resource, whose name appears on a separate
line. Changing the called Agent preserves an authored step title.

Port information controls appear on hover or keyboard focus, adjacent to the
actual anchors. A known contract incompatibility colors the connection and
receiving anchor red while dragging and after reopening the Flow. The same
mapping-aware check powers the diagnostic message. The connection remains
editable so its mapping can be corrected; unresolved compatibility remains
neutral and runtime validation remains authoritative. Published definitions are
inspected without rewriting their graph.

Execution progress uses a compact header, elapsed time, root-step counts and
separate current Flow/step labels. Full and sidebar error cards share a responsive
expected/received comparison, producer and contract-source locations, correction
guidance and collapsed technical details. Compact projection feedback retains the
explanation and source links.


### Repairing an output mapping

New transformation templates and Output steps forward `${transition.output}`.
Existing authored mappings remain intact. A mapping such as
`${steps.transform.output}` still reads the step named `transform`, even after
its incoming connection changes. If that step has been replaced by
`artifactTransform`, open the draft's Output settings and select **Use the
incoming transition**, or edit the explicit mapping. Published versions stay
immutable; **Open draft** preserves the namespace, owner scope and navigation
context while leaving the published view.

The Output inspector labels explicit mappings separately from incoming-value
forwarding and exposes an editable expression/JSON control in drafts. Output
validation rejects references to missing step identifiers. At runtime, a direct
value reference to an unavailable step produces `data_contract_mapping_unavailable`
with the producing run, consuming step and reference path. A declared but unselected branch retains its nullable result; an actual JSON `null`
also remains distinct from a deleted reference and follows the declared schema.
An invalid named success Output does not emit that success name to its parent;
the original child contract failure propagates instead of a later null-manifest
failure in the orchestration.

The full incompatibility panel appears only in **Summary**. Its short explanation
remains visible, with structured details collapsed initially. Step-local errors
remain available in their inspector. **Causality** links execution identifiers,
Flow headings and calling steps to their exact scoped runs. Output disclosures
load an authorized run only when expanded, show returned values and produced
Artifact links through the existing output view, and distinguish empty text,
returned null, absent output and expired/unavailable payloads. Causal value
previews are limited to 16,384 characters; the run's Input/output tab remains
available for complete retained values.


Existing instances reconcile internal Tool schemas during initialization version
5, including the fixed output schemas required by the built-in Artifact storage
read/write Flows. This updates the managed Tool resources without rewriting
published Flow versions. After upgrading, restart the authoritative host before
starting a new local-copy operation; historical failed runs remain unchanged.
Storage failures and other execution failures use the same Summary-only error
card as contract failures, with a localized heading, a short explanation and
collapsed diagnostic details. They remain identifiable by their original code.


Initialization version 5 publishes the current built-in staging and broker
ToolSets as `1.0.1`, pinning the reconciled Tool/provider generations, and selects
that staging version in the managed default binding. Storage Flow versions remain
`1.0.0`. Existing published ToolSet/Flow versions and execution history are
preserved; explicit callers of an older, stale broker version must deliberately
publish their callers against the new version. Fresh instances retain the
original broker publication for the built-in bundles that pin it.

Historical staged objects keep their recorded backend version. A changed binding
or stale historical Tool pin is reported explicitly; obtain a new staged copy
from the durable Artifact when needed. During reconciliation, drift in the current built-in publication fails
initialization with an explicit diagnostic. A release that changes these
contracts must advance both the initialization target and the current ToolSet
publication.

Staging route failures retain their original code, binding, capability, ToolSet
version and Tool identity through the governed Worker HTTP boundary and the Flow
Run error details, rather than becoming a generic server-error message.

## Parallel processing and process identity

`maximumConcurrency` optionally requests 1–16 concurrent units; omission preserves
sequential execution and historical definition serialization. A root assignment also
has a shared Worker process-lane budget (`Agentstration:RuntimeWorker:MaximumConcurrentProcessRuns`,
default 4, range 1–16). The effective concurrency is the lower limit. Nested process
regions execute sequentially on their enclosing lane to avoid waiting-parent starvation.
Each Artifact retains isolated owner/attempt/index access and drafts. Preparation of
all units precedes publication; completion order never changes manifest order. A failed
unit fails the owning step without publishing successful siblings as a partial manifest.

Each private child carries `processOrigin`: the source immutable snapshot, owner step,
region and derived graph hash, plus owner run/attempt/StepExecution, stable unit key and
ordinal. A named Flow invoked inside the process remains a named invocation even when
it inherits scoped Artifact capabilities. These fields supplement the existing source
Flow reference; they do not publish a new resource or rewrite older runs. See ADR-0215.

A terminal process unit is reused on admission replay. Changed input, key, ordinal or
definition is rejected. Interrupted nonterminal units follow existing assignment recovery;
external non-idempotent side effects do not acquire an exactly-once guarantee. Cancellation
propagates through the owning assignment and terminalizes unfinished units.
