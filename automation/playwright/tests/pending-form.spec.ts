import { Checkpoints } from '../src/contracts/checkpoints.js';
import { test } from '../src/fixtures/test.js';
import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { PendingFormPage } from '../src/pages/pending-form.page.js';
import { ProductPages } from '../src/pages/product.pages.js';

test.use({ authenticationMode: 'Local', organizationStructureFixture: true, formPendingFixture: true });
test.skip(Boolean(process.env.AGENTSTRATION_CONSOLE_URL), 'Requires the isolated pending-input fixture.');
for (const locale of ['en-US', 'fr-FR'] as const) for (const theme of ['light', 'dark'] as const) for (const mobile of [false, true]) {
  const dimension = mobile ? '@responsive' : locale === 'fr-FR' ? '@locale' : '';
  test(`pending response clearing ${locale} ${theme} ${dimension} @smoke`, async ({ page, product }, testInfo) => {
    const pages = new ProductPages(page);
    await authenticateConsole({ ...product, pages, checkpoint: ignoreCheckpoints }, { username: 'admin', password: 'Fixture973!Admin' });
    await pages.sharedForms.configure(product.consoleUrl, locale, theme);
    if (!product.formPendingInteractionId) throw new Error('The fixture did not expose its pending input.');
    await new PendingFormPage(page).exercise(product.consoleUrl, product.workplaceUrl, product.formPendingInteractionId, locale);
    await testInfo.attach(Checkpoints.sharedForms.dynamic, { body: await page.screenshot(), contentType: 'image/png' });
  });
}
