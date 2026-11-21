# Embedding resource relationships

`ResourceRelations` is a read-only Console component using the System Explorer
LikeC4 island, authorized progressive stream, ToolSet access summaries and exact
provenance. The shared Console resource layout automatically offers it as a
**Relations** section on saved Agent screens, including namespaced/governed Agent
details already using that layout. No Agent screen or canonical route changes.

## Automatic Agent section

`ConsoleResourceDetailsTemplate` owns the localized `#relations` entry and its
content. Its central first-rollout condition checks saved kind `Agent`, a nonempty
UID, a known owner and the existing Console navigation actor context. Other kinds
remain unchanged; enabling later families belongs in the layout, not their
screens. Screens using a different layout must first adopt the common template.
Graph coverage still depends on each family's authorized contributor.

The layout composes family additional sections, preserving the standard
Overview/Definition/Access/YAML destinations. `relations` is reserved by the
layout when enabled; a competing family entry is rejected rather than silently
replacing either region. Creation, pending resources and absent actor context do
not establish a graph root, and the owner is never used as an actor fallback.

The shared template mounts the section on first visit and retains it while
hidden. Switching sections preserves the definition form and draft guard.
Accepted resource generation/ETag, exact identity or actor changes remount the
observation using saved metadata; typing in a form does not refresh the graph.
The component retains its own direction/expansion/refresh and stale-response
protections. Direct fragment navigation retains owner, namespace and actor query
coordinates. Display-only scope badges do not affect graph authorization.

## Relationship workspace

The product labels this workspace **Relations**; renderer/architecture terminology
is not part of its heading. The compact toolbar switches dependencies/usages,
visual depth and Graph/List presentation. Visible-resource counts describe the
current authorized window, never global inventory. Type labels are localized for
known families; unknown protocol kinds retain their exact name.

The canvas takes the whole content width. Large graphs open around the focused
resource at a readable scale; Fit shows the complete graph and Center view
returns the camera to the focused resource. Camera changes never alter the graph
or its canonical layout coordinates. Known resource kinds are localized in the
diagram as well as the list and inspector. Resource and link inspection opens in
an overlay drawer only on selection or an explicit Details action. Technical
identities and exact provenance remain available inside expandable details. A
renderer failure opens the same authorized resources in the styled accessible
list; failure never causes a broader data read.

**Full screen** maximizes this same workspace in the application's shared
`ModalFrame`, above global navigation. Native dialogue behavior supplies the
modal keyboard boundary and Escape; the maximize control regains focus after
restoring if the resource URL is unchanged. Navigation and resource-coordinate
changes close the surface. Direction, depth, selection, presentation and the
received observation remain in the owning component; moving between inline and
maximized presentation remounts/refits only the renderer, without another stream
request. The modal retains the host theme and safe-area/short-viewport scrolling.
No Resource/Graph contract, route, Agent screen or write command changes.

## Minimal integration

```razor
@using Agentstration.Resources
@using Agentstration.SystemGraph.Contracts
@using Agentstration.Web.Components.ResourceRelations

<ResourceRelations Context="actorContext"
                   Namespace="resource.Namespace"
                   Root="@SystemGraphIds.Definition(resource.Uid)" />
```

For manual embedding outside the automatic Agent section, persisted resource screens follow the [resource-screen authoring guide](../ux/resource-screen-authoring.md)
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

Do not declare this manual section on Agents: their Console layout already owns
it. The Console adapter owns physical resource identity,
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

The accessible list works when selected explicitly or after a renderer failure. Inspection exposes
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

The automatic Agent section adds a resource-screen UX journey. Deferred scenario
[#1201](https://github.com/gbaudrit/agentstration/issues/1201) records its exact
routes, prerequisites and success/failure assertions alongside the existing
Explorer coverage. Its maintainer UX-validation and merged-PR gates remain in
force. Component/client regressions and temporary executable desktop/mobile smoke
are required in the implementation; no reusable Playwright journey starts here.

Focused traversal does not expand through a non-root ToolSet. Selected-member
`toolset-access` evidence connects Agents to Tools; ordinary membership cannot
create access to an unselected Tool or an unrelated ToolSet consumer.
