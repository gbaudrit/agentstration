# Give resource details a consistent overview and editing structure

## Area
Console

## Feature type
User-facing capability

## Console or Workplace UX impact
Console UX change

## Problem or need
Resource details mix saved operational information and editable fields. Administrators need a readable overview outside the editing form, with predictable access to editing, permissions and YAML. The composition should preserve each resource family's additional tabs and rights.

## Expected outcome
Model Profile and Agent detail pages share a clear header and Overview, Edit, Access and YAML navigation where those capabilities apply. Overview represents saved data; the editor makes unsaved changes explicit. Existing family actions and routes remain usable.

## Proposed solution
Consolidate ResourceHeader, ResourceTabs and the existing ResourceAccessPanel. Keep display/technical names together. Allow family sections and additional tabs without forcing runtime-specific fields into every resource. Distinguish true in-page tabs from links to another route.

## Scope
- Pilots: `/modelprofiles/{Name}` then `/agents/{Name}`; preserve existing creation routes and direct-link behavior.
- Overview for identity, ownership, relevant status, saved configuration summary and supported usage summary. Only relevant families show desired versus active generation/deployment.
- Edit hosts the domain form and FR-02 behavior. Creation may use the form directly until a persisted overview exists.
- Access reuses #795 and existing ownership/policy logic, including read-only/inherited/managed-resource cases; do not invent descendant grants for Workspace-owned resources.
- YAML preserves existing viewing and editing capabilities, source validation and permissions. The prototype's read-only YAML does not justify removing product editing.
- Localized accessible navigation, stable tab/panel IDs, keyboard interaction, direct links and retained domain-specific tabs such as tools, contracts, executions or usages.

## Out of scope
Four mandatory tabs on operational tasks or conversations, new authorization capabilities, a new manifest editor, removing domain tabs, changing resource routes or a site-wide navigation redesign.

## Dependencies
Parent: [FR-00](FR-00-resource-administration.md). Depends on [FR-02](FR-02-safe-editing.md); reuse T-01 and the Access panel delivered by #795. FR-05 supplies the scalable usage presentation separately; initial overview must not imply unsupported usage enumeration.

## Alternatives considered
A single large edit page makes saved state hard to inspect. A universal resource base class would move unrelated domain behavior into the UI shell. Four tabs on every route would invent capabilities some objects do not have.

## Deployment impact
None

## Acceptance criteria
- [ ] Both pilots expose an overview outside the form, a dedicated editing view and the existing Access/YAML capabilities allowed by policy.
- [ ] Overview uses the last saved representation; unsaved form/YAML changes remain clearly identified and preserved under FR-02.
- [ ] The header keeps display and technical names together and presents ownership/managed status without implying edit permission.
- [ ] Domain-specific tabs, actions, route aliases and deep links retain their behavior; users can navigate with keyboard and direct links.
- [ ] Access composes the existing policy panel and does not broaden rights; inherited or managed resources remain appropriately read-only.
- [ ] YAML editing, if available before adoption, remains available with validation and draft protection.
- [ ] Layout and focus remain usable on desktop/mobile in light/dark themes, with complete en-US/fr-FR labels.

## UX test scenario follow-up
Child: pending parent publication. Create and attach a separate native Technical task child labeled technical-task and test-scenario-task before the UX PR. New reusable automation starts after explicit maintainer UX approval and merge. Existing regression repairs, behavior tests and browser smoke remain required in that PR.

Routes: both detail routes above, `/modelprofiles/new`, `/agents/new` and existing family-tab deep links. Fixtures: deterministic saved resources, Instance/Tenant/Workspace ownership cases, one writable and one inherited/read-only case, a restricted user, and differing saved/active Agent generations where supported. Actions: visit overview, edit without saving, switch Access/YAML/family tabs, return, save and reopen via direct link. Success: saved overview and draft are distinct, input survives navigation, rights and extra tabs remain intact. Expected failures: unauthorized edits cannot execute; invalid raw YAML remains available with errors rather than disappearing. Include keyboard tab activation and desktop/mobile/theme smoke.

## Additional context
Local planning ID: FR-03. ResourceTabs and ResourceHeader are existing integration points. Proposed labels: enhancement, triage:agent, triage:pending-review; no priority assigned.

## Attachments
[Cross-screen analysis](../analyse-des-ecrans-transverses.md), [reference overview and editor](../maquette-ecran-type.md).

## Preliminary checks
- [x] I searched for an existing issue covering this request. Open/closed review on 2026-11-09 identified #795 as the delivered Access capability and #898 as usage semantics; this request consolidates composition. Recheck before publication.
