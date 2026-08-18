# Agentstration Workplace

## Published Entry execution targets

Entry authoring and Entry execution are deliberately different contracts. The Console saves an `EntryDraft` whose binding selects either an Agent or a Flow. Publishing validates the resource and writes an immutable Workplace projection whose `resolvedTarget` is always an exact, pinned Flow reference. A draft edit is therefore invisible to Workplace until the next publication.

Agent bindings are normalized through hidden, system-managed Direct Agent Flows. Workplace renders these Entries exactly like Flow-bound Entries and submits through the same Work API. Work API creates a WorkItem and FlowRun in both cases; the Flow engine invokes the Agent and maps its output into the existing Work result and Workplace action contracts. Workplace does not receive the administrative binding or any provider/runtime object.

The primary input is declared explicitly with `EntryFieldRole.PrimaryInput`; it is unrelated to a Dashboard's Primary presentation role. The latter remains exclusively on `DashboardEntryReference`.

## Workspace and Dashboard

Workspace is the functional boundary for Interactions, Tasks, Conversations, Notifications, and Dashboards. Dashboard is the UX composition of namespaced Entry references, roles, and order. Entry remains independent from every Dashboard that displays it, and its published `ResolvedTarget` remains an immutable Flow reference.

`home` is only the default seeded Dashboard (`IsDefault = true`), and `personal` is only a Workspace name. Neither name activates engine behavior. Workplace routes make both selections explicit with `/w/{workspace}/d/{dashboard}`; `/` resolves the configured default Workspace and then its default Dashboard. Switching Dashboard does not move or filter Workspace work state.

Pack installation only makes namespaced published Entries available for explicit Dashboard selection. It does not add them to Home.

Entry dependency inspection exposes both the administration binding and the resolved Flow. Agent and user Flow deletion are rejected while a published Entry references them, and a system-managed Direct Agent Flow cannot be deleted independently.

## Iteration 3 UX

The third increment turns the existing functional vertical into one continuous end-user journey without changing its boundaries. A Dashboard is organized around an optional visually emphasized Primary Entry, followed by Featured and Standard Entries, then the current Interaction, recent Tasks, and attention-first notifications. Primary is a Dashboard presentation role only: `EntryRenderer` remains the single generic Prompt/Form renderer and `PrimaryEntryContainer` supplies visual emphasis without introducing a new business type.

## Iteration 4 durable conversation

The Console supervises these durable Tasks through Work API. It does not reuse Workplace UI state or read Work SQLite: `/tasks` is a paginated operational projection, while Workplace remains the place to converse and answer PendingActions. Optional links from Console reopen the corresponding Workplace Task or Interaction.

An Entry now opens a durable Interaction rather than a terminal Task funnel. The active conversation has a permanent composer after immediate replies, during non-blocking work, and after results or artifacts. A completed Task moves its Interaction to `Idle`; it does not close it. `New request` returns to the Primary Entry while the previous Interaction remains available under Recent conversations.

`prepare-report` applies the standard detail level by default and starts work immediately. A follow-up such as “Make it shorter and suitable for executives” is accepted asynchronously, builds a controlled `InteractionContinuationContext`, and starts a new FlowRun. The new run records its terminal predecessor as `ParentFlowRunId`, retains the Interaction, public Task, and triggering message identifiers, and never reopens the previous FlowRun. The same public Task then exposes the successive results. Artifacts appear only when the Work result explicitly declares a durable deliverable; conversation text is never converted into a synthetic file.

Continuation execution uses a child WorkItem internally. Workplace projects that execution onto the anchor WorkTask, so the user sees one living Task rather than technical run records. The context contains recent functional messages and result/artifact references only; it excludes persistence entities, runtime internals, providers, models, technical prompts, and traces. An optional Entry continuation target can override the initial target, but the initial target remains the default.

Simple PendingActions are conversation-native. Their prompt is rendered as an Agentstration turn rather than an operations panel. `guided-request` asks for a style with one-click Choice buttons; confirmation is also one click, and Input uses a small inline composer. The chosen value is recorded as a user message. Blocking questions disable the permanent composer with an explanation until resolved.

The Work API adds recent Interaction listing and makes message continuation explicit:

```http
GET  /api/workspaces/{workspaceName}/interactions?take=20
POST /api/workspaces/{workspaceName}/interactions/{interactionId}/messages
```

The POST returns `202 Accepted` with the accepted message, updated Interaction, functional action, and public Task projection. Progress, FlowRun completion, results, artifacts, and messages continue over Workspace-scoped SignalR, with targeted REST refresh after reconnect.

Prompt Entries provide declarative suggestions that fill the composer without submitting, multiline input, an explicit send action, loading/disabled states, and Ctrl/Command+Enter submission. Unsupported attachment controls stay hidden. Pending actions appear inside the conversation that caused them; their single-use raw resume token stays in the initiating browser response and is never persisted or exposed by later Interaction reads.

When an Interaction becomes a Task, Workplace keeps the user in context with an inline Task card. Compact progress retains the current functional activity but suppresses terminal lifecycle markers already conveyed by the answer, pending action, result, or artifact; detailed progress retains the complete functional history. Task detail uses functional activity labels, conversation context, readable results, downloadable artifact cards, and conditional Task actions. Storage keys, Flow Run identifiers, provider/runtime details, and raw JSON infrastructure views are not part of the default experience.

The shell continues to reuse the Console design system and icon language, with end-user vocabulary and a responsive composition: wide two-column workspace, tablet stacking, and a touch-friendly single column with bottom navigation below 620 px. Loading, empty, disconnected-realtime, API-unavailable, expired-action, failure, cancellation, no-result, and no-artifact states remain explicit. SignalR updates only the affected active conversation, Task, or notification summary; REST remains authoritative after reconnect.

## Deployable structure

The second Workplace increment separates the end-user experience from the operations Console while retaining one modular codebase:

```text
Agentstration.Work                    Workspace-owned functional model
Agentstration.Application             orchestration, suspension/resume and projections
Agentstration.Work.Contracts          stable HTTP and SignalR contracts
Agentstration.Work.Storage.*          independent SQLite Work persistence
Agentstration.Workplace.Client        HTTP-only API client and reconnecting SignalR client
Agentstration.Workplace.Components    reusable Razor business components
Agentstration.Workplace.Web           standalone end-user Blazor host
Agentstration.Web                     authoritative server, operations Console, APIs, hub and workers
```

`Agentstration.Workplace.Web` references neither the server host nor application, infrastructure, runtime, provider, or storage implementations. Its only server communication is the configurable API base URL and Workplace hub URL. If the hub URL is omitted, it is derived as `hubs/workplace` from the API base URL.

Direct local launch uses two processes:

```powershell
$env:AI__Provider = "Deterministic"
dotnet run --project src/Agentstration.Web
dotnet run --project src/Agentstration.Workplace.Web
```

The defaults are `http://localhost:5100` for the authoritative server and Console, and `http://localhost:5180` for Workplace. Flow authoring and Entry execution both use the Flow API hosted by the authoritative server so that selection, publication, and execution resolve immutable versions from the same Flow store. Aspire starts `agentstration-console` and `agentstration-workplace` and injects the server endpoint into Workplace.

## Isolation and public model

Workspace is the only Workplace isolation boundary. There is no Workplace Tenant, User, Profile, account, role, permission, or authentication model in this increment. Every owned repository lookup and public route includes `WorkspaceId` or `{workspaceName}`. Cross-workspace reads and mutations return not found.

The correlation chain is:

```text
InteractionId → WorkTaskId → WorkExecutionId → FlowRunId → runtime execution

For the shipped local topology, Workplace Web is a separate Interactive Server host while Work and Flow APIs remain in the authoritative Agentstration host. Its server-side API client forwards only the Agentstration session-cookie chunks and Workspace-selection cookie to the exact configured API origin; redirects are disabled. The API remains responsible for authenticating the cookie, resolving the canonical Management context, and authorizing `runs/execute`. A Flow-backed Work submission without that context is rejected before a WorkItem or FlowRun is queued. This cookie relay is intentionally limited to the local, same-session topology. A Workplace deployed independently must authenticate API calls through a standard mechanism such as OAuth Bearer; it must not forward cookies to a broader set of origins.
```

Conversation messages use the functional roles `User`, `Agentstration`, and `System`; these labels are not identity records. Exact submitted JSON and attachments remain attached to the Interaction. Results, activities, notifications, and artifact metadata are separate durable projections. An execution producer persists artifact bytes through `IArtifactStore` before returning its `WorkResult`; the resulting opaque storage key, media type, and length are carried by `WorkArtifact` and projected without copying. Downloads remain behind a workspace-scoped API route.

## Pending actions and resume

`PendingAction` is the unified durable suspension record for input, confirmation, choice, file, and approval requests. It carries its Workspace and Interaction correlation, lifecycle status, expiry, response, continuation step, and optimistic version.

The server creates a cryptographically random resume token and persists only its SHA-256 hash. The raw token is returned once inside the corresponding structured action. Public PendingAction DTOs and SignalR events never expose the hash. A response must match Workspace, Interaction, PendingAction and token; the action is then completed atomically and cannot be replayed. Expected failures use Problem Details: invalid input/token is `400`, missing/cross-workspace resources are `404`, and expired/already-resolved actions are `409`.

Stable action discriminators are `respond`, `requestInput`, `requestConfirmation`, `requestChoice`, `createTask`, `showResult`, and `showError`. The `prepare-report` demo Entry exercises two server-side suspensions (choice, then confirmation), creates a Task, and runs the deterministic local Flow. Its synthesis remains in the conversation and its structured result projection; no downloadable artifact is invented. `quick-answer` demonstrates an Interaction completed without a Task.

## HTTP and real time

All Workplace-owned routes are nested under the Workspace:

```text
POST /api/workspaces/{workspace}/entries/{entry}/interactions
GET  /api/workspaces/{workspace}/interactions/{id}
GET  /api/workspaces/{workspace}/interactions/{id}/messages
POST /api/workspaces/{workspace}/interactions/{id}/pending-actions/{actionId}/responses
GET  /api/workspaces/{workspace}/tasks
GET  /api/workspaces/{workspace}/tasks/{id}
POST /api/workspaces/{workspace}/tasks/{id}/pause|resume|cancel
GET  /api/workspaces/{workspace}/tasks/{id}/activities|results|artifacts
GET  /api/workspaces/{workspace}/tasks/{id}/artifacts/{artifactId}/content
GET  /api/workspaces/{workspace}/notifications
POST /api/workspaces/{workspace}/notifications/{id}/read
POST /api/workspaces/{workspace}/notifications/read-all
```

`/hubs/workplace` uses the exact group key `workspace:{workspaceId}` and stable event names: `InteractionUpdated`, `MessageAdded`, `PendingActionCreated`, `PendingActionResolved`, `TaskCreated`, `TaskStatusChanged`, `TaskActivityAdded`, `TaskResultAdded`, `TaskArtifactAdded`, `NotificationCreated`, `NotificationUpdated`, and `UnreadNotificationCountChanged`.

The client reconnects automatically, explicitly rejoins its Workspace group, and refreshes authoritative projections over HTTP after events or reconnect. SignalR is an invalidation/differential channel, not a second state store.

## UX

Workplace deliberately reuses the Console design system from `Agentstration.Web.Components`: typography, tokens, panels, buttons, badges, empty/loading/error states, brand lockup, responsive breakpoints, sidebar, top bar, and compact mobile composition. Workplace keeps end-user vocabulary and navigation (`Home`, `Tasks`, `Notifications`) while matching the Console visual language.

The Dashboard view renders an optional Primary Entry as the central intention surface, configured Featured and Standard Entries, PendingAction panels in the active conversation, inline and recent Tasks, and an attention-first notification summary. Task detail exposes conversation, functional progression, readable results, deliverables, and conditional pause/resume/cancel actions. Flow Run and runtime implementation details are not part of the default Workplace presentation.

## Validation

The offline suite covers direct Task execution, one-click PendingAction resume, invalid and single-use tokens, non-persistence of raw resume tokens, Workspace isolation, deterministic completion, continuation to a parented immutable FlowRun, multiple results, explicit artifact projection, immediate-response follow-up, and cross-workspace artifact-store isolation. Component tests cover generic Primary/Standard Entry rendering, suggestion confirmation, permanent composer states, inline pending actions, resolved answers, successive outputs, functional progression, result de-duplication, and artifact information boundaries. Architecture tests enforce that Workplace Web cannot reference Console or server implementation assemblies and that Work API has no Console assembly dependency.
