# FR #1121 — implementation evidence

The gallery below is a **historical first-wave checkpoint**. The approved compact all-list rollout supersedes its scope glyphs, draft-version presentation and adoption boundary. Use the [current compact evidence](compact-all/README.md) for the delivered rendering and verification provenance.

Real Console screenshots from an isolated local Development instance, SQLite and the existing offline browser fixtures. Desktop: 1920 × 1080, English. Mobile: 390 × 844, French. Both themes use the application's actual persisted preferences. No mocked DOM or image editing is used.

| Collection | Desktop dark | Desktop light | Mobile dark | Mobile light |
| --- | --- | --- | --- | --- |
| Agents | [Table](agents-desktop-en-dark.png) | [Table](agents-desktop-en-light.png) | [Cards](agents-mobile-fr-dark.png) | [Cards](agents-mobile-fr-light.png) |
| Model Profiles | [Table](modelprofiles-desktop-en-dark.png) | [Table](modelprofiles-desktop-en-light.png) | [Cards](modelprofiles-mobile-fr-dark.png) | [Cards](modelprofiles-mobile-fr-light.png) |
| Data Source Profiles | [Table](data-source-profiles-desktop-en-dark.png) | [Table](data-source-profiles-desktop-en-light.png) | [Cards](data-source-profiles-mobile-fr-dark.png) | [Cards](data-source-profiles-mobile-fr-light.png) |
| Runtime Profiles | [Table](runtimeprofiles-desktop-en-dark.png) | [Table](runtimeprofiles-desktop-en-light.png) | [Cards](runtimeprofiles-mobile-fr-dark.png) | [Cards](runtimeprofiles-mobile-fr-light.png) |
| Flow Definitions | [Table](flows-desktop-en-dark.png) | [Table](flows-desktop-en-light.png) | [Cards](flows-mobile-fr-dark.png) | [Cards](flows-mobile-fr-light.png) |

Additional interaction captures:

- [Actions opened with keyboard focus](agents-actions-keyboard.png).
- [Long tenant name, desktop full-value disclosure](long-tenant-desktop-focus.png).
- [Long tenant name, mobile tap disclosure](long-tenant-mobile-tap.png).

The [implementation smoke results](smoke-results.txt) record checks for full clipboard values, final Actions disclosures, arrow/Home/End navigation, Escape/focus restoration, exact namespace/scope/context navigation for homonyms, long tenant names and absence of horizontal document overflow. These checks exercised the actual application in all four viewport/theme combinations above. Full values remain present in accessible DOM text while names truncate visually.

This is implementation evidence, not maintainer UX acceptance and not the reusable scenario tracked by [#1122](https://github.com/gbaudrit/agentstration/issues/1122). The maintainer subsequently authorized preparing the child coverage in a stacked draft before merge. Implemented-UX acceptance and both merges remain pending. The full .NET, TypeScript, existing browser and CI results are recorded in that PR.

The maintainer's version/action refinements supersede the original column/menu presentation: [36 follow-up captures and results](versions-and-actions/README.md).
