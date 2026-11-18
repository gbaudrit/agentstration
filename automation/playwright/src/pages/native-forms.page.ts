import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import type { FormAppearance } from '../contracts/shared-form-cases.js';
import type { SupportedTestLocale } from '../locales/expected-text.js';
import { SharedFormsPage } from './shared-forms.page.js';

const administrator = { username: 'forms-admin', password: 'FormsFixture973!Admin' };

export class NativeFormsPage {
  private readonly fields: SharedFormsPage;
  public constructor(private readonly page: Page) { this.fields = new SharedFormsPage(page); }

  public async inspectBootstrap(base: string, locale: SupportedTestLocale, theme: FormAppearance): Promise<void> {
    await this.page.emulateMedia({ colorScheme: theme });
    await this.page.goto(`${base}/_culture?culture=${locale}&returnUrl=%2Fbootstrap`);
    await expect(this.page.getByTestId(TestIds.consoleAdministration.bootstrap)).toBeVisible();
    await expect(this.page.locator('html')).toHaveAttribute('data-theme', theme);
    const inputs = this.page.locator('.auth-form input:not([type=hidden]):not([type=checkbox])');
    await expect(inputs).toHaveCount(9);
    for (const input of await inputs.all()) {
      await input.fill('UX form draft');
      await this.assertAssociated(input);
      await this.fields.clearField(input, locale);
      await expect(this.page).toHaveURL(/\/bootstrap/);
    }
    await this.fields.assertWithinViewport();
  }

  public async initialize(base: string): Promise<void> {
    await this.page.goto(`${base}/bootstrap`);
    for (const [field, value] of Object.entries({
      TenantDisplayName: 'Forms Tenant', WorkspaceDisplayName: 'Forms Workspace',
      DisplayName: 'Forms Administrator', UserName: administrator.username,
      Password: administrator.password, ConfirmPassword: administrator.password,
    })) await this.input(field).fill(value);
    await expect(this.input('TenantName')).toHaveValue('forms-tenant');
    await expect(this.input('WorkspaceName')).toHaveValue('forms-workspace');
    await this.page.locator('.auth-form button[type=submit]').click();
    await this.page.waitForURL(url => !url.pathname.startsWith('/bootstrap'));
    if (new URL(this.page.url()).pathname === '/login') await this.signIn(base, administrator.username, administrator.password);
    await expect(this.page.getByTestId(TestIds.console.shell)).toBeVisible();
  }

  public async inspectAccount(base: string, locale: SupportedTestLocale, theme: FormAppearance): Promise<void> {
    await this.fields.configure(base, locale, theme);
    // The saved preference takes precedence over the opposite system appearance.
    await this.page.emulateMedia({ colorScheme: theme === 'light' ? 'dark' : 'light' });
    await this.page.goto(`${base}/account/security`);
    await expect(this.page.locator('html')).toHaveAttribute('data-theme', theme);
    for (const field of ['CurrentPassword', 'NewPassword', 'ConfirmPassword']) {
      const input = this.input(field);
      await input.fill('Private draft only');
      await this.assertAssociated(input);
      await this.fields.clearField(input, locale);
      await expect(this.page).toHaveURL(/\/account\/security/);
    }
    await this.page.goto(`${base}/account/pat`);
    await expect(this.page.locator('html')).toHaveAttribute('data-theme', theme);
    await this.input('Name').fill('Invalid empty expiry');
    await this.page.getByTestId(TestIds.consoleAdministration.patPermission).first().check();
    const expiry = this.input('ExpiresInDays', 'select');
    await this.assertAssociated(expiry);
    await this.fields.clearField(expiry, locale);
    await this.page.getByTestId(TestIds.consoleAdministration.patCreateForm).evaluate(form => (form as HTMLFormElement).noValidate = true);
    await this.page.getByTestId(TestIds.consoleAdministration.patCreate).click();
    await expect(this.page.locator('.token-secret')).toHaveCount(0);
    await expect(this.page.locator('#Input_ExpiresInDays-error')).toHaveText(/.+/);
    await this.fields.assertWithinViewport();
  }

  public async inspectRejectedLogin(base: string, locale: SupportedTestLocale): Promise<void> {
    await this.page.goto(`${base}/_culture?culture=${locale}&returnUrl=%2Flogin`);
    const user = this.page.getByTestId(TestIds.login.username);
    const password = this.page.getByTestId(TestIds.login.password);
    await user.fill('admin');
    await this.fields.clearField(user, locale);
    await user.fill('admin');
    await password.fill('WrongFixture973!Password');
    await this.page.getByTestId(TestIds.login.submit).click();
    await expect(this.page).toHaveURL(/\/login/);
    await expect(password).toHaveValue('');
    await expect(this.page.locator('.validation-summary')).toHaveText(/.+/);
  }

  public async signIn(base: string, username = 'admin', password = 'admin'): Promise<void> {
    await this.page.goto(`${base}/login`);
    await this.page.getByTestId(TestIds.login.username).fill(username);
    await this.page.getByTestId(TestIds.login.password).fill(password);
    await this.page.getByTestId(TestIds.login.submit).click();
    await this.page.waitForURL(url => !url.pathname.startsWith('/login'));
  }

  public async assertNoScriptFallback(base: string): Promise<void> {
    const context = await this.page.context().browser()!.newContext({ javaScriptEnabled: false });
    try {
      const page = await context.newPage();
      await page.goto(`${base}/login`);
      await expect(page.locator('[data-native-field-clear]').first()).toBeHidden();
      await new NativeFormsPage(page).signIn(base);
      await expect(page.getByTestId(TestIds.console.shell)).toBeVisible();
    } finally { await context.close(); }
  }

  public target(): Locator { return this.page.locator('.auth-card'); }
  private input(name: string, tag = 'input'): Locator { return this.page.locator(`${tag}[name="Input.${name}"]`); }
  private async assertAssociated(input: Locator): Promise<void> {
    const id = await input.getAttribute('id');
    expect(id).toBeTruthy();
    await expect(this.page.locator(`label[for="${id}"]`)).toHaveCount(1);
    const descriptions = (await input.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);
    expect(descriptions.length).toBeGreaterThan(0);
    for (const description of descriptions) await expect(this.page.locator(`[id="${description}"]`)).toHaveCount(1);
  }
}
