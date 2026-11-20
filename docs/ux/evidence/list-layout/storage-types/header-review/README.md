# Storage type list destination label

User-requested follow-up on `/storage-types`: the secondary header link now reads `Storages` / `Stockages`, reusing the existing localized destination label instead of `Back to Storages` / `Retour aux stockages`. Its `/storages` destination, navigation context, permissions and styling are unchanged. This label change is limited to the type list; shared Back wording and protected creation/detail screens are unchanged.

[English desktop](desktop-en.png), [French mobile](mobile-fr.png), [source proof](source-proof.json). Actual deterministic Web/SQLite checks with system Chromium pass at 1440×1000 and 390×844: localized label, navigation to Storages and back to Storage types with the same Instance context, and no document horizontal overflow. Captures inspected. Existing populated metadata fixtures are used; no content operation is invoked.

All six StorageTypeCollectionTests cases pass, including maintained paging/permissions/context behavior and EN/FR navigation-label assertions. Release Web build completes without warnings and full targeted dotnet format verification passes. Full functional lanes and the deferred reusable #1242 journey were not repeated for this label-only follow-up. The broader existing desktop/mobile Connected Storage journeys passed in the immediately preceding Storage list correction.
