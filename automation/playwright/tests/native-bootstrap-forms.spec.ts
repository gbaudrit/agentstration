import { Checkpoints } from '../src/contracts/checkpoints.js';
import { test } from '../src/fixtures/test.js';
import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { NativeFormsPage } from '../src/pages/native-forms.page.js';
import { ProductPages } from '../src/pages/product.pages.js';

test.use({ authenticationMode: 'Local', initialBootstrapEnabled: false });
test.skip(Boolean(process.env.AGENTSTRATION_CONSOLE_URL), 'Requires an isolated uninitialized fixture.');
for (const locale of ['en-US', 'fr-FR'] as const) for (const theme of ['light', 'dark'] as const) for (const mobile of [false, true]) {
  const dimension = mobile ? '@responsive' : locale === 'fr-FR' ? '@locale' : '';
  test(`bootstrap fields ${locale} ${theme} ${dimension} @smoke`, async ({ page, product }, testInfo) => {
    const forms = new NativeFormsPage(page);
    await forms.inspectBootstrap(product.consoleUrl, locale, theme);
    await testInfo.attach(Checkpoints.sharedForms.nativeBootstrap, { body: await forms.target().screenshot(), contentType: 'image/png' });
  });
}
test('native bootstrap retains model binding, generated names and real POST @smoke', async ({ page, product }) => {
  await new NativeFormsPage(page).initialize(product.consoleUrl);
});
