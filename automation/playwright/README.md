# Agentstration browser automation

This workspace owns the Playwright page objects and journeys used by product UX tests and reproducible external capture.

```powershell
npm ci
npm run install:browsers
npm run test:smoke
```

To exercise the capture contract:

```powershell
npm run capture -- --plan examples/console-home.capture-plan.json --output .work/example-capture
```

The welcome-agent plan replays the first agent created in the handoff demo:

```powershell
npm run capture -- --plan examples/create-welcome-agent.capture-plan.json --output .work/welcome-agent
```

To run that plan against an existing Console without starting local product hosts, override its URL from the command line:

```powershell
npm run capture -- --plan examples/create-welcome-agent.capture-plan.json --output .work/welcome-agent --console-url https://agentstration.example.com
```

Command-line URLs take precedence over plan values. `--workplace-url` is optional for Console-only journeys and can be supplied when a journey also uses Workplace.

Profile inputs may be an ordered array when equivalent environments use different resource names. The journey selects the first available candidate and reports the available options immediately when none match.

See [Browser automation](../../docs/contributing/browser-automation.md) and [ADR-0079](../../docs/decisions/0079-product-owned-browser-journeys.md) for ownership, extension, and external-consumption rules.
