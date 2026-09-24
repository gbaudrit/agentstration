# Resource authoring names

Browser authoring surfaces that expose both a human-readable label and a stable resource identifier use the shared `ResourceIdentityFields` component and `ResourceNameDerivation` policies.

## Interaction contract

- Creation presents **Display name** before **Technical name** and focuses Display name.
- Technical name follows Display name only while the field remains in automatic mode.
- Any direct technical-name edit, including clearing the field, ends automatic mode.
- A technical name supplied before the component is initialized is an explicit prefill and is never regenerated.
- Edition presents the persisted technical name read-only. Changing Display name never changes resource identity.
- Server-side validation and conflict handling remain authoritative.

The initial server-rendered bootstrap applies the same interaction to Tenant and Workspace through `resource-name-fields.js`, using their 64-character lowercase kebab policy.

## Policies

| Policy | Used by | Derived form | Maximum |
| --- | --- | --- | ---: |
| Generic | Agents, model resources, runtime profiles, secrets, vaults, extensions, Tool resources and providers | lowercase ASCII; `-` replaces invalid runs; `.` and `_` remain when valid | 128 |
| Flow | Flows | lowercase ASCII letters and digits with `-` or `_` | 128 |
| Entry | Entries | lowercase ASCII letters and digits with `-` or `_` | 128 |
| Dashboard | Workplace Dashboards | lowercase ASCII letters and digits with `-` or `_` | 128 |
| Parameter | Parameters | lowercase ASCII letters and digits with `-` or `.` | 128 |
| Trigger | Triggers | lowercase kebab form | 63 |
| Source Registry | Source Registry registrations | portable lowercase kebab form | 128 |
| Tenant / Workspace | initial topology and Workspace creation | lowercase kebab form | 64 |
| Pack | Pack creation and fork | lowercase kebab form | 60 |

All policies normalize Unicode, remove combining diacritics when that yields a stable Latin representation, collapse invalid runs, trim separators, and truncate without leaving a trailing separator. An input with no portable result stays empty so the existing validation can explain the failure.

## Intentionally unchanged one-field surfaces

- Consumer views, lists, pickers, member/profile forms, and presentation-only Workplace surfaces remain display-only.
- Discovered Source and Tool inventory keeps provider-owned technical identifiers; those screens do not author a managed resource identity.
- Entry form fields keep their existing `Name` and `Label` behavior because they are nested input-schema fields, not managed resources.
- YAML, Bootstrap profile documents, imports, Pack manifests, and externally supplied clone/fork identifiers remain explicit inputs and are never regenerated.
- Usernames, model names discovered from providers, secret keys, contribution IDs, namespaces, and reference names are separate domain concepts and do not use resource display-name derivation.
