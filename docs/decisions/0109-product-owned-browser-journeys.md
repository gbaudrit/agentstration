# ADR-0109: Browser journeys are product-owned reusable automation assets

## Context

Agentstration needs browser-level UX tests for the Console and Workplace. The separate communication repository also needs reproducible screenshots and video source material for a specific released product version. Duplicating navigation scripts would let selectors, fixtures, and user journeys drift away from the UI that owns them.

Browser tests and editorial production nevertheless have different lifecycle rules. UX tests may block a product change. Communication must never block a product release, and editorial plans and rendered assets do not belong in the product repository.

## Decision

Agentstration owns a TypeScript Playwright workspace under `automation/playwright`. It contains the product host fixture, stable page objects, reusable journeys, browser tests, named checkpoints, and a capture CLI.

A journey describes product interaction and accepts typed scenario data. Test specifications wrap journeys with assertions. The capture CLI wraps the same journeys with a checkpoint recorder. Journeys contain neither editorial copy nor rendering and publication logic.

The default managed host fixture starts the Console and Workplace against isolated local state, the Development bootstrap account, SQLite, and deterministic AI. Tests remain offline. Semantic accessible selectors are preferred; `data-testid` is reserved for controls whose stable product meaning cannot be selected reliably through accessibility semantics.

An external consumer invokes the Playwright workspace from a checkout of the exact product tag or commit that it documents. The workspace is not published as an npm package in this increment. Capture plans select a journey and checkpoints but contain no browser selectors. The runner records the product commit and checksums of generated assets.

## Consequences

- UI navigation knowledge evolves with the product and is available to both tests and communication.
- A released tag contains the matching automation needed to reproduce its captures.
- Browser tests can remain strict while communication selects publication-quality viewports and checkpoints.
- Communication still owns editorial plans, storyboards, post-processing, and final assets.
- Consumers must prepare a checkout of the requested product revision and run `npm ci` in its Playwright workspace.
- Browser binaries and Node dependencies add a separate CI job, but they do not become dependencies of the .NET solution or runtime product.
