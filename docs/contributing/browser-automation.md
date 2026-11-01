# Browser automation

The measurable route and journey coverage target is defined in [Browser coverage model](browser-coverage.md).

Agentstration owns reusable Playwright journeys for browser-level UX validation and deterministic external capture. The workspace is independent from the .NET solution and lives under `automation/playwright`.

## Setup

```powershell
npm --prefix automation/playwright ci
npm --prefix automation/playwright run install:browsers
```

Node.js 22 or later is required. The dependency lock and Playwright browser revision are versioned with the product.

## Run UX smoke tests

```powershell
npm --prefix automation/playwright run test:smoke
```

The default fixture starts the Console and Workplace on available loopback ports. Each worker uses an isolated directory under `automation/playwright/.work`, the Development bootstrap profile, SQLite, and deterministic AI. Ollama, Azure, Docker, and Internet access are not required.

Set `AGENTSTRATION_PLAYWRIGHT_NO_BUILD=true` only after building both Web projects and the Ollama extension project. Failed tests retain Playwright traces, screenshots, and video under `automation/playwright/test-results`.

For a local diagnostic when the pinned browser binary cannot be downloaded, an explicitly installed Playwright channel may be selected, for example `$env:AGENTSTRATION_PLAYWRIGHT_CHANNEL = "chrome"`. CI always installs and uses the pinned Chromium revision.

### Playwright UI against an existing instance

Set `AGENTSTRATION_CONSOLE_URL` before opening Playwright UI to bypass managed local hosts:

```powershell
$env:AGENTSTRATION_CONSOLE_URL = "http://localhost:53400"
$env:AGENTSTRATION_WORKPLACE_URL = "http://localhost:53401"
$env:AGENTSTRATION_USERNAME = "admin"
$env:AGENTSTRATION_PASSWORD = "admin"
$env:AGENTSTRATION_WORKSPACE_NAME = "playwright-campaign"
Set-Location automation/playwright
npx playwright test --ui
```

`AGENTSTRATION_USERNAME` and `AGENTSTRATION_PASSWORD` are required together when `AGENTSTRATION_CONSOLE_URL` selects external UI-test mode. Explicit `username` and `password` journey input overrides the environment pair. `AGENTSTRATION_WORKSPACE_NAME` selects an existing campaign Workspace by its stable technical name from the Console header; an explicit journey `workspaceName` overrides it. The journey fails immediately if the configured Workspace is not offered by the selector and never creates it implicitly. Managed local hosts retain the public Development fixture `admin / admin`; the environment-selected external mode never falls back to it silently. `AGENTSTRATION_WORKPLACE_URL` is optional for Console-only tests and otherwise falls back to the Console URL. In external mode Playwright neither starts nor stops the product, and it does not isolate or reset its data. Use a disposable instance or a dedicated campaign Workspace, and expect create-only journeys to reject resources left by an earlier run. Remove the environment variables to return to managed isolated hosts.

## Design rules

- Page objects own selectors for one product surface.
- Journeys compose page-object actions and emit stable named checkpoints.
- Test specifications add assertions around journeys.
- Capture plans select checkpoints but never contain selectors.
- Prefer accessible role and label selectors. Add `data-testid` only for localized, custom, or otherwise ambiguous controls.
- Wait for URL, health, enabled controls, and visible domain state. Do not add arbitrary sleeps to functional journeys.
- Use typed scenario input. Keep release copy, storyboards, and publication-specific data outside this repository.

For a new Console or Workplace UX journey, the [UX test scenario follow-up](github-governance.md#ux-test-scenario-follow-up) is a separate technical task. It starts after maintainer UX validation and the UX pull request merge. The UX pull request still maintains existing browser tests and runs the required smoke checks.

## Stable automation contracts

The Playwright workspace keeps its shared vocabulary in three explicit catalogs:

| Contract | Location | Update when |
| --- | --- | --- |
| Test IDs | `src/contracts/test-ids.ts` | A cross-locale or ambiguous control needs a stable locator or capture target. Add the matching literal to the Razor markup and consume only the constant from page objects and tests. |
| Checkpoints | `src/contracts/checkpoints.ts` | A journey exposes a new meaningful user-visible state. Keep existing values stable and update plans that select the new checkpoint. |
| Expected localized text | `src/locales/expected-text.ts` | A semantic localization assertion is added or intentionally changed. Supply every supported locale independently from the product `.resx` files. |

Use accessible roles and names first. Test IDs are a fallback for cross-locale journeys, ambiguity, capture boundaries, and non-visible readiness state. Page objects own locators; journeys own behavior and checkpoint timing; JSON plans contain neither selectors nor product localization constants.

The scoped `automation/playwright/AGENTS.md` file is the maintenance contract for coding agents changing this area.

## Campaign workspaces

A campaign workspace is the durable data sandbox for a related set of UX tests, screenshots, or video takes. It is not the campaign definition itself: locale, theme, viewport, product revision, scenario data, and output evidence remain explicit plan dimensions.

The `create-workspace` journey creates and selects a new workspace and fails if its technical name already exists. Other journeys resolve the Workspace from an explicit `workspaceName`, then `AGENTSTRATION_WORKSPACE_NAME`, and otherwise keep the current Workspace. A configured Workspace is selected directly from the global Console header; the journeys do not visit Organization settings and never silently create, replace, reuse, or delete it. Required resources must be seeded or installed explicitly in that workspace before a dependent journey runs.

For an existing Console instance, a typical campaign begins with:

```powershell
npm --prefix automation/playwright run capture -- `
  --plan automation/playwright/examples/create-campaign-workspace.capture-plan.json -- `
  --output automation/playwright/.work/campaign-workspace -- `
  --console-url https://agentstration.example.com
```

Subsequent plans can select it by adding `"workspaceName": "capture-handoff"` to their journey input. The built-in local capture host is recreated for each command; use one persistent external instance when multiple commands must share campaign state.

The solution-discovery example is intentionally split into dependency-ordered product journeys:

1. Create the campaign Workspace.
2. Create and deploy the participant Agents.
3. Run `create-solution-discovery-flow.capture-plan.json` to create, configure, publish, and activate the handoff Flow.
4. Run `create-solution-discovery-entry.capture-plan.json` to configure and publish the Workplace Entry pinned to that Flow.

Each plan fails when its prerequisites are absent. The Flow journey reports the available Agent technical identifiers when a participant is missing; the Entry journey reports available Flow targets when its binding cannot be resolved. This keeps campaign setup explicit and prevents a capture from silently documenting a different topology.

## Capture from a plan

The example plan captures the authenticated Console home page:

```powershell
npm --prefix automation/playwright run capture -- `
  --plan automation/playwright/examples/console-home.capture-plan.json `
  --output automation/playwright/.work/example-capture
```

The runner starts isolated product hosts unless an external Console URL is provided. It writes the requested PNG files and `capture-manifest.json`, which records the exact product commit, checkout cleanliness, browser version, and asset checksums. When a plan supplies `productRef`, the runner rejects a checkout that does not resolve to that exact commit.

Use `--console-url <url>` to run against an existing Console; add `--workplace-url <url>` only when the selected journey uses Workplace. Command-line URLs override values from the plan. Supplying a Console URL disables local product-host startup.

An external repository should checkout the requested Agentstration tag, run the command from that checkout, and write output into its own workspace. It must not copy the page objects or journeys.

## Data Source and Knowledge lifecycle scenarios

The current Data Source and Knowledge UX was explicitly validated by the maintainer
on 2026-11-01 for [#731](https://github.com/gbaudrit/agentstration/issues/731) and
[#732](https://github.com/gbaudrit/agentstration/issues/732). Their owning PRs
#733 and #739 are merged; #740 completed the composition replacement.

`administer-data-sources` visits `/data-source-profiles`,
`/data-source-profiles/new`, `/data-source-profiles/{Name}`, `/data-sources`,
`/data-sources/new`, and `/data-sources/{Name}`. It creates a profile and two
configurations, checks schema errors, acquires offline content, publishes and
activates a compatible revision, and verifies that historical acquisitions retain
their exact original composition. Disabling a profile exposes unavailable-binding
feedback and prevents acquisition.

`project-knowledge-sources` visits `/knowledge-sources`, `/knowledge-sources/new`,
`/knowledge-sources/{Name}/edit`, and `/knowledge-sources/{Name}` with the
`projections`, `snapshots`, and `retrieval` views. It exercises a single input,
a required input without an acquisition, optional partial failure, and successful
aggregation. It checks Snapshot input, acquisition, profile, Flow, artifact, storage,
and retrieval provenance and proves that reacquisition does not itself project.
The bounded acquisition and projection fixtures invoke no Tools, so their expected
acquisition Tool binding set is explicitly empty.

Both journeys reuse the authentication and execution-scope fixtures, including the
sibling Workspace and Tenant, and use two locally staged durable text artifacts and
published deterministic Flows. The `example.test` configuration values are never
fetched. Ancestor grants and lateral or unauthorized access are checked through the
supported resource APIs. No live model, crawler, remote provider, or Internet call
is part of the scenarios.

Run `npx playwright test source-lifecycle.spec.ts` from `automation/playwright`.
Desktop and 390 × 844 mobile variants are tagged `@smoke` and run in the existing
Browser UX CI lane. The capture checkpoints are `source-lifecycle-acquisitions`
and `source-lifecycle-projections`. Both journeys own their corresponding routes in the application coverage catalog.
Explicit reads of another Workspace’s resources must return HTTP 403; storage
raises the scope authorization failure consumed by the family API error handlers.

## Governed Knowledge administration

`administer-governed-knowledge` implements [#647](https://github.com/gbaudrit/agentstration/issues/647)
on the current Data Source-backed UX delivered by merged PRs #648, #739 and #745.
It composes `project-knowledge-sources` from #732 rather than duplicating acquisition,
projection readiness, partial failure, Snapshot provenance, retrieval and scope-denial
assertions. Implementation was requested by the maintainer on 2026-11-01; the
Data Source and projection UX validation is recorded above. The final routes are
`/data-sources/{Name}`, `/knowledge-sources`, `/knowledge-sources/new`,
`/knowledge-sources/{Name}/edit`, `/knowledge-sources/{Name}` (including `tools`),
`/tools/sets`, `/tools/sets/{Name}`, `/agents/new`, `/agents/{Name}`, `/artifacts`,
`/artifacts/durable/{Id}`, and `/artifacts/staged/{Id}`.

The journey creates a dedicated Workspace Model Profile to preserve shared fixture
usage graphs, publishes the source-specific retrieval ToolSet, inspects its immutable
version, deploys an Agent with only the search member, and checks that saving broader
draft grants does not alter the active revision's pinned permissions. From a Snapshot
artifact link it inspects the durable artifact, explicitly materializes and previews
the local copy, verifies metadata-only credentials cannot read or download content,
extends retention, observes active-lease purge denial, revokes that fixture lease,
and cancels then confirms deletion. The durable artifact and complete Snapshot
history remain unchanged after local-copy purge. The ToolSet page operations are
reusable by #640, which still owns later-version, conflicting-exposure and in-use
deletion cases. ToolSet detail coverage remains `partial` in the catalog until that
separate work is complete.

Prerequisites are the local deterministic host, Development bootstrap, built-in
Knowledge and Artifact Flows, and Platform-administrator access. Fixtures use local
text only, compatible published graph-only Flows, explicit ancestor grants, and a
real unauthorized account; no crawler, Internet content, remote model or cloud storage
is used. External capture mutates the selected instance and requires a disposable
campaign Workspace with these same prerequisites. The scenario changes only its own
Agent and staged copy; it does not purge the durable Snapshot artifact.

Run `npx playwright test governed-knowledge.spec.ts` from `automation/playwright`.
The smoke lane covers desktop, 390 × 844 mobile, and French labels. Capture checkpoints
are `source-lifecycle-projections`, `governed-knowledge-tools-published`,
`governed-knowledge-agent-pinned`, and `governed-knowledge-artifact-purged`.
