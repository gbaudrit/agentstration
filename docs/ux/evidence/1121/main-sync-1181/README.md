# Main synchronization: real Storage and Connected Service captures

Product source: `2c6a1adfcbdef5a64c8c28d48d9b992f799e8547`, including main
`8f1645446d7ae9082ed2d00bd28cce98ebaa023a` (#1181). These are unedited PNGs
captured from the running application, using fresh local Development hosts and
public disposable fixture data. No remote storage, service or LLM operation is
required. The authored fixture types and resources exist only in browser-test data.

The automation page/spec files were exercised in the working tree and subsequently committed as `5c5159d8`; their hashes in observations.json identify that harness separately from the product source above.

The populated regression extends #1122's collection contract on `/storages`,
`/storage-types` and `/connected-services`. It checks two same-named Storages in
different namespaces, long display/qualified identities, exact namespace/scope
navigation, complete identity copy without navigation, the small I marker,
independent state, absence of a lock on an unmanaged resource in its owning scope,
keyboard and real touch disclosure, Escape/focus return, unchanged published type
version, editor clear/focus behavior and canceled deactivation confirmations.
An unknown namespace must return 404 rather than another homonymous Storage.
Both list and visited detail/editor surfaces assert zero horizontal document overflow.
The embedded Storage type catalog retains native disclosure: copy leaves the
summary closed and the URL unchanged, Enter opens/closes it, and the resource name
opens the exact type. The type detail also verifies the two homonymous Storage
usages retain distinct qualified identities, owner badges, exact destinations and
complete copies.
These selected checks do not constitute exhaustive populated evidence for every
new embedded surface; the adoption and coverage matrices retain that distinction.

The two maintained variants use EN/dark/1440×1000 desktop and
FR/light/390×844 mobile with `hasTouch: true`. The browser is system Chromium
154.0.8037.57 with Playwright 1.63.0. UI assemblies were built before the campaign
and remained unchanged while the hosts ran. The two cases pass with zero failures,
skips or flaky cases. Assertions, health checks, retries and timeouts remain intact.
The page is returned to its top before each full-page capture; no pixels are edited.
[Observations and raw-image/source checksums](observations.json) record the final
run statistics and the SHA-256 of its original local JSON report.

| Surface | EN desktop, dark | FR mobile, light |
| --- | --- | --- |
| Storages list | [Capture](en-US-dark-desktop-compact-storages-list.png) | [Capture](fr-FR-light-mobile-compact-storages-list.png) |
| Storage definition | [Capture](en-US-dark-desktop-compact-storages-details.png) | [Capture](fr-FR-light-mobile-compact-storages-details.png) |
| Storage Types list | [Capture](en-US-dark-desktop-compact-types-list.png) | [Capture](fr-FR-light-mobile-compact-types-list.png) |
| Storage Type detail and usages | [Capture](en-US-dark-desktop-compact-types-details.png) | [Capture](fr-FR-light-mobile-compact-types-details.png) |
| Connected Services list | [Capture](en-US-dark-desktop-compact-services-list.png) | [Capture](fr-FR-light-mobile-compact-services-list.png) |
| Connected Service definition | [Capture](en-US-dark-desktop-compact-services-details.png) | [Capture](fr-FR-light-mobile-compact-services-details.png) |

## Failures retained and corrected

The populated cases first exposed a strict test locator matching both scope and
state tooltips. The locator now selects the scope panel explicitly; the required
visibility assertion remains in place.

They then exposed two real layout defects in the local merged build before
`6958cd84`: a long Storage name expanded the Storage Type usage grid by 289 px on
desktop, and a pinned Connected Service type/hash expanded the definition by 226 px
on mobile. The first image is retained [here](failures/storage-type-long-usage-desktop-before.png),
and the second [here](failures/connected-service-long-type-mobile-before.png).
The shared resource-link grid now allows its items to shrink before ellipsis;
the service editor allows its grid children to shrink and wraps complete technical
values. The corrections preserve content rather than clipping document overflow.
The same assertions pass after correction. These two earlier images came from the
local merged working tree before the final source commit; they are failure evidence,
not screenshots of the final commit.

A final mobile regression on `fedaac85` found the newly composed native Storage
catalog summary expanding its implicit parent grid by 257 px. The original
[mobile failure capture](failures/embedded-storage-catalog-mobile-before.png) is
retained. The Storage page now gives the catalog grid a `minmax(0, 1fr)` track and
allows its native details/summary items to shrink. The original zero-overflow
assertion remains unchanged; no horizontal document content is hidden.

The harness also waits for the exact Storage Type URL after activating its link;
reading the URL immediately after a Blazor navigation had produced one premature
desktop failure. The expected path, namespace and owner assertions are retained,
with the existing expectation timeout. On failure, a bounded geometry attachment
records up to 24 overflowing elements without user content text.

The final source also retains newly added ToolDefinition publication fields in the
existing unsaved-navigation guard. Two component cases separately edit the version
or publish flag, verify blocked navigation and cancellation, then revert the edit
and verify navigation succeeds. All 710 Console component cases pass after this
correction. This guard change has no additional visual fixture resource.
