# Approved compact all-list evidence (#1121)

These **96 unedited screenshots** come from the actual SQLite/offline application and its disposable browser fixtures. They supersede the earlier first-wave, all-list and Flow-prototype galleries for this compact presentation. No generated image, mock DOM, image manipulation or production demonstration resource is used.

The baseline product source for the Console and CI captures is `44058e720b118545a19a73bb08a3f9c20552ec05`. The six Workplace captures are refreshed on `7db199ff6deebdaf468718bcf7e69d008ecaf2c2`: the shared context now carries the authorized Workspace ID, so task/artifact/notification markers resolve the real name only for that exact owner. No extra product request is added. A component regression rejects mismatching owner IDs and follows context changes; all **994 Fast tests** pass on that follow-up. The four maintained local version/action variants passed on that source: Chromium **154.0.8037.57**, English desktop **1440 × 1000**, French mobile **390 × 844**, both themes. The local browser differs from CI's pinned Playwright browser because its download host is unavailable in this environment. CI uses Playwright **1.63.0** with its pinned Chromium. Captures store original bytes and SHA-256 hashes in the JSON indexes below.

The expanded CI campaign at coverage `d3ccdf873b2d952b3f284d58883544f44486bb08`, stacked on that exact product source, passed **483 cases**, with zero failures, skipped or flaky cases. [Full run and its seven evidence artifacts](https://github.com/gbaudrit/agentstration/actions/runs/43391503391). The latest subsequent documentation/coverage HEAD checks are linked from both draft PRs; this immutable run remains the capture provenance.

## Final resource identities and business versions

| Surface | Desktop English dark | Desktop English light | Mobile French dark | Mobile French light |
| --- | --- | --- | --- | --- |
| Agents, generation retained | [View](agents-en-US-dark.png) | [View](agents-en-US-light.png) | [View](agents-fr-FR-dark.png) | [View](agents-fr-FR-light.png) |
| Data Source Profiles, actual publication | [View](data-source-profiles-en-US-dark.png) | [View](data-source-profiles-en-US-light.png) | [View](data-source-profiles-fr-FR-dark.png) | [View](data-source-profiles-fr-FR-light.png) |
| Flows, real publication or dash | [View](flows-en-US-dark.png) | [View](flows-en-US-light.png) | [View](flows-fr-FR-dark.png) | [View](flows-fr-FR-light.png) |
| Extension catalog, installed version | [View](extensions-en-US-dark.png) | [View](extensions-en-US-light.png) | [View](extensions-fr-FR-dark.png) | [View](extensions-fr-FR-light.png) |
| Model Profile selector, direct Choose | [View](profile-primary-choice-en-US-dark.png) | [View](profile-primary-choice-en-US-light.png) | [View](profile-primary-choice-fr-FR-dark.png) | [View](profile-primary-choice-fr-FR-light.png) |
| Entries, direct Configure and real owner | [View](entry-cards-en-US-dark.png) | [View](entry-cards-en-US-light.png) | [View](entry-cards-fr-FR-dark.png) | [View](entry-cards-fr-FR-light.png) |
| Pack Projects, target archive version | [View](pack-project-card-en-US-dark.png) | [View](pack-project-card-en-US-light.png) | [View](pack-project-card-fr-FR-dark.png) | [View](pack-project-card-fr-FR-light.png) |

Desktop table variants are also archived for [Agents](agents-table-en-US-dark.png), [Profiles](data-source-profiles-table-en-US-dark.png), [Flows](flows-table-en-US-dark.png) and [Extensions](extensions-table-en-US-dark.png), in both themes. [Local test results, capture dimensions and hashes](local-observations.json).

The final captures exposed and then verified fixes for Entry-card padding/direct Configure and populated Pack-card wrapping at mobile width. Archive targets, installed versions, generations and immutable references keep their business meanings. Source and authorization tests separately verify real publication lookup without activation, draft isolation and inherited-parent discovery without disclosing denied parents.

## Full authorized owner disclosure

| Variant | Full type/name panel |
| --- | --- |
| Desktop English dark | [View](long-owner-desktop-en-dark.png) |
| Desktop English light | [View](long-owner-desktop-en-light.png) |
| Mobile French dark | [View](long-owner-mobile-fr-dark.png) |
| Mobile French light | [View](long-owner-mobile-fr-light.png) |

The tests use the maximum supported 120-character tenant name and homonymous resource names in different namespaces. They assert the 10–12px I/T/W visual, usable target, full accessible type/name, hover/focus, real touch/tap, Escape, complete copy, exact scoped destination and absence of owner-resolution requests caused by row interaction. Copy receives keyboard focus before activation. Namespace is independent of owner.

## Populated embedded and specialized collections

The following successful domain checkpoints retain their family commands and assertions. They cover native selectors, histories, execution evidence, contextual ownership, revisions and immutable version references without inventing a resource lifecycle. Their observations are separate from top-level route checks. Checkpoint locale/theme follows each existing domain journey rather than a universal variant matrix.

| Domain workflow | Actual checkpoints |
| --- | --- |
| tasks, Flow executions, events and Agent runs | [flow-observability-task](embedded-c2c7c6dd-flow-observability-task.png) · [flow-observability-task-run](embedded-c2c7c6dd-flow-observability-task-run.png) · [flow-observability-global-run](embedded-c2c7c6dd-flow-observability-global-run.png) · [flow-observability-agent-run](embedded-c2c7c6dd-flow-observability-agent-run.png) · [flow-observability-events](embedded-c2c7c6dd-flow-observability-events.png) |
| Pack resource selection and build history | [pack-project-composer](embedded-2e23ce1c-pack-project-composer.png) · [pack-project-created](embedded-2e23ce1c-pack-project-created.png) · [pack-project-built](embedded-2e23ce1c-pack-project-built.png) |
| Source versions, bindings and channels | [source-import-form](embedded-193f028d-source-import-form.png) · [source-imported](embedded-193f028d-source-imported.png) |
| acquisition history and profile revisions | [source-lifecycle-acquisitions](embedded-311e4409-source-lifecycle-acquisitions.png) |
| projections, input evidence and snapshots | [source-lifecycle-projections](embedded-11f18c74-source-lifecycle-projections.png) |
| ToolSets, provider groups and pinned Agent members | [source-lifecycle-projections](embedded-523e4b63-source-lifecycle-projections.png) · [governed-knowledge-tools-published](embedded-523e4b63-governed-knowledge-tools-published.png) · [governed-knowledge-agent-pinned](embedded-523e4b63-governed-knowledge-agent-pinned.png) · [governed-toolsets-governance-verified](embedded-523e4b63-governed-toolsets-governance-verified.png) |
| Trigger occurrences and operational ownership | [operational-triggers-editor](embedded-059fe2a5-operational-triggers-editor.png) · [operational-triggers-created](embedded-059fe2a5-operational-triggers-created.png) · [operational-triggers-submitted](embedded-059fe2a5-operational-triggers-submitted.png) · [operational-triggers-isolation](embedded-059fe2a5-operational-triggers-isolation.png) |
| members, personal tokens and command results | [console-home](embedded-9c48161a-console-home.png) · [console-admin-organization](embedded-9c48161a-console-admin-organization.png) · [console-admin-member](embedded-9c48161a-console-admin-member.png) · [console-admin-preferences](embedded-9c48161a-console-admin-preferences.png) · [console-admin-token](embedded-9c48161a-console-admin-token.png) · [console-admin-navigation](embedded-9c48161a-console-admin-navigation.png) |
| Schema versions | [schema-administration-completed](embedded-46095476-schema-administration-completed.png) |
| provider models and Model Profile capabilities | [model-administration-provider](embedded-be4fea05-model-administration-provider.png) · [model-administration-runtime](embedded-be4fea05-model-administration-runtime.png) · [model-administration-profile](embedded-be4fea05-model-administration-profile.png) · [model-administration-updated](embedded-be4fea05-model-administration-updated.png) · [model-administration-deleted](embedded-be4fea05-model-administration-deleted.png) |
| compact Flows align versions and paginate en-US dark  | [flows-compact-managed](embedded-0ea3942f-flows-compact-managed.png) · [flows-compact-table](embedded-0ea3942f-flows-compact-table.png) · [flows-compact-cards](embedded-0ea3942f-flows-compact-cards.png) · [flows-compact-inherited](embedded-0ea3942f-flows-compact-inherited.png) |
| compact Flows align versions and paginate en-US light  | [flows-compact-managed](embedded-0c97d718-flows-compact-managed.png) · [flows-compact-table](embedded-0c97d718-flows-compact-table.png) · [flows-compact-cards](embedded-0c97d718-flows-compact-cards.png) · [flows-compact-inherited](embedded-0c97d718-flows-compact-inherited.png) |
| compact Flows align versions and paginate fr-FR dark | [flows-compact-managed](embedded-232bc0d5-flows-compact-managed.png) · [flows-compact-cards](embedded-232bc0d5-flows-compact-cards.png) · [flows-compact-inherited](embedded-232bc0d5-flows-compact-inherited.png) |
| compact Flows align versions and paginate fr-FR light | [flows-compact-managed](embedded-7f69c426-flows-compact-managed.png) · [flows-compact-cards](embedded-7f69c426-flows-compact-cards.png) · [flows-compact-inherited](embedded-7f69c426-flows-compact-inherited.png) |

[CI observations, per-case results, exact artifacts and screenshot hashes](ci-observations.json). These archived case records cover five downloaded shards; the two remaining route shards are recorded in the route index below. Their 80 + 71 passing cases complete the 483-case total.

## Workplace

A separate exact-owner follow-up execution passed the complete populated Workplace interaction and saved these six stable states:

- [workplace-home](workplace-home.png)
- [workplace-response-completed](workplace-response-completed.png)
- [collections-workplace-activity](collections-workplace-activity.png)
- [collections-workplace-tasks](collections-workplace-tasks.png)
- [collections-workplace-task](collections-workplace-task.png)
- [collections-workplace-notifications](collections-workplace-notifications.png)

[Workplace result, observed collection counts and hashes](workplace-observations.json). The journey additionally verifies full accessible owner names, focus disclosure, Escape and focus restoration. Owner labels are saved in the raw observations. It submits a deterministic report request, waits for its real Task/Agent response, inspects activity, task list/details and notifications, then marks a notification read. Native chronological/thread behavior remains intact.

## All route families and the adoption matrix

All **98 route/tab/alias cases** (49 English desktop dark + 49 French mobile light) passed and attached actual screenshots. [Exact route cases, job result lines, artifact links and attachment names](route-evidence.json). The two complete route artifacts retain all PNG bytes and their JSON/HTML reports. They are linked rather than duplicated in this repository gallery.

The [271-active surface matrix](../../../collection-surfaces-1121.json) records 71 shared-frame and 200 specialized functional collections, four separate unmounted legacy renderers and 114 audited repeated-item sources. It records immutable source-blob provenance, applicable route evidence and existing populated-domain specifications per surface. Functional collections are not routes or raw loops. Every specialized renderer has a domain-specific treatment; retaining a useful tree, timeline, message history or native selector is an adaptation decision.

A route screenshot can show an empty host and does not establish a populated embedded interaction. Domain journeys do not instantiate every possible adapter state, permission combination or variant cross-product. Source/component/API checks, the common presentation contract and family regressions complement these screenshots; they are not maintainer UX acceptance. Optional external integrations remain reported separately in the delivery validation. Both issues remain open and both PRs remain drafts.


## Trigger permission readiness follow-up

The final-head implementation run exposed an absent Enable command after detail navigation. A deterministic component regression reproduces the same lifecycle defect: when shared permissions arrive after the resource, the authorized command stays absent. The unauthorized case correctly remains without a command. Trigger details now await the existing shared context before loading; layout/page requests remain deduplicated to one. Opening confirmation still makes no save.

On product `0219023627de7e05b750e5f400e936dee3764174`, both regression cases pass, the full **996-test Fast lane** passes, the maintained Trigger journey passes **three consecutive repetitions**, and its populated contract case passes. No browser assertion, timeout or retry was relaxed. [Raw results and exact-source observations](trigger-observations.json). These four additional unedited checkpoints retain the baseline captures above with their original provenance:

- [operational-triggers-editor](trigger-permissions-operational-triggers-editor.png)
- [operational-triggers-created](trigger-permissions-operational-triggers-created.png)
- [operational-triggers-submitted](trigger-permissions-operational-triggers-submitted.png)
- [operational-triggers-isolation](trigger-permissions-operational-triggers-isolation.png)
