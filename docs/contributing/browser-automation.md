# Browser automation

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

## Design rules

- Page objects own selectors for one product surface.
- Journeys compose page-object actions and emit stable named checkpoints.
- Test specifications add assertions around journeys.
- Capture plans select checkpoints but never contain selectors.
- Prefer accessible role and label selectors. Add `data-testid` only for localized, custom, or otherwise ambiguous controls.
- Wait for URL, health, enabled controls, and visible domain state. Do not add arbitrary sleeps to functional journeys.
- Use typed scenario input. Keep release copy, storyboards, and publication-specific data outside this repository.

## Stable automation contracts

The Playwright workspace keeps its shared vocabulary in three explicit catalogs:

| Contract | Location | Update when |
| --- | --- | --- |
| Test IDs | `src/contracts/test-ids.ts` | A cross-locale or ambiguous control needs a stable locator or capture target. Add the matching literal to the Razor markup and consume only the constant from page objects and tests. |
| Checkpoints | `src/contracts/checkpoints.ts` | A journey exposes a new meaningful user-visible state. Keep existing values stable and update plans that select the new checkpoint. |
| Expected localized text | `src/locales/expected-text.ts` | A semantic localization assertion is added or intentionally changed. Supply every supported locale independently from the product `.resx` files. |

Use accessible roles and names first. Test IDs are a fallback for cross-locale journeys, ambiguity, capture boundaries, and non-visible readiness state. Page objects own locators; journeys own behavior and checkpoint timing; JSON plans contain neither selectors nor product localization constants.

The scoped [`automation/playwright/AGENTS.md`](../../automation/playwright/AGENTS.md) is the maintenance contract for coding agents changing this area.

## Campaign workspaces

A campaign workspace is the durable data sandbox for a related set of UX tests, screenshots, or video takes. It is not the campaign definition itself: locale, theme, viewport, product revision, scenario data, and output evidence remain explicit plan dimensions.

The `create-workspace` journey creates and selects a new workspace and fails if its technical name already exists. Other journeys may accept `workspaceName` to select a workspace prepared by the caller, but they never silently create, replace, reuse, or delete it. Required resources must be seeded or installed explicitly in that workspace before a dependent journey runs.

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
