import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

export interface ResourceAccessIdentity {
  uid: string;
  generation: string;
  version: string;
  originVersion: string;
}

export class ResourceAccessPage {
  public constructor(private readonly page: Page) {}

  public get panel(): Locator { return this.page.getByTestId(TestIds.resourceAccess.panel); }

  public async openVaultAccess(): Promise<void> {
    await this.page.getByTestId('vault-access-tab').click();
    await this.panel.waitFor({ state: 'visible' });
  }

  public async openSecretAccess(): Promise<void> {
    await this.page.getByTestId('secret-access-tab').click();
    await this.panel.waitFor({ state: 'visible' });
  }

  public async ensureManagedUtilitiesTool(consoleUrl: string): Promise<string> {
    let requestId = '';
    let pairingUri = '';
    let registrationName = '';
    let available = false;
    await expect(async () => {
      const response = await this.page.request.get(`${consoleUrl}/api/aep/enrollments`);
      const body = await response.text();
      expect(response.status(), body).toBe(200);
      const enrollments = JSON.parse(body) as Array<{
        definition?: { instanceId?: string; extensionId?: string; state?: string; pairingUri?: string; registrationName?: string };
      }>;
      const enrollment = enrollments.find(value => value.definition?.extensionId === 'Agentstration.Extensions.Utilities');
      expect(enrollment, 'Utilities AEP enrollment announcement').toBeDefined();
      requestId = enrollment?.definition?.instanceId ?? '';
      pairingUri = enrollment?.definition?.pairingUri ?? '';
      registrationName = enrollment?.definition?.registrationName ?? '';
      available = enrollment?.definition?.state?.toLowerCase() === 'available';
      expect(requestId).not.toBe('');
      expect(pairingUri).not.toBe('');
    }).toPass({ timeout: 30_000 });

    if (!available) {
      const assigned = await this.page.request.post(`${consoleUrl}/api/aep/enrollments/${requestId}/assign`, {
        data: { scopeRef: '/instance' },
      });
      expect(assigned.status(), await assigned.text()).toBe(204);

      const rotated = await this.page.request.post(`${consoleUrl}/api/aep/enrollments/${requestId}/rotate`);
      const rotationBody = await rotated.text();
      expect(rotated.status(), rotationBody).toBe(200);
      const rotation = JSON.parse(rotationBody) as { code?: string };
      expect(rotation.code).toBeTruthy();

      const paired = await this.page.request.post(pairingUri, { form: { code: rotation.code! } });
      expect(paired.status(), await paired.text()).toBe(200);

      await expect(async () => {
        const response = await this.page.request.get(`${consoleUrl}/api/aep/enrollments`);
        const body = await response.text();
        expect(response.status(), body).toBe(200);
        const enrollments = JSON.parse(body) as Array<{
          definition?: { instanceId?: string; state?: string; registrationName?: string };
        }>;
        const enrollment = enrollments.find(value => value.definition?.instanceId === requestId);
        expect(enrollment?.definition?.state?.toLowerCase()).toBe('available');
        registrationName = enrollment?.definition?.registrationName ?? '';
        expect(registrationName).not.toBe('');
      }).toPass({ timeout: 30_000 });
    }

    const toolName = `${registrationName}.hash.compute`;
    await expect(async () => {
      const response = await this.page.request.get(
        `${consoleUrl}/api/tools/${encodeURIComponent(toolName)}?scopeRef=${encodeURIComponent('/instance')}`,
        { headers: { 'X-Agentstration-Context': '/instance' } },
      );
      expect(response.status(), await response.text()).toBe(200);
    }).toPass({ timeout: 30_000 });
    return toolName;
  }

  public async openManagedTool(consoleUrl: string, name: string, scopeRef = '/instance'): Promise<ResourceAccessIdentity> {
    const route = `${consoleUrl}/tools/${encodeURIComponent(name)}?scopeRef=${encodeURIComponent(scopeRef)}`;
    await expect(async () => {
      const response = await this.page.goto(route, { waitUntil: 'domcontentloaded' });
      expect(response?.status(), `Managed Tool route '${route}'`).toBe(200);
      await this.page.getByTestId(TestIds.resourceAdministration.toolDetails).waitFor({ state: 'visible' });
    }).toPass({ timeout: 30_000 });
    await expect(this.page.getByTestId(TestIds.toolExecution.enabledToggle)).toBeDisabled();
    const accessTab = this.page.getByTestId('tool-access-tab');
    await expect(async () => {
      await accessTab.click();
      expect(await accessTab.getAttribute('aria-selected')).toBe('true');
    }).toPass({ timeout: 15_000 });
    await this.panel.waitFor({ state: 'visible' });
    await expect(this.panel).toHaveAttribute('data-resource-managed', 'true');
    await expect(this.panel).toHaveAttribute('data-resource-origin', 'Extension');
    return this.identity();
  }

  public async addGrant(scopeRef: string): Promise<void> {
    await this.page.getByTestId(TestIds.resourceAccess.target).selectOption(scopeRef);
    await this.page.getByTestId(TestIds.resourceAccess.add).click();
    await this.grant(scopeRef).waitFor({ state: 'visible' });
    await this.save();
  }

  public async removeGrant(scopeRef: string): Promise<void> {
    const grant = this.grant(scopeRef);
    await grant.getByTestId(TestIds.resourceAccess.remove).click();
    await grant.waitFor({ state: 'detached' });
    await this.save();
  }

  public grant(scopeRef: string): Locator {
    return this.page.locator(`[data-testid="${TestIds.resourceAccess.grant}"][data-scope-ref="${scopeRef}"]`);
  }

  public async rejectInjectedTarget(scopeRef: string, expectedMessage: string): Promise<void> {
    const target = this.page.getByTestId(TestIds.resourceAccess.target);
    await target.evaluate((element, value) => {
      const option = document.createElement('option');
      option.value = value;
      option.textContent = value;
      element.append(option);
    }, scopeRef);
    await target.selectOption(scopeRef);
    await this.page.getByTestId(TestIds.resourceAccess.add).click();
    await expect(this.page.getByTestId(TestIds.resourceAccess.invalidTarget)).toHaveText(expectedMessage);
    await expect(this.grant(scopeRef)).toHaveCount(0);
  }

  public async identity(): Promise<ResourceAccessIdentity> {
    await expect(this.panel).toHaveAttribute('data-resource-uid', /.+/);
    const attributes = await Promise.all([
      this.panel.getAttribute('data-resource-uid'),
      this.panel.getAttribute('data-resource-generation'),
      this.panel.getAttribute('data-resource-version'),
      this.panel.getAttribute('data-resource-origin-version'),
    ]);
    return {
      uid: attributes[0] ?? '', generation: attributes[1] ?? '',
      version: attributes[2] ?? '', originVersion: attributes[3] ?? '',
    };
  }

  public async assertIdentity(expected: ResourceAccessIdentity): Promise<void> {
    expect(await this.identity()).toEqual(expected);
  }

  private async save(): Promise<void> {
    await this.page.getByTestId(TestIds.resourceAccess.save).click();
    await expect(this.page.getByTestId(TestIds.resourceAccess.saved)).toBeVisible();
  }
}
