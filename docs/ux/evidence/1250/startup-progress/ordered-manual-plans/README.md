# Ordered manual Bootstrap plans

Implementation smoke on 2026-11-21 for PR #1260, continuing #871 and the deferred
browser child #1217. Captured from the Release Web executable on the Bootstrap
branch after the `571dc19a` baseline. These are implementation review captures,
not maintainer UX approval or a reusable journey. The #1217 validation/merge gate
remains in force.

## Preparation and preview

[Desktop preparation](prepare-fr-desktop.png) selects three temporary offline
profiles in reverse scope order. The Console displays Instance, Tenant and
Workspace in their actual execution order. Each scoped profile has its own target;
the Tenant and Workspace targets are declared by preceding selected profiles and
do not exist when preview starts. Catalog configuration was opened and closed as
a separate modal.

[Desktop preview](preview-fr-desktop.png) is a separate view without the catalog
or preparation form. It lists the ordered profile/target pairs and five expected
resource outcomes. Semantic validation that needs future targets is explicitly
marked deferred, including the overall warning state. Preview creates no targets.
Back preserves the three checked profiles and their selected targets. An explicit
confirmation submits the frozen selection and digest.

[Mobile preparation](prepare-fr-mobile.png) and [mobile preview](preview-fr-mobile.png)
were checked at 390x844 with no document horizontal overflow. The shared Console
navigation and full available desktop content width remain in use.

## Application

[Desktop application](application-fr-desktop.png) uses the existing StepProgress
inside Bootstrap, with no catalog or preview stacked above it. The real local
application created, in order, one Tenant, its Workspace and Parameter, then a
Flow and Entry inside the new Workspace. The history contains one application and
one request identity. Reload resumes the same application ID. Persisted per-profile
resolved ownership matches the created Workspace; the administrator's consulted
Console Workspace did not change.

Mobile application EN was checked at 390x844 in [light](application-en-mobile-light.png)
and [dark](application-en-mobile-dark.png), and at 320px for horizontal overflow.
No JavaScript errors or document-wide horizontal overflow occurred. Existing
shared mobile navigation/overlay behavior is visible in these captures and was
not redesigned by this change.

## Verification scope

The smoke used an isolated SQLite directory, deterministic AI and the public
local Development administrator fixture. Temporary profiles and scripts remain
outside the repository; no database, credential, manifest body or parameter value
is included in this evidence directory. The profile identifiers shown are test data.

Six new backend scenarios cover read-only preview, scope ordering, future target
creation, producer-relative bindings, per-target isolation, authorization, stale
sources, request replay, cancellation and deferred failure with retained resources.
The binding probe used by those scenarios is test-only; the browser uses actual
resource handlers. Console tests cover independent targets and back/preview state.
See PR #1260 for the final build and functional-lane results and the registered
catalog smoke result. The full repository browser smoke catalog is not certified
by these focused checks.

Exact Source selections and legacy flat same-target requests remain supported.
Plans contain at most 16 distinct local profiles. Persistent preparation drafts,
repeated instances of one profile, mixed Source/local plans and rollback are
outside this implementation. Validation of deferred dependencies completes before
the affected profile executes, so an error can occur after earlier profiles have
created resources.
