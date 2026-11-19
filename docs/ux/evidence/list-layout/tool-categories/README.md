# Tool Categories list composition

Fresh ad hoc checks against the real deterministic `Agentstration.Web` executable and isolated SQLite. The normal development Bootstrap supplied the Workspace and built-in categories; 25 additional ordinary categories were created through the API in that Workspace, across `team` and `other` namespaces. No reusable #1242 journey is implemented.

- [Desktop, English, 1440×1000](desktop-en.png), table view.
- [Mobile, French, 390×844](mobile-fr.png), automatic cards and touch controls.
- [Source proof](source-proof.json): repository base plus exact collection adapter and shared template blobs used for the captures.

Both browser checks passed: search applied to the whole catalog; page two has five matching rows; exact member-count filtering resets to page one; namespace filtering finds all 13 matching fixture categories; no-match reset restores results; refresh retains the view; five legitimate family destinations remain; no redundant row Actions or document horizontal overflow. Screenshots show the reset, refreshed catalog. System Chromium was used through the installed project Playwright because the environment already provides that browser.

Five targeted EN/FR component cases pass, covering exact scoped links, filtering/paging, retained cards through no matches, failed refresh and retry, snapshot shrink, stale/context-replaced responses, denied access and permission-gated creation. The Release Web build passes with zero warnings/errors. Three existing collection-inventory assertions pass. Full functional lanes and complete browser journeys were not rerun for this screen; scenario #1242 remains blocked pending UX validation and merge.
