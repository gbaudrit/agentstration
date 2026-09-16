# Browser coverage model

Agentstration tracks browser coverage as a product contract rather than an informal list of screenshots. The executable catalog in `automation/playwright/src/coverage/application-surfaces.ts` owns every Console, account, and Workplace route declared with `@page`.

The long-term target has two parts:

1. every routable surface renders from deterministic fixture state in each applicable host;
2. every critical user mutation has a reusable behavioral journey with observable success and expected-failure assertions.

Route aliases belong to one functional surface. They do not inflate the page count, but the suite must still materialize and navigate each alias. Parameterized routes declare the fixture keys needed to construct a real URL. A surface is `covered` only when its page object, every route alias, and its critical behavior are exercised by the owning specification. Existing but incomplete automation is `partial`; work that has not started is `planned`. Both states link to a tracking issue.

## Contributor workflow

When adding, removing, or changing an `@page` directive:

1. update the matching catalog surface in the same change;
2. declare any fixture keys required by route parameters;
3. add or update the page object and route-rendering test;
4. add or update a reusable journey when the page owns a critical mutation;
5. run the catalog contract tests and the affected browser specification.

The catalog test intentionally reads route declarations from source and fails on missing, duplicate, or stale entries. It does not parse implementation details or derive localized assertions from RESX resources.

## Commands

From `automation/playwright`:

```powershell
npm exec tsc -- --noEmit
npx playwright test tests/application-surface-catalog.spec.ts
npm run coverage:report
```

The report separates functional surfaces from route declarations and lists the owning specification or tracking issue. Reaching 100% means that every cataloged surface is marked `covered`; it does not claim exhaustive combinations of all inputs, browsers, permissions, or external providers.
