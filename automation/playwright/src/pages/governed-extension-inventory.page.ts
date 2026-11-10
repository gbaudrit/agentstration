import { expect, type Locator, type Page } from '@playwright/test';
import { fillAndCommit } from './controls.js';
import { TestIds } from '../contracts/test-ids.js';
import { GovernedInventoryTextByLocale, type SupportedTestLocale } from '../locales/expected-text.js';

const namespace = 'qa-inventory';
const query = 'namespace=qa-inventory&scopeRef=%2Finstance&context=%2Finstance';

export class GovernedExtensionInventoryPage {
  public constructor(private readonly page: Page) {}
  public get inventory(): Locator { return this.page.getByTestId(TestIds.governedInventory.inventory); }
  public get managed(): Locator { return this.page.getByTestId(TestIds.governedInventory.managed); }
  public get contributions(): Locator { return this.page.getByTestId(TestIds.governedInventory.contributions); }

  private async navigate(url: string): Promise<void> {
    const response = await this.page.goto(url, { waitUntil: 'networkidle' });
    expect(response?.ok(), `Governed inventory route ${url}`).toBe(true);
    await expect(this.page.locator('[aria-busy="true"]')).toHaveCount(0);
  }

  private assertScopedLink(href: string | null): void {
    expect(href).not.toBeNull();
    const url = new URL(href!, this.page.url());
    expect(url.searchParams.get('scopeRef')).toBe('/instance');
    expect(url.searchParams.get('context')).toBe('/instance');
    expect(url.searchParams.get('namespace') === namespace || url.pathname.includes(`/namespaces/${namespace}/`)).toBe(true);
  }

  public async openFromCatalog(consoleUrl: string, registration = 'qa-managed'): Promise<void> {
    await this.navigate(`${consoleUrl}/extensions?context=%2Finstance`);
    await this.page.locator(`[data-testid="${TestIds.distribution.extensions}"][data-interactive="true"]`).waitFor();
    await this.page.getByTestId(TestIds.distribution.extensionCatalogTab).click();
    const link = this.page.locator(`a[href^='/extensions/${registration}?']`).first();
    this.assertScopedLink(await link.getAttribute('href'));
    await link.click();
    await this.page.getByTestId(TestIds.distribution.extensionDetails).waitFor();
    await expect(this.page).toHaveURL(url => url.pathname === `/extensions/${registration}` && url.searchParams.get('context') === '/instance');
  }

  public async inspectInventory(locale: SupportedTestLocale): Promise<void> {
    const text = GovernedInventoryTextByLocale[locale];
    await this.page.getByRole('tab', { name: text.resources, exact: true }).click();
    const installation = this.inventory.getByTestId(TestIds.governedInventory.installation);
    await expect(installation).toContainText('qa-managed');
    await expect(installation).toContainText(text.installed);
    await expect(installation).toContainText('9.0.0');
    await expect(installation).toContainText('2.4.0');
    await expect(installation.locator('.extension-bundle-meta > span').filter({ hasText: text.bundleVersion })).toContainText('2.4.0');
    await expect(installation.locator('.extension-bundle-meta > span').filter({ hasText: text.extensionVersion })).toContainText('9.0.0');
    const grid = installation.getByTestId(TestIds.governedInventory.resources);
    await expect(grid.locator('tbody > tr, .collection-card')).toHaveCount(3);
    await expect(grid.locator('a')).toHaveCount(3);
    for (const [index, kind] of text.kinds.entries()) {
      const row = grid.locator('tbody > tr, .collection-card').nth(index);
      await expect(row.locator('.extension-bundle-kind')).toHaveText(kind);
      await expect(row.locator('.collection-qualified-name code')).toHaveText(`${namespace}/qa-managed`);
      await expect(row.locator('.resource-scope-badge')).toHaveText(text.scope);
      await expect(row).toContainText('2.4.0');
      await expect(row.locator('.managed-resource-badge')).toHaveText(text.managed);
    }
    for (const href of await grid.locator('a').evaluateAll(nodes => nodes.map(node => node.getAttribute('href')))) this.assertScopedLink(href);
    await expect(grid).toContainText(`${namespace}/qa-managed`);
    const bounds = await this.inventory.boundingBox();
    expect(bounds!.width).toBeLessThanOrEqual(this.page.viewportSize()!.width);
  }

  public async inspectTechnicalDetails(locale: SupportedTestLocale): Promise<void> {
    const text = GovernedInventoryTextByLocale[locale];
    const evidence = this.inventory.getByTestId(TestIds.governedInventory.evidence);
    await expect(evidence).not.toHaveAttribute('open', '');
    await evidence.getByText(text.technical, { exact: true }).click();
    await expect(evidence).toHaveAttribute('open', '');
    await expect(evidence).toContainText('example.governed-inventory');
    await expect(evidence.locator('code').filter({ hasText: /^aep-fe2894091928dc6833342faf$/ })).toHaveCount(1);
    await expect(evidence.locator('code').filter({ hasText: /^fixture\.ready$/ })).toHaveCount(1);
    await expect(evidence.locator('code').filter({ hasText: /^qa-managed\.ready$/ })).toHaveCount(1);
    await expect(evidence.locator('code').filter({ hasText: /^[a-f0-9]{64}$/ })).toHaveCount(1);
    const publications = evidence.getByTestId(TestIds.governedInventory.publications);
    await expect(evidence.getByRole('heading', { name: text.publications, exact: true })).toBeVisible();
    await expect(publications.locator('li')).toHaveCount(3);
    await expect(publications.locator('a')).toHaveCount(0);
    for (const row of await publications.locator('li').all()) {
      await expect(row).toContainText('2.4.0');
      await expect(row).toContainText(namespace);
    }
  }

  private async assertNoMutation(locale: SupportedTestLocale): Promise<void> {
    for (const name of GovernedInventoryTextByLocale[locale].mutations) {
      for (const button of await this.page.getByRole('button', { name, exact: true }).all()) await expect(button).toBeDisabled();
    }
    await expect(this.page.locator('a.flow-header-designer')).toHaveCount(0);
    await expect(this.page.getByRole('link', { name: GovernedInventoryTextByLocale[locale].yamlEditor, exact: true })).toHaveCount(0);
  }

  public async inspectManagedResources(consoleUrl: string, locale: SupportedTestLocale): Promise<void> {
    const hrefs = await this.inventory.getByTestId(TestIds.governedInventory.resources).locator('a').evaluateAll(nodes => nodes.map(node => node.getAttribute('href')!));
    for (const href of hrefs) {
      await this.navigate(new URL(href, consoleUrl).href);
      await expect(this.managed).toContainText(GovernedInventoryTextByLocale[locale].managed);
      await expect(this.managed).toContainText('9.0.0');
      await expect(this.managed).toContainText('2.4.0');
      const owner = this.managed.locator('a');
      this.assertScopedLink(await owner.getAttribute('href'));
      await this.assertNoMutation(locale);
      if (href.includes('/flows/')) {
        const text = GovernedInventoryTextByLocale[locale];
        for (const tab of [text.definition, text.deployments, text.yaml]) {
          await this.page.locator('nav.section-tabs').getByRole('button', { name: tab, exact: true }).click();
          await this.assertNoMutation(locale);
        }
      }
      if (href.includes('/data-source-profiles/')) {
        const text = GovernedInventoryTextByLocale[locale];
        for (const tab of [text.definition, text.revisions, text.yaml]) {
          await this.page.getByRole('tab', { name: tab, exact: true }).click();
          await this.assertNoMutation(locale);
          if (tab !== text.yaml) await expect(this.page.locator('fieldset')).toHaveAttribute('disabled', '');
        }
        await expect(this.page.locator('textarea')).toHaveAttribute('readonly', '');
      }
      await this.navigate(new URL(href, consoleUrl).href);
      await this.managed.locator('a').click();
      await expect(this.page).toHaveURL(url => url.pathname === '/extensions/qa-managed' && url.searchParams.get('namespace') === namespace);
    }
    await this.navigate(`${consoleUrl}/data-source-profiles/qa-managed?${query}`);
    await expect(this.managed).toBeVisible();
    await this.navigate(`${consoleUrl}/data-source-profiles?context=%2Finstance`);
    const profileRow = this.page.locator('tbody tr, .collection-card').filter({ has: this.page.locator("a.resource-name-link[href^='/data-source-profiles/qa-managed?']") });
    await expect(profileRow).toContainText('Governed inventory fixture');
    await expect(profileRow).toContainText('9.0.0');
    await expect(profileRow).toContainText('2.4.0');
    this.assertScopedLink(await profileRow.locator("a[href^='/extensions/qa-managed?']").getAttribute('href'));
    await this.navigate(`${consoleUrl}/data-source-profiles/qa-managed?${query}`);
  }

  public async inspectContributionsAndTools(consoleUrl: string, locale: SupportedTestLocale): Promise<void> {
    const text = GovernedInventoryTextByLocale[locale];
    await this.navigate(`${consoleUrl}/extensions/qa-managed?${query}`);
    await this.page.getByRole('tab', { name: text.contributions, exact: true }).click();
    for (const [id, label, hasLink] of [
      ['ready', text.available, true], ['disabled', text.unavailable, true],
      ['missing', text.missing, false], ['conflict', text.conflict, false],
    ] as const) {
      const card = this.contributions.locator(`[data-contribution-id='fixture.${id}']`);
      await expect(card).toContainText(label);
      await expect(card.locator('.extension-materialized-resources a')).toHaveCount(hasLink ? 1 : 0);
      if (id === 'conflict') await expect(card.locator('a')).toHaveCount(0);
      if (id === 'missing') this.assertScopedLink(await card.locator("a[href^='/tools/providers/qa-managed?']").getAttribute('href'));
    }
    const ready = this.contributions.locator("[data-contribution-id='fixture.ready']");
    await ready.locator('.extension-materialized-resources a').click();
    await expect(this.page.getByTestId(TestIds.toolExecution.enabledToggle)).toBeDisabled();
    const provider = this.page.locator("a[href^='/tools/providers/qa-managed?']");
    this.assertScopedLink(await provider.getAttribute('href'));
    await expect(this.page.locator('main')).toContainText('Governed inventory fixture');
    await provider.click();
    await expect(this.page.locator('fieldset')).toHaveAttribute('disabled', '');
    await this.page.locator("a[href^='/extensions/qa-managed?']").click();
    await this.navigate(`${consoleUrl}/tools?context=%2Finstance`);
    const owners = this.page.locator("a[href^='/extensions/qa-managed?']");
    await expect(async () => {
      await fillAndCommit(this.page.getByRole('search').getByRole('searchbox'), 'qa-managed');
      await expect(owners).toHaveCount(4);
    }).toPass();
    for (const owner of await owners.all()) {
      await expect(owner).toHaveText('Governed inventory fixture');
      this.assertScopedLink(await owner.getAttribute('href'));
    }
    await owners.first().click();
    await this.page.getByRole('tab', { name: text.contributions, exact: true }).click();
  }

  public async inspectOrdinaryResources(consoleUrl: string, locale: SupportedTestLocale): Promise<void> {
    const text = GovernedInventoryTextByLocale[locale];
    await this.navigate(`${consoleUrl}/tools/sets/qa-user?${query}`);
    await expect(this.managed).toHaveCount(0);
    await expect(this.page.getByRole('button', { name: text.delete, exact: true })).toBeEnabled();
    await this.navigate(`${consoleUrl}/namespaces/${namespace}/flows/qa-user?scopeRef=%2Finstance&context=%2Finstance`);
    await expect(this.managed).toHaveCount(0);
    await expect(this.page.locator('a.flow-header-designer')).toBeVisible();
    await this.navigate(`${consoleUrl}/data-source-profiles/qa-user?${query}`);
    await expect(this.managed).toHaveCount(0);
    await expect(this.page.getByRole('button', { name: text.delete, exact: true })).toBeEnabled();
    await this.page.getByRole('tab', { name: text.definition, exact: true }).click();
    await expect(this.page.locator('fieldset')).not.toHaveAttribute('disabled', '');
    await expect(this.page.getByRole('button', { name: text.save, exact: true })).toBeEnabled();
  }

  public async inspectDegraded(consoleUrl: string, locale: SupportedTestLocale): Promise<void> {
    const text = GovernedInventoryTextByLocale[locale];
    await this.openFromCatalog(consoleUrl, 'qa-degraded');
    await this.page.getByRole('tab', { name: text.resources, exact: true }).click();
    await expect(this.inventory).toContainText(text.degraded);
    await expect(this.inventory).toContainText('fixture: recorded definition drift');
    await expect(this.inventory.getByTestId(TestIds.governedInventory.resources).locator('a')).toHaveCount(0);
    await expect(this.inventory.getByTestId(TestIds.governedInventory.publications).locator('li')).toHaveCount(3);
    await this.page.getByRole('tab', { name: text.contributions, exact: true }).click();
    const bundle = this.contributions.locator("[data-contribution-id='qa-degraded']");
    await expect(bundle).toContainText(text.conflict);
    await expect(bundle.locator('a')).toHaveCount(0);
    await this.page.getByRole('tab', { name: text.resources, exact: true }).click();
  }
}
