# ADR-0073 — UI localization uses RESX and Principal culture preferences

- Status: Accepted
- Date: 2026-08-28

## Context

The Console, authentication pages, shared components, and standalone Workplace currently render English text directly from Razor components. Principal preferences already persist appearance independently from ASP.NET Core Identity credentials, while date and trigger time-zone handling are separate concerns. Agentstration also needs a future path for translating user-authored Entry and Dashboard presentation without mixing those translations into product UI resources.

## Decision

Agentstration localizes product-owned UI text through the .NET resource system and `IStringLocalizer`. English (`en-US`) is the neutral source and fallback culture; French (`fr-FR`) is the first additional culture. Supported and default cultures are configured identically in the authoritative server and standalone Workplace.

Request culture is selected by the standard localization providers. A bounded local endpoint writes or removes the culture cookie and performs only local redirects. The rendered HTML language follows `CurrentUICulture`. An authenticated Principal persists an optional BCP 47 language preference alongside the theme; `null` means automatic browser selection. Changing the preference synchronizes the cookie and starts a new server-side Blazor circuit.

Culture, UI culture, and time zone remain separate. API property names, resource identities, enum values, error codes, persisted timestamps, and protocol formats remain invariant. UI clients localize stable presentation text rather than making decisions from translated messages.

Resource-authored translations are not stored in RESX. A later ADR and executable vertical will define immutable localization sidecars for the user-facing fields of Entry and Dashboard resources. The source manifest will retain its default locale and source text; Packs will carry source manifests and sidecars together.

## Consequences

- Existing Principal preference JSON remains readable because the language property is optional; no relational migration is required.
- Console and Workplace share culture configuration and cookie behavior without introducing a new service boundary.
- UI migration can proceed feature by feature, beginning with Profile settings.
- A language is not considered supported until both hosts configure it and required resource sets pass completeness tests.
- Translating manifests, agent output, durable notifications, and historical records remains outside this first increment.
