import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import type { FormAppearance, SharedFormCase } from '../contracts/shared-form-cases.js';
import { SharedFormTextByLocale, type SupportedTestLocale } from '../locales/expected-text.js';

/** Shared assertions exercised against production form routes, never a demo page. */
export class SharedFormsPage {
  public constructor(private readonly page: Page) {}

  public async configure(consoleUrl: string, locale: SupportedTestLocale, theme: FormAppearance): Promise<void> {
    const response = await this.page.request.put(`${consoleUrl}/api/identity/preferences`, { data: { theme, language: locale } });
    expect(response.ok(), 'The fixture user can update their own appearance.').toBe(true);
    const culture = await this.page.request.get(`${consoleUrl}/_culture?culture=${locale}&returnUrl=%2F`);
    expect(culture.ok()).toBe(true);
  }

  public async openEditor(consoleUrl: string, definition: SharedFormCase, locale: SupportedTestLocale, theme: FormAppearance): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}${definition.path}`, { waitUntil: 'domcontentloaded' });
    expect(response?.ok()).toBe(true);
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-preferences-ready', 'true');
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-theme', theme);
    await expect(this.page.locator('html')).toHaveAttribute('lang', locale);
    await expect(this.editableText().first()).toBeVisible();
  }

  public async assertAssociations(): Promise<void> {
    const failures = await this.page.locator('.workspace .form-field').evaluateAll(fields => fields.flatMap(field => {
      const controls = [...field.querySelectorAll<HTMLInputElement>('input, textarea, select')].filter(control => control.type !== 'hidden');
      return controls.flatMap(control => {
        const problems: string[] = [];
        if (!control.id || ![...field.querySelectorAll('label')].some(label => label.htmlFor === control.id)) problems.push(`Missing associated label: ${control.id}`);
        for (const id of (control.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean)) {
          if (!document.getElementById(id)) problems.push(`Missing field description: ${id}`);
        }
        return problems;
      });
    }));
    expect(failures).toEqual([]);
    await expect(this.page.locator('.workspace .form-field label button')).toHaveCount(0);
  }

  public async exerciseEditorClearing(locale: SupportedTestLocale): Promise<void> {
    const field = this.editableText().first();
    const url = this.page.url();
    await field.fill('UX shared draft');
    await this.clearField(field, locale);
    await expect(this.page).toHaveURL(url);
    await this.assertWithinViewport();
  }

  public async assertRequiredRegistrySelection(locale: SupportedTestLocale): Promise<void> {
    const select = this.page.locator('.resource-form select').first();
    const original = await select.inputValue();
    expect(original).not.toBe('');
    await this.clearField(select, locale);
    await expect(this.page.locator('.validation-errors')).toContainText(SharedFormTextByLocale[locale].selectionRequired);
    const url = this.page.url();
    await this.page.locator('.resource-form button[type=submit]').click();
    await expect(this.page).toHaveURL(url);
    await select.selectOption(original);
    await expect(select).toHaveValue(original);
  }

  public async exerciseWorkplacePrompt(workplaceUrl: string, locale: SupportedTestLocale, theme: FormAppearance): Promise<void> {
    await this.page.request.get(`${workplaceUrl}/_culture?culture=${locale}&returnUrl=%2F`);
    await this.page.goto(`${workplaceUrl}/w/default/d/home/start/default/quick-answer`, { waitUntil: 'domcontentloaded' });
    await expect(this.page.getByTestId(TestIds.workplace.shell)).toHaveClass(new RegExp(`theme-${theme}`));
    await expect(this.page.getByTestId(TestIds.workplace.promptForm)).toHaveAttribute('data-interactive', 'true');
    const input = this.page.getByTestId(TestIds.workplace.promptInput);
    await input.fill('A draft that must not start an interaction.');
    await this.clearField(input, locale);
    await expect(this.page.getByTestId(TestIds.workplace.promptSubmit)).toBeDisabled();
    await this.assertWithinViewport();
    if ((this.page.viewportSize()?.width ?? 1440) <= 620) {
      await expect(this.page.locator('.mobile-brand-logo')).toHaveAttribute('src', new RegExp(`lockup-${theme}\\.png$`));
      await expect(this.page.locator('.entry-start-header h1')).toBeVisible();
      const colors = await this.page.locator('.entry-start-header h1').evaluate(element => ({
        text: getComputedStyle(element).color,
        expected: getComputedStyle(element.closest('.app-shell')!).getPropertyValue('--text').trim(),
      }));
      if (theme === 'light') expect(colors.text).not.toBe('rgb(247, 249, 255)');
    }
  }

  public async assertWithinViewport(): Promise<void> {
    expect(await this.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  }

  public target(): Locator { return this.page.locator('main').first(); }

  private editableText(): Locator {
    return this.page.locator('.workspace .form-field .clearable-field input:not([readonly]):not([disabled]):not([type=number]), .workspace .form-field .clearable-field textarea:not([readonly]):not([disabled])');
  }

  public async clearField(field: Locator, locale: SupportedTestLocale): Promise<void> {
    const clear = field.locator('..').getByRole('button');
    await expect(clear).toHaveAttribute('type', 'button');
    await expect(clear).toHaveAccessibleName(new RegExp(`^${SharedFormTextByLocale[locale].clear}`));
    await clear.click();
    await expect(field).toHaveValue('');
    await expect(field).toBeFocused();
  }
}
