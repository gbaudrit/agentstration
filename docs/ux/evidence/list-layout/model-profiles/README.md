# Model Profiles list composition

Real deterministic Development product-host fixture with isolated SQLite, an AEP Ollama contribution and local fake model catalog. Twenty-five available Model Profiles were created through the API in Instance scope and `list-layout` namespace, in addition to the fixture's pre-existing reasoning profile. No model generation, paid provider or real user content is used. API metadata returns `available`, rather than `Ready`; the smoke was adjusted to use that actual status.

[Desktop English, 1440×1000](desktop-en.png), [mobile French controls, 390×844](mobile-fr.png), [mobile result card](mobile-fr-results.png), [confirmation dialog](delete-confirm-desktop-en.png), [source proof](source-proof.json).

Both browser checks pass: full-snapshot search, 20 + 5 filtered pages, status/provider/model/known-usage/namespace/scope filters, no-match reset, retained refresh/view, exact qualified links, useful Delete-only menus and no document horizontal overflow. A separate public disposable profile check verifies that cancellation retains the target, confirmation removes only the exact namespaced Instance profile, its API returns 404, and the remaining list reloads. The fixture's unrelated profile is retained.

Ten new targeted cases plus two existing EN/FR status/tone regressions pass. Cases verify one unfiltered catalog read without a provider-catalog or per-row query; filtering/paging/card retention; refresh failure/shrink and context replacement; separate read/create/delete permissions with managed/inherited protection; scoped latest-ETag confirmation; pre-existing and racing conflict usages with exact consumer links; and late usage/snapshot rejection after a context change. Successful confirmed deletion removes the known target from the retained snapshot before refresh. Operation errors use localized feedback and keep private transport messages out of the UI.

Web/Workplace Release builds have no warnings; full targeted format and two catalog assertions pass. The known incoming TransformationFunctionEditor inventory omission was not redundantly rerun. Full functional lanes and new reusable #1242 journey remain deferred.
