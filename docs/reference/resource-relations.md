# Embedding resource relationships

`ResourceRelations` is a read-only Console component prepared for future resource
detail tabs. It reuses the System Explorer LikeC4 island, authorized progressive
stream, ToolSet access summaries and exact provenance. It does not install tabs
on resource screens or add a production route.

## Minimal integration

```razor
@using Agentstration.Resources
@using Agentstration.SystemGraph.Contracts
@using Agentstration.Web.Components.ResourceRelations

<ResourceRelations Context="actorContext"
                   Namespace="resource.Namespace"
                   Root="@SystemGraphIds.Definition(resource.Uid)" />
```

Persisted resource screens follow the [resource-screen authoring guide](../ux/resource-screen-authoring.md)
and compose `ConsoleResourceDetailsTemplate`. Declare a primary `relations` entry
in its `AdditionalSections` and render the component from `AdditionalContent` for
that path. Preserve any existing family sections and fragments. Do not replace
`Sections`, `Content`, or the standard Overview/Definition/Access/YAML sections.

```razor
<ConsoleResourceDetailsTemplate ...
    AdditionalSections="resourceSections">
    @* Keep the screen's other named regions. *@
    <AdditionalContent Context="path">
        @if (path == "relations")
        {
            <ResourceRelations Context="actorContext"
                               Namespace="resource.Namespace"
                               Root="@SystemGraphIds.Definition(resource.Uid)" />
        }
    </AdditionalContent>
</ConsoleResourceDetailsTemplate>
```

The screen supplies `resourceSections`, including
`new ResourceNavigationItem("relations", localizedRelationsLabel, "hierarchy")`;
use its existing bilingual catalog for the label. Only offer the section for a
saved resource with a known nonempty UID, never an unsaved creation draft. The
template mounts additional sections on first visit and retains their hidden panes;
therefore ordinary Overview visits do not start a graph request. Its existing
fragment navigation owns `#relations`, keyboard/mobile navigation and draft leave
protection. This component does not parse or rewrite the resource page URL.

This is the integration contract; resource-screen adoption is not installed by
this component preparation. The Console adapter owns physical resource identity,
while `PresentationScopeRef` is display-only and must not be passed as the actor
context. Use the screen's current authorized actor context.

The host supplies the current authorized **actor context**, independently of the
resource owner. A Workspace actor inspecting an Instance resource retains its
Workspace permissions. Namespace selects the root; authorized neighbors can cross
namespaces. Root uses an exact UID-derived graph ID, never a name alone.

For a generation/publication, use `SystemGraphIds.Generation(uid, generation)` or
`SystemGraphIds.Publication(uid, version)`. Native AgentRevision resources use
their own definition node IDs. No latest-version selection, homonym rebinding or
pin rewriting occurs. A definition root can have consumers of several recorded
generations; exact generation/publication roots preserve that distinction.

The Console registers `ISystemGraphStreamClient` through the existing
`SystemExplorerApiClient`. Other hosts must supply the authorized port and
localization services. Loading starts after the first interactive render.

## Host callbacks

- `SelectedChanged` receives the visible `SystemGraphNode` selected in the diagram
  or accessible list.
- `OpenResource` receives that exact node. Resolve a supported detail URL using
  its owner, namespace and version; do not silently open a current homonym.
- `OpenExplorer` receives a `ResourceRelationsFocus` with actor context, current
  root ID/namespace, direction and displayed depth, including local recentering.
- `InitialDirection` accepts `Dependencies` (default) or `Usages`.

After a successful resource edit, a host can call `RefreshAsync()` through its
component reference, or remount with a key based on the resource ETag. Refresh
keeps the current focus/direction/depth and obtains a new authorized observation.

Navigation buttons are offered only when their callback is supplied. Recentering
retains the actor context and uses the selected node's exact ID and namespace. A
return action restores the screen's original root. Context/root/namespace changes
clear old data and cancel its request; late results cannot restore it. Authoring,
adoption, publication and execution remain family-owned commands outside this UI.

## Exploration and rendering

The stream accepts `direction=dependencies` or `direction=usages`, requiring a
root. These follow outgoing or incoming references while retaining the original
consumer-to-dependency arrows. Focused traversal excludes lifecycle publication,
logical membership and deployment-configuration edges; an exact publication does
not expand into sibling versions. Default `context` preserves existing Explorer
behavior. Authorization, binding/provenance checks, bounded working sets,
cancellation, heartbeats and fail-closed invalidation are shared by all modes.

The component reads one finite directional observation within existing bounds:
10,000 authorized nodes, 20,000 relations and 32 technical traversal steps. It
initially shows one **visual** level after generation grouping and ToolDefinition
contraction. Depth changes reuse received metadata, up to eight visual levels and
80 displayed nodes. Window and source truncation are reported explicitly. Family
enumeration still costs work; this is not an indexed or constant-time lookup.

ToolSets remain versioned Agent-to-Tool link provenance, never inferred C4
containers. Unselected membership cannot create a summary access grant. Selecting
a link exposes its original paths and ToolDefinition evidence. Native LikeC4
rectangles retain kind labels and gain local Tabler icons extracted from the
product sprite. Unknown kinds receive a generic icon. No authored icon URL,
external icon request or graphical field enters the semantic graph. Focused
layout is left-to-right, highlights the root and follows the application's theme.
Localized zoom/fit controls change only the viewport. A changed focused diagram
fits its new bounds after layout; theme-only changes preserve the viewport.

The accessible list works with a disabled or failed diagram. Inspection exposes
received identity, ownership and generation/publication evidence. Missing and
inaccessible roots remain indistinguishable. An empty view states only that no
relation is visible in the observed scope; global completeness is never claimed.
A stream error, sequence gap or incomplete body clears all provisional data.

## Validation and later adoption

Application tests cover direction, cycles, depth, hidden roots, revocation and
exact publication boundaries. Client/component tests cover selected ToolSet
evidence, visual limits, localization, expansion without refetching, cancellation,
context changes, callbacks and fallback. Native renderer tests cover local icons,
rectangles, layout and unchanged graph input. Temporary local browser hosts
validate the executable component.

A new resource-screen UX journey is **Not applicable** to this preparation: no
resource route or tab is added. Future screen adoption owns its journey. Existing
Explorer regressions/browser smoke still apply; deferred scenario #1201 retains
its maintainer UX validation and merged-PR gates.

Focused traversal does not expand through a non-root ToolSet. Selected-member
`toolset-access` evidence connects Agents to Tools; ordinary membership cannot
create access to an unselected Tool or an unrelated ToolSet consumer.
