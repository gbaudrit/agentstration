# Shared StepProgress component smoke evidence

Captured on 2026-11-20 using an isolated temporary Razor host and system Chromium.
The harness uses synthetic data-processing steps and a second independent
operation. It creates no product route, Bootstrap API or provider integration.

- [French desktop, 1440px](desktop-fr.png)
- [English component labels on mobile, 390px](mobile-en.png)
- [French desktop, dark theme and reduced motion](desktop-fr-dark.png)
- [Observed assertions](results.json)

The caller-owned demonstration titles/content are French fixture data, including
on the English-label capture. The component-owned headings, statuses and follow
controls use the requested culture. Light/dark styles are supplied through the
normal theme variables; the running animation stops under reduced motion.

The executable smoke verified selected/active independence, automatic-follow
pause/resume and bound values, retained draft/focus under observation refresh,
caller-owned actions, keyboard navigation with disabled-step skipping and no
native scroll, normal Tab behavior, independent instances, empty-list restoration,
stacked mobile cards and no horizontal page overflow. No browser page errors
were observed.

These captures are implementation evidence for the shared primitive. They do
not validate the future Bootstrap screen UX or satisfy the deferred reusable
browser journey in #1251. No new cataloged Playwright scenario was added.
