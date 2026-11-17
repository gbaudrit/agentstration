# ADR-0243: Tool access summaries preserve authorized provenance

## Status

Proposed

## Context

Separate boxes for current resources, recorded generations, ToolSets and
ToolDefinition publications obscure an Agent's capabilities. ToolSet membership
alone cannot establish which Tools the Agent received. Neither a ToolSet nor a
ToolDefinition establishes a C4 container boundary.

## Decision

Keep the exact authorized graph, list and JSON export. The Console's default
summary groups generations by resource UID, moves ToolSets to access links and
keeps ToolDefinition provenance in link details. Distinct publications, native
Agent revisions, deployment configurations and homonymous UIDs remain distinct.
The technical-details control restores the exact window. Native C4 deployment
views retain their own execution boundaries.

The Agents contributor emits `toolset-access` only for selected members of the
exact retained ToolSet publication. Draft empty selection means all members;
recorded revisions use their explicit stored selection and matching Tool pins.
Lookup checks publication UID or canonical ToolSet UID, owner, namespace, name,
version, hash and generation. Native publication UIDs are translated to canonical
ToolSet publication graph IDs after validation. No stored revision is rewritten.

`ViaNodeId` is an additional neutral relation endpoint. Strict and progressive
reads require its visibility and binding-inspection permission together with both
ordinary endpoints. Streams emit and recheck it before the relation and verify it
at completion; the client rejects dangling references. Hidden or revoked ToolSets
cannot leak through labels. These provenance relations do not expand executable
impact/reconciliation closure.

The summary uses only received metadata. It never derives grants by joining all
ToolSet members to an Agent. Direct draft declarations can be labelled direct;
unspecified legacy origins remain unspecified. Multiple visible ToolSet paths to
one Tool aggregate on one selectable link. Exact versions, source fields, pin
evidence and ToolDefinition provenance remain inspectable. Tools reached solely
through unselected membership are absent from an Agent summary; their authorized
technical metadata remains in the exact inventory.

Typed outgoing ToolDefinition paths may be contracted for presentation. Original
steps are retained; a display shortcut does not assert a direct executable
relationship. Traversal is bounded and cycle-safe. Synthetic link IDs, grouping
and localized labels stay in the UI; export retains the partial semantic
observation. Existing layout limits, progressive coalescing and disposal remain.
Old-context callbacks cannot restore previous details. No publication, adoption,
permission or execution policy changes.

## Consequences

Agent views become smaller while access paths remain reviewable. Unknown legacy
provenance stays unknown. No graph store, resource, inferred container or permission
authority is added. Reusable UX journeys remain deferred under #1201 until explicit
maintainer UX validation and the owning UX PR's merge; existing implementation
regression and browser smoke checks still apply.
