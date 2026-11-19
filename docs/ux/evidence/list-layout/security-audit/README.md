# Security audit list composition

Real deterministic Development Web executable and disposable local SQLite audit data from the existing fixture. No account or role was changed to prepare this capture. Authentication and access checks may add normal audit events during the smoke. The actual API and UI each returned the latest 200 events in both locale/viewport checks.

[Desktop English table, 1440×1000](desktop-en.png), [mobile French controls, 390×844](mobile-fr.png), [mobile French results](mobile-fr-results.png), [source proof](source-proof.json).

Both checks pass: explicit latest-200 help, no unsupported search/filter/pagination/global total, existing Organization navigation, complete copyable IDs, retained refresh/cards and no document horizontal overflow. Captures inspected. The calendar date remains a single readable unit on desktop; mobile can wrap date and time at their natural boundary. Scope is audit-event context, not resource ownership. Actual action/reason codes remain visible; missing action labels and future outcomes use localized neutral fallbacks instead of untranslated resource keys.

Five new collection cases plus the existing Organization localization regression pass: EN/FR, unchanged one-request limit, no invented resource/read gate around the authoritative PlatformAdmin API, full copyable actor/target/correlation, compatible refresh failure/retry, cache clearing before reauthorization on every role/context snapshot notification, late-response rejection and API-denied clearing. Browser evidence uses the administrator fixture; denial and privilege-notification behavior are covered by the component cases.

Warning-free Release Web build, full targeted format verification, symmetric duplicate-free EN/FR catalogs and two collection inventory assertions pass. The known incoming TransformationFunctionEditor inventory omission was not redundantly rerun. Full functional lanes and new reusable #1242 journey remain deferred.
