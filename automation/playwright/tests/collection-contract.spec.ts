import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { inspectCollections } from '../src/journeys/inspect-collections.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });

// Independent cases: one failing family does not suppress the other collection checks.
const routes = [
  '/agents', '/modelprofiles', '/runtimeprofiles', '/data-source-profiles', '/flows',
  '/entries', '/modelproviders', '/parameters', '/secrets', '/vaults', '/schemas',
  '/tools', '/tools/providers', '/tools/definitions', '/tools/sets', '/tools/categories',
  '/triggers', '/data-sources', '/knowledge-sources', '/sourceproviders',
  '/settings/source-registries', '/settings/source-registries/discovery', '/settings/sources',
  '/packs', '/extensions?tab=catalog', '/extensions?tab=enrollments',
  '/agent-runs', '/agent-instances', '/flow-runs', '/run-events', '/tasks', '/conversations',
  '/artifacts', '/resource-plans', '/cleanup', '/settings/bootstrap', '/settings/resource-scopes',
  '/settings/runtime-workers', '/settings/organization/access', '/settings/organization/members',
  '/settings/organization/security-audit', '/settings/organization/structure', '/settings', '/account/pat',
] as const;

for (const variant of [
  { name: 'desktop English dark', locale: 'en-US', theme: 'dark', width: 1440, height: 1000, tag: '' },
  { name: 'mobile French light', locale: 'fr-FR', theme: 'light', width: 390, height: 844, tag: '@responsive' },
] as const) {
  test.describe(variant.name, () => {
    test.use({ locale: variant.locale, colorScheme: variant.theme, viewport: { width: variant.width, height: variant.height } });
    for (const route of routes) {
      test(`collection contract ${route} ${variant.tag} @collections @smoke`, async ({ page, product, context }) => {
        const pages = new ProductPages(page);
        const journeyContext = { ...product, pages, checkpoint: ignoreCheckpoints };
        await authenticateConsole(journeyContext, {});
        await pages.consoleAdministration.restoreLanguage(product.consoleUrl, variant.locale);
        await pages.ensureTheme(variant.theme);
        await context.grantPermissions(['clipboard-read', 'clipboard-write']);
        await inspectCollections(journeyContext, { route, locale: variant.locale });
      });
    }
  });
}
