import { Checkpoints } from '../src/contracts/checkpoints.js';
import { test } from '../src/fixtures/test.js';
import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { NativeFormsPage } from '../src/pages/native-forms.page.js';
import { ProductPages } from '../src/pages/product.pages.js';

test.use({ authenticationMode: 'Local' });
for (const locale of ['en-US', 'fr-FR'] as const) for (const theme of ['light', 'dark'] as const) for (const mobile of [false, true]) {
  const dimension = mobile ? '@responsive' : locale === 'fr-FR' ? '@locale' : '';
  test(`account fields ${locale} ${theme} ${dimension} @smoke`, async ({ page, product }, testInfo) => {
    await authenticateConsole({ ...product, pages: new ProductPages(page), checkpoint: ignoreCheckpoints }, {});
    const forms = new NativeFormsPage(page);
    await forms.inspectAccount(product.consoleUrl, locale, theme);
    await testInfo.attach(Checkpoints.sharedForms.nativeAccount, { body: await forms.target().screenshot(), contentType: 'image/png' });
  });
}
for (const locale of ['en-US', 'fr-FR'] as const) {
  test(`rejected native login keeps passwords write-only ${locale} ${locale === 'fr-FR' ? '@locale' : ''} @smoke`, async ({ page, product }) => {
    await new NativeFormsPage(page).inspectRejectedLogin(product.consoleUrl, locale);
  });
}
test('native login remains operable without JavaScript @smoke', async ({ page, product }) => {
  await new NativeFormsPage(page).assertNoScriptFallback(product.consoleUrl);
});
