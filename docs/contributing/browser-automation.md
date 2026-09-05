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
