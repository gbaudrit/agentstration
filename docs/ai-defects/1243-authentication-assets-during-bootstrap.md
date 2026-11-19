# CAR-1243: Authentication assets blocked during Bootstrap

## Status

Open — 2026-11-19

## References

- Issue: [#1243](https://github.com/gbaudrit/agentstration/issues/1243)
- Introducing change: [2884a6cb](https://github.com/gbaudrit/agentstration/commit/2884a6cb), implementing #1239
- Corrective pull request: None; correction on `feat/bootstrapping`.
- Related ADRs: [ADR-0242](../decisions/0242-model-provider-discovery-runs-asynchronously.md)

## Defect

The startup allowlist exposes authentication pages after topology setup but blocks
`/auth.css`. The login form loses its layout, and the brand image displays at its
intrinsic size. The separate root form helper `/resource-name-fields.js` is also
omitted. Normal startup readiness hides the omission because the fence then stops
restricting requests.

## Detection

- Stage: Maintainer validation before merge.
- Detection mechanism: Screenshot of an unstyled login page during phased startup.
- Why it was not detected earlier: The previous interrupted-Bootstrap browser probe
  checked login, provider visibility and saving, without asserting stylesheet
  responses or the geometry of the authentication layout.

## Causal analysis

### Faulty approach

The AI-assisted change maintained a manual startup asset allowlist but treated the
`/auth` route segment as sufficient for authentication resources. ASP.NET Core's
`StartsWithSegments` correctly excludes the separate filename `/auth.css`.

### Contributing assumptions

A working form and loaded shared styles were treated as evidence of complete
visual rendering. They did not establish that the page-specific stylesheet loaded.

### Missed signals

`Pages/Shared/_AuthenticationLayout.cshtml` explicitly links `/auth.css` with a
version query. Both root files already existed in `wwwroot`. Their paths needed
to be checked against the startup fence, alongside the Razor and API routes.

### Safeguard gap

Static theme tests inspected CSS source text. Normal browser journeys ran after
readiness, and the pending-startup API test did not request authentication assets.
None tested actual stylesheet delivery through the active fence.

## Resolution

Permit the exact root authentication stylesheet and form-helper paths through the
startup fence. Retain endpoint authentication and the execution-route fence.

## Prevention

HTTP regression tests render the real authentication layout during pending and
failed Bootstrap, follow its stylesheet links, and require successful CSS
responses. They also check the versioned stylesheet, the root form helper and
continued rejection of execution paths, including paths ending in `auth.css`.
Browser smoke additionally checks the logo/card dimensions and CSS responses on
desktop and mobile while readiness is false.

## Validation

Pending targeted HTTP, browser and repository checks. No completed results are
claimed at this stage.
