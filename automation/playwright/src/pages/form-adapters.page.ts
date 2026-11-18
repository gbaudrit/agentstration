import { expect, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { SharedFormTextByLocale, type SupportedTestLocale } from '../locales/expected-text.js';
import { SharedFormsPage } from './shared-forms.page.js';

export class FormAdaptersPage {
  private readonly fields: SharedFormsPage;
  public constructor(private readonly page: Page) { this.fields = new SharedFormsPage(page); }

  public async exerciseContextualValues(base: string, locale: SupportedTestLocale): Promise<void> {
    for (const [kind, requirement, dialogId, inputId] of [
      ['Parameter', 'projectEndpoint', TestIds.formAdapters.parameterDialog, TestIds.formAdapters.parameterValue],
      ['Secret', 'credential', TestIds.formAdapters.secretDialog, TestIds.formAdapters.secretValue],
    ] as const) {
      const before = await this.count(base, kind === 'Parameter' ? '/api/parameters' : '/api/secrets');
      const requirementPanel = this.page.locator(`[data-testid="${TestIds.formAdapters.requirement}"][data-requirement-id="${requirement}"]`);
      await requirementPanel.getByTestId(TestIds.formAdapters.bindingKind).selectOption(kind);
      const trigger = requirementPanel.getByTestId(TestIds.formAdapters.bindingCreate);
      await trigger.click();
      const dialog = this.page.getByTestId(dialogId);
      await expect(dialog).toBeVisible();
      const input = dialog.getByTestId(inputId);
      await input.fill(kind === 'Secret' ? 'private-fixture-draft' : 'https://example.test/project');
      if (kind === 'Secret') await expect(dialog).not.toContainText('private-fixture-draft');
      await this.fields.clearField(input, locale);
      if (kind === 'Parameter') {
        await dialog.getByTestId(TestIds.formAdapters.parameterCreate).click();
        await expect(dialog).toBeVisible();
        await expect(dialog.locator('.validation-errors')).toHaveText(/.+/);
      }
      expect(await this.count(base, kind === 'Parameter' ? '/api/parameters' : '/api/secrets')).toBe(before);
      await this.page.keyboard.press('Escape');
      await expect(dialog).toHaveCount(0);
      await expect(trigger).toBeFocused();
    }
  }

  public async exerciseAccountAdministration(base: string, locale: SupportedTestLocale): Promise<void> {
    const scope = await this.page.getByTestId(TestIds.console.shell).getAttribute('data-context-scope');
    await this.page.goto(`${base}/settings/organization/members${scope ? `?context=${encodeURIComponent(scope)}` : ''}`);
    const section = this.page.getByTestId(TestIds.consoleAdministration.organizationMembers);
    await expect(section.locator('.form-field input').first()).toBeVisible();
    await this.fields.assertAssociations();
    const before = await this.count(base, '/api/identity/accounts/');
    const username = section.locator('.form-field input').first();
    await username.fill('unsubmitted-fixture-account');
    await this.fields.clearField(username, locale);
    const workspace = section.locator('.form-field select').first();
    let original = await workspace.inputValue();
    if (!original) {
      original = await workspace.locator('option:not([value=""])').first().getAttribute('value') ?? '';
      await workspace.selectOption(original);
    }
    await this.fields.clearField(workspace, locale);
    await expect(section.locator('.validation-errors')).toContainText(SharedFormTextByLocale[locale].selectionRequired);
    await expect(section.locator('.form-actions .button-primary').first()).toBeDisabled();
    expect(await this.count(base, '/api/identity/accounts/')).toBe(before);
    await workspace.selectOption(original);
    await this.fields.assertWithinViewport();
  }

  public async exercisePackClassifications(base: string, locale: SupportedTestLocale): Promise<void> {
    await this.page.goto(`${base}/pack-projects/new`);
    const composer = this.page.getByTestId(TestIds.distribution.packComposer);
    await expect(composer).toHaveAttribute('data-interactive', 'true');
    await this.page.getByTestId(TestIds.distribution.packProjectPublisher).fill('forms');
    await this.page.getByTestId(TestIds.distribution.packProjectPublisher).press('Tab');
    await this.page.getByTestId(TestIds.distribution.packProjectDisplayName).fill('Shared classification contract');
    await composer.locator('[data-resource-kind="Flow"][data-resource-name="system-direct-agent-dotnet-expert"]').getByTestId(TestIds.distribution.packProjectAddResource).click();
    const create = this.page.getByTestId(TestIds.distribution.packProjectCreate);
    await expect(create).toBeEnabled();
    const choice = composer.locator('.composer-metadata select').first();
    const original = await choice.inputValue();
    await this.fields.clearField(choice, locale);
    await expect(create).toBeDisabled();
    await expect(composer.locator('.validation-errors')).toContainText(SharedFormTextByLocale[locale].selectionRequired);
    await choice.selectOption(original);
    await expect(create).toBeEnabled();
    await this.fields.assertWithinViewport();
  }

  public async exerciseRegistryRequiredSelection(base: string, locale: SupportedTestLocale): Promise<void> {
    const before = await this.count(base, '/api/sourceregistries');
    await this.page.goto(`${base}/settings/source-registries/new`);
    await this.page.locator('.resource-form .form-field input').first().fill('Unsubmitted registry');
    const endpoint = this.page.locator('.resource-form input[type=url]');
    await endpoint.fill('https://example.test/index.json');
    await endpoint.press('Tab');
    await this.fields.assertRequiredRegistrySelection(locale);
    expect(await this.count(base, '/api/sourceregistries')).toBe(before);
  }

  public async assertLanguageChoiceIsFixed(base: string, locale: SupportedTestLocale): Promise<void> {
    await this.page.goto(`${base}/settings/profile`);
    const language = this.page.getByTestId(TestIds.consoleAdministration.profileLanguage);
    await expect(language).toHaveValue(locale);
    await expect(language.locator('..').getByRole('button')).toHaveCount(0);
    await expect(language).toHaveAttribute('aria-describedby', 'profile-language-help');
    await this.fields.assertWithinViewport();
  }

  private async count(base: string, path: string): Promise<number> {
    const response = await this.page.request.get(base + path, { headers: { 'X-Agentstration-Context': '/instance' } });
    expect(response.ok()).toBe(true);
    const body = await response.json();
    const values = Array.isArray(body) ? body : body.value;
    expect(Array.isArray(values)).toBe(true);
    return values.length;
  }
}
