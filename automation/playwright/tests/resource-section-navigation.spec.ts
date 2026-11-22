import { expect, test, type Locator, type Page } from '@playwright/test';
import { selectResourceSection } from '../src/pages/resource-sections.js';

test.use({ actionTimeout: 1_000 });

for (const mobile of [false, true]) {
  for (const state of ['stable', 'remount', 'selected'] as const) {
    test(`resource section automation ${mobile ? 'drawer @responsive' : 'rail'} ${state} @smoke`, async ({ page }) => {
      await page.setContent(`
        <header class="resource-header">
          <button class="resource-navigation-trigger" style="display: ${mobile ? 'block' : 'none'}">Sections</button>
        </header>
        <div class="resource-details-navigation">
          <nav class="resource-navigation" data-navigation-ready="true">
            <div class="resource-navigation-rail" style="display: ${mobile ? 'none' : 'block'}">
              <a data-resource-section="worker" href="#worker">Worker</a>
            </div>
          </nav>
        </div>
        <script>
          const navigation = document.querySelector('.resource-navigation');
          document.querySelector('.resource-navigation-trigger').onclick = () => {
            const drawer = document.createElement('dialog');
            drawer.innerHTML = document.querySelector('.resource-navigation-rail').innerHTML;
            navigation.append(drawer);
            drawer.showModal();
          };
          navigation.onclick = event => {
            if (!event.target.closest('a[data-resource-section]')) return;
            document.querySelector('.resource-navigation-rail a').setAttribute('aria-current', 'page');
            navigation.querySelector('dialog')?.remove();
          };
        </script>
      `);

      if (state === 'selected') {
        await page.evaluate(() => {
          location.hash = 'worker';
          document.querySelector('.resource-navigation-rail a')!.setAttribute('aria-current', 'page');
          // Reconciliation of a direct fragment can close a drawer opened too
          // early. Selecting an already active section must not reopen it.
          document.querySelector('.resource-navigation-trigger')!.addEventListener('click', () => {
            queueMicrotask(() => document.querySelector('dialog')?.remove());
          });
        });
      }

      // Reproduce the CI trace: the rail detaches between the readiness check
      // and layout selection, while the desktop drawer trigger stays hidden.
      const subject = state === 'remount' ? remountBeforeLayoutRead(page) : page;
      await selectResourceSection(subject, 'worker');

      await expect(page).toHaveURL(url => url.hash === '#worker');
      await expect(page.locator('.resource-navigation-rail a')).toHaveAttribute('aria-current', 'page');
      await expect(page.locator('dialog')).toHaveCount(0);
    });
  }
}

function remountBeforeLayoutRead(page: Page): Page {
  let remounted = false;
  const rail = (locator: Locator): Locator => new Proxy(locator, {
    get(target, key) {
      if (key === 'isVisible') return async () => {
        if (!remounted) {
          remounted = true;
          await page.evaluate(() => {
            const navigation = document.querySelector('.resource-navigation')!;
            const header = document.querySelector('.resource-header')!;
            navigation.remove();
            header.remove();
            requestAnimationFrame(() => requestAnimationFrame(() => {
              document.body.prepend(header);
              document.querySelector('.resource-details-navigation')!.append(navigation);
            }));
          });
          return false;
        }
        return await target.isVisible();
      };
      const value = Reflect.get(target, key);
      return typeof value === 'function' ? value.bind(target) : value;
    },
  });
  const navigation = (locator: Locator): Locator => new Proxy(locator, {
    get(target, key) {
      if (key === 'locator') return (...args: Parameters<Locator['locator']>) => {
        const child = target.locator(...args);
        return args[0] === '.resource-navigation-rail' ? rail(child) : child;
      };
      const value = Reflect.get(target, key);
      return typeof value === 'function' ? value.bind(target) : value;
    },
  });
  return new Proxy(page, {
    get(target, key) {
      if (key === 'locator') return (...args: Parameters<Page['locator']>) => {
        const locator = target.locator(...args);
        return args[0] === '.resource-details-navigation .resource-navigation' ? navigation(locator) : locator;
      };
      const value = Reflect.get(target, key);
      return typeof value === 'function' ? value.bind(target) : value;
    },
  });
}
