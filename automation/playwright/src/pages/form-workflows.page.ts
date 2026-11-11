import { expect, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import type { SupportedTestLocale } from '../locales/expected-text.js';
import { SharedFormsPage } from './shared-forms.page.js';

export class FormWorkflowsPage {
  private readonly fields: SharedFormsPage;
  public constructor(private readonly page: Page) { this.fields = new SharedFormsPage(page); }

  public async exerciseTransitionPriority(base: string, name: string, locale: SupportedTestLocale): Promise<void> {
    const priority = this.page.getByTestId(TestIds.flowObservability.transitionPriority);
    await priority.fill('7');
    await priority.press('Tab');
    const read = async () => {
      const response = await this.page.request.get(`${base}/api/flows/${encodeURIComponent(name)}/draft`);
      expect(response.ok()).toBe(true);
      return (await response.json()).value.definition.transitions as { id: string; priority?: number | null }[];
    };
    await expect.poll(async () => (await read()).find(value => value.id === 'input-completed-completed')?.priority).toBe(7);
    const before = await read();
    await priority.fill('42');
    await this.fields.clearField(priority, locale);
    await expect.poll(async () => (await read()).find(value => value.id === 'input-completed-completed')?.priority ?? null).toBeNull();
    const after = await read();
    expect(after.map(value => value.id)).toEqual(before.map(value => value.id));
    await this.fields.assertWithinViewport();
  }

  public async exerciseSourceScheduling(base: string, publisher: string, name: string, locale: SupportedTestLocale): Promise<void> {
    const scope = new URL(this.page.url()).searchParams.get('scopeRef');
    if (!scope) throw new Error('The imported Source URL must retain its exact owner scope.');
    const path = `${base}/api/sources/console/${publisher}/${name}?scopeRef=${encodeURIComponent(scope)}`;
    const read = async () => {
      const response = await this.page.request.get(path);
      expect(response.ok()).toBe(true);
      return (await response.json()).source.configuration;
    };
    const before = await read();
    await this.page.locator('#source-scheduling-tab').click();
    const form = this.page.locator('#source-tab-panel form');
    const number = form.locator('input[type=number]').first();
    const original = await number.inputValue();
    expect(original).not.toBe('');
    await this.fields.clearField(number, locale);
    await expect(form.locator('.validation-errors')).toHaveText(/.+/);
    await form.locator('button[type=submit]').click();
    await expect(number).toHaveValue('');
    expect(await read()).toEqual(before);
    await number.fill(original);
    await number.press('Tab');
    await expect(form.locator('.validation-errors')).toHaveCount(0);
    await this.fields.assertWithinViewport();
  }

  public async provisionEntry(base: string, name: string): Promise<void> {
    const response = await this.page.request.get(`${base}/api/management/entries/quick-answer`);
    expect(response.ok()).toBe(true);
    const draft = (await response.json()).value;
    const data = { ...draft, id: { value: name, namespace: 'default' }, name, displayName: 'Shared field contract',
      revision: 1, publishedBinding: null,
      // These API enums use their numeric wire representation.
      presentation: { ...draft.presentation, kind: 1, suggestions: [], fields: [
        { name: 'request', type: 1, label: 'Required request', description: 'Required field', required: true, role: 1, defaultValue: 'Initial request', order: 0 },
        { name: 'note', type: 2, label: 'Optional note', description: 'Optional field', defaultValue: 'Initial note', order: 1 },
        { name: 'count', type: 3, label: 'Optional count', description: 'Number field', defaultValue: '7', order: 2 },
        { name: 'date', type: 7, label: 'Optional date', description: 'Date field', defaultValue: '2026-11-10', order: 3 },
        { name: 'choice', type: 5, label: 'Optional choice', description: 'Choice field', defaultValue: 'compact', options: [{ value: 'compact', label: 'Compact' }, { value: 'detailed', label: 'Detailed' }], order: 4 },
        { name: 'enabled', type: 4, label: 'Enabled', description: 'Boolean field', defaultValue: true, order: 5 },
      ] },
    };
    const saved = await this.page.request.put(`${base}/api/management/entries/${name}`, { data });
    expect(saved.ok(), await saved.text()).toBe(true);
    const published = await this.page.request.post(`${base}/api/management/entries/${name}/publish`);
    expect(published.ok(), await published.text()).toBe(true);
  }

  public async exerciseEntry(workplaceUrl: string, name: string, locale: SupportedTestLocale): Promise<void> {
    await this.page.request.get(`${workplaceUrl}/_culture?culture=${locale}&returnUrl=%2F`);
    await this.page.goto(`${workplaceUrl}/w/default/d/home/start/default/${name}`);
    const form = this.page.getByTestId(TestIds.workplace.formForm);
    await expect(form).toHaveAttribute('data-interactive', 'true');
    for (const name of ['request', 'note', 'count', 'date', 'choice']) {
      const field = form.locator(`[data-testid="${TestIds.workplace.formInput}"][data-field-name="${name}"]`);
      const input = field.locator('input, textarea, select');
      await expect(input).not.toHaveValue('');
      await this.fields.clearField(input, locale);
    }
    const checkbox = form.locator(`[data-field-name="enabled"] input`);
    await expect(checkbox).toBeChecked();
    await checkbox.uncheck();
    await expect(checkbox).not.toBeChecked();
    const url = this.page.url();
    await this.page.getByTestId(TestIds.workplace.formSubmit).click();
    await expect(form.getByRole('alert')).toContainText('Required request');
    await expect(this.page).toHaveURL(url);
    await this.fields.assertWithinViewport();
  }
}
