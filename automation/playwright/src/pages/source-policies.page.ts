import { selectResourceSection } from './resource-sections.js';
import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { SourcePolicyFixtures, type SourcePolicyFixture } from '../fixtures/source-policies.js';
import type { SourcePolicyTestControl } from '../fixtures/product-hosts.js';
import { SourcePolicyTextByLocale, type SupportedTestLocale } from '../locales/expected-text.js';

/** Console lifecycle actions with independent API evidence and a test-host-only clock. */
export class SourcePoliciesPage {
  public constructor(private readonly page: Page) {}
  public get history(): Locator { return this.page.getByTestId(TestIds.sourceLifecycle.snapshots); }
  public get definition(): Locator { return this.page.getByTestId(TestIds.sourcePolicies.knowledgeDefinitionFields); }

  public async exerciseLifecycle(base: string, control: SourcePolicyTestControl, locale: SupportedTestLocale, onCreationStep?: (step: string) => Promise<void>): Promise<void> {
    this.page.setDefaultTimeout(15_000);
    const fixtures = new SourcePolicyFixtures(this.page, base);
    const fixture = await fixtures.prepare();
    const direct = `direct-${fixture.suffix}`; const stored = `stored-${fixture.suffix}`; const knowledge = `knowledge-${fixture.suffix}`;
    const text = SourcePolicyTextByLocale[locale];
    await control.change({});
    await this.createDirect(base, fixture, direct, text.ttl, locale, onCreationStep);
    await fixtures.source(fixture, stored, {});
    await this.openDataset(base, stored);
    await this.page.getByTestId(TestIds.sourcePolicies.ttl).fill('01:00:00');
    await this.page.getByTestId(TestIds.sourcePolicies.history).check();
    await this.page.getByTestId(TestIds.sourcePolicies.retention).fill('24:00:00');
    await this.saveDataset(base, stored, fixtures);
    await this.openDataset(base, stored);
    await expect(this.page.getByTestId(TestIds.sourcePolicies.ttl)).toHaveValue('01:00:00');
    await expect(this.page.getByTestId(TestIds.sourcePolicies.retention)).toHaveValue('24:00:00');
    await fixtures.knowledge(fixture, knowledge, [direct, stored]);
    await this.openKnowledge(base, knowledge);
    await this.page.getByTestId(TestIds.sourcePolicies.enable).check();
    await this.page.getByTestId(TestIds.sourcePolicies.ttl).fill('00:20:00');
    await this.page.getByTestId(TestIds.sourcePolicies.history).check();
    await this.page.getByTestId(TestIds.sourcePolicies.retention).fill('24:00:00');
    await expect(this.definition).toContainText(text.history);
    await expect(this.page.getByTestId(TestIds.sourcePolicies.fallback).nth(0)).toBeDisabled();
    await this.saveKnowledge(knowledge, fixtures);
    await this.openKnowledge(base, knowledge);
    await expect(this.page.getByTestId(TestIds.sourcePolicies.ttl)).toHaveValue('00:20:00');
    const first = await this.project(base, knowledge, fixtures);
    expect(first.inputs[0].directInput).toBeTruthy();
    expect(first.inputs[1].storageInput).toBeTruthy();
    await this.page.goto(`${base}/data-sources/${direct}?context=%2Finstance&scopeRef=%2Finstance#acquisitions`);
    await expect(this.page.getByTestId(TestIds.sourcePolicies.directResult).first()).toContainText(text.direct);
    await control.change({ advanceSeconds: 21 * 60 });
    expect((await fixtures.api('POST', `/api/knowledgesources/${knowledge}/content/status?scopeRef=%2Finstance`, {})).value.preparationRequired).toBe(true);
    const implicit = await this.currentRead(knowledge, fixtures);
    expect(implicit.state).toBe('ready');
    expect(implicit.snapshotId).not.toBe(first.snapshotName);
    const implicitProjection = (await fixtures.api('GET', `/api/knowledgesources/${knowledge}/projections?scopeRef=%2Finstance`)).value
      .find((value: any) => value.snapshotName === implicit.snapshotId);
    expect(implicitProjection.inputs[1].acquisitionId).toBe(first.inputs[1].acquisitionId);
    await control.change({ advanceSeconds: 24 * 60 });
    const second = await this.project(base, knowledge, fixtures);
    expect(second.inputs[1].acquisitionId).toBe(first.inputs[1].acquisitionId);
    expect(second.inputs[0].acquisitionId).not.toBe(first.inputs[0].acquisitionId);
    await control.change({ advanceSeconds: 16 * 60 });
    const third = await this.project(base, knowledge, fixtures);
    expect(third.inputs[1].acquisitionId).not.toBe(first.inputs[1].acquisitionId);
    expect(third.inputs[1].refreshFailure).toBeFalsy();
    await control.change({ advanceSeconds: 61 * 60, failWrites: true });
    const refused = await this.project(base, knowledge, fixtures, true);
    expect(refused.errorCode).toBe('fixture_refresh_failed');
    await this.assertActive(knowledge, third.snapshotName, fixtures);
    await this.openKnowledge(base, knowledge);
    await this.page.getByTestId(TestIds.sourcePolicies.fallback).nth(1).check();
    await this.page.getByTestId(TestIds.sourcePolicies.fallbackAge).fill('04:00:00');
    await this.saveKnowledge(knowledge, fixtures);
    const fallback = await this.project(base, knowledge, fixtures);
    expect(fallback.inputs[1].acquisitionId).toBe(third.inputs[1].acquisitionId);
    expect(fallback.inputs[1].refreshFailure.code).toBe('fixture_refresh_failed');
    await this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceProjectionRow).filter({ has: this.page.getByTitle(fallback.metadata.name, { exact: true }) })
      .getByRole('button').click();
    await expect(this.page.getByTestId(TestIds.sourcePolicies.fallbackEvidence)).toContainText('fixture_refresh_failed');
    await control.change({ advanceSeconds: 3 * 3600, failWrites: true });
    await this.project(base, knowledge, fixtures, true);
    await this.assertActive(knowledge, fallback.snapshotName, fixtures);
    const denied = await this.currentRead(knowledge, fixtures);
    expect(denied.state).toBe('failed');
    await this.openKnowledge(base, knowledge);
    await this.page.getByTestId(TestIds.sourcePolicies.expiredFallback).check();
    await this.saveKnowledge(knowledge, fixtures);
    const permitted = await this.currentRead(knowledge, fixtures);
    expect(permitted.state).toBe('ready');
    expect(permitted.snapshotId).toBe(fallback.snapshotName);
    const before = (await fixtures.api('GET', `/api/knowledgesources/${knowledge}/snapshots?scopeRef=%2Finstance`)).value;
    expect(before.some((view: any) => view.snapshot.metadata.name === first.snapshotName && ![3, 'retired'].includes(view.lifecycleState))).toBe(true);
    await this.openKnowledge(base, knowledge);
    await this.page.getByTestId(TestIds.sourcePolicies.history).uncheck();
    await this.saveKnowledge(knowledge, fixtures);
    await control.change({ advanceSeconds: 3601, reconcile: true });
    const retired = (await fixtures.api('GET', `/api/knowledgesources/${knowledge}/snapshots?scopeRef=%2Finstance`)).value;
    expect(String(retired.find((view: any) => view.snapshot.metadata.name === first.snapshotName).lifecycleState)).toMatch(/retired|3/i);
    await this.page.goto(`${base}/knowledge-sources/${knowledge}?context=%2Finstance#snapshots`);
    await selectResourceSection(this.page, "snapshots");
    const refresh = this.page.getByTestId("knowledge-source-refresh");
    await refresh.click(); await expect(refresh).toBeEnabled();
    await expect(this.page.getByTestId(TestIds.sourceLifecycle.snapshots)).toContainText(text.retired);
    await this.assertResponsive();
    await control.change({});
  }

  private async createDirect(base: string, fixture: SourcePolicyFixture, name: string, ttlLabel: string, locale: SupportedTestLocale, onStep?: (step: string) => Promise<void>): Promise<void> {
    await this.page.goto(`${base}/data-sources/new?composition=dataset&context=%2Finstance`);
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-preferences-ready', 'true');
    const text = SourcePolicyTextByLocale[locale];
    await expect(this.page.getByTestId(TestIds.sourcePolicies.definitionFields)).toBeEnabled();
    await this.wizardSelect('review');
    for (const step of ['identity', 'acquisition', 'storage']) {
      await expect(this.wizardButton(step)).toHaveAttribute('data-step-complete', 'false');
      await expect(this.wizardButton(step)).toHaveAccessibleName(new RegExp(text.wizardIncomplete + '$'));
    }
    await onStep?.('review-incomplete');
    await this.activate(this.page.getByTestId(TestIds.creationWizard.finishTop));
    await this.assertWizardStep('identity');
    await expect(this.page.getByRole('alert').filter({ hasText: text.wizardIdentityRequired })).toBeVisible();
    await this.assertNotCreated(base, name);
    await this.wizardNext('acquisition');
    await this.wizardSelect('identity');
    await this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceDisplayName).fill(name);
    await this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceDisplayName).blur();
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceName)).toHaveValue(name);
    await onStep?.('identity');
    await this.wizardNext('acquisition');
    await this.page.getByTestId(TestIds.sourcePolicies.service).selectOption(fixture.service.uid);
    await this.page.getByTestId(TestIds.sourcePolicies.operation).selectOption(`${fixture.operation.uid}:${fixture.operation.generation}:${fixture.operation.definition.definitionDigest}`);
    await this.page.locator('[data-schema-path="$.dataset"] input').fill('synthetic-direct');
    await this.page.keyboard.press('Tab');
    await onStep?.('acquisition');
    await this.wizardNext('storage');
    await this.wizardNext('treatments');
    await this.rejectIncompleteCreation('storage', text.wizardStorageRequired);
    await this.assertNotCreated(base, name);
    await this.page.getByTestId(TestIds.sourcePolicies.direct).check();
    await expect(this.page.getByTestId(TestIds.sourcePolicies.lifecycle)).toContainText(ttlLabel);
    await this.page.getByTestId(TestIds.sourcePolicies.ttl).fill('01:00:00');
    await this.page.getByTestId(TestIds.sourcePolicies.ttl).blur();
    await this.wizardNext('treatments');
    await this.rejectIncompleteCreation('storage', text.datasetStorageRequired);
    await this.assertNotCreated(base, name);
    await this.page.getByTestId(TestIds.sourcePolicies.ttl).fill('');
    await this.page.getByTestId(TestIds.sourcePolicies.ttl).blur();
    await this.page.getByTestId(TestIds.sourcePolicies.history).check();
    await this.page.getByTestId(TestIds.sourcePolicies.retention).fill('24:00:00');
    await this.wizardNext('treatments');
    await this.rejectIncompleteCreation('storage', text.datasetStorageRequired);
    await this.assertNotCreated(base, name);
    await this.page.getByTestId(TestIds.sourcePolicies.history).uncheck();
    await this.wizardNext('treatments');
    await this.activate(this.page.getByTestId(TestIds.creationWizard.previousTop));
    await this.assertWizardStep('storage');
    await expect(this.page.getByTestId(TestIds.sourcePolicies.direct)).toBeChecked();
    await onStep?.('storage');
    await this.wizardNext('treatments');
    await onStep?.('treatments');
    await this.wizardNext('contracts');
    await onStep?.('contracts');
    await this.wizardNext('review');
    await expect(this.page.getByTestId(TestIds.creationWizard.review)).toContainText(`default/${name}`);
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceSave)).toBeEnabled();
    await this.assertNotCreated(base, name);
    await expect(this.page.getByTestId(TestIds.creationWizard.source).locator('nav [data-step-complete="false"]')).toHaveCount(0);
    await expect(this.wizardButton('storage')).toHaveAccessibleName(new RegExp(text.wizardComplete + '$'));
    await onStep?.('review');
    await this.activate(this.page.getByRole('button', { name: text.wizardEditAcquisition, exact: true }));
    await this.assertWizardStep('acquisition');
    await expect(this.page.getByTestId(TestIds.sourcePolicies.operation)).toHaveValue(`${fixture.operation.uid}:${fixture.operation.generation}:${fixture.operation.definition.definitionDigest}`);
    await expect(this.page.locator('[data-schema-path="$.dataset"] input')).toHaveValue('synthetic-direct');
    await this.wizardNext('storage');
    await expect(this.page.getByTestId(TestIds.sourcePolicies.direct)).toBeChecked();
    await this.wizardNext('treatments');
    await this.wizardNext('contracts');
    await this.wizardNext('review');
    await this.activate(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceSave));
    await expect(this.page).toHaveURL(new RegExp(`/data-sources/${name}\\?`));
    await this.openDataset(base, name);
    await expect(this.page.getByTestId(TestIds.sourcePolicies.direct)).toBeChecked();
    await expect(this.page.getByTestId(TestIds.sourcePolicies.storage)).toHaveValue('');
  }

  private wizardButton(step: string): Locator {
    return this.page.getByTestId(TestIds.creationWizard.source).locator(`nav button[data-wizard-step="${step}"]`);
  }
  private async wizardSelect(step: string): Promise<void> {
    await expect(this.wizardButton(step)).toBeEnabled();
    await this.activate(this.wizardButton(step));
    await this.assertWizardStep(step);
    await expect.poll(() => this.page.evaluate(() => document.activeElement?.getAttribute('data-step-id'))).toBe(step);
  }
  private async rejectIncompleteCreation(step: string, message: string): Promise<void> {
    await expect(this.wizardButton(step)).toHaveAttribute('data-step-complete', 'false');
    await this.wizardSelect('review');
    await this.activate(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceSave));
    await this.assertWizardStep(step);
    await expect(this.page.getByRole('alert').filter({ hasText: message })).toBeVisible();
  }

  private async activate(button: Locator): Promise<void> {
    if (await this.page.evaluate(() => navigator.maxTouchPoints > 0)) await button.tap();
    else { await button.focus(); await button.press('Enter'); }
  }
  private async wizardNext(expected: string): Promise<void> {
    const previous = await this.page.getByTestId(TestIds.creationWizard.source).locator('[data-step-id]:not([hidden])').getAttribute('data-step-id');
    await this.activate(this.page.getByTestId(TestIds.creationWizard.nextTop));
    await this.assertWizardStep(expected);
    if (previous !== expected) await expect.poll(() => this.page.evaluate(() => document.activeElement?.getAttribute('data-step-id'))).toBe(expected);
  }
  private async assertWizardStep(expected: string): Promise<void> {
    const active = this.page.getByTestId(TestIds.creationWizard.source).locator('[data-step-id]:not([hidden])');
    await expect(active).toHaveAttribute('data-step-id', expected);
    await expect(this.page.getByTestId(TestIds.creationWizard.source).locator('[aria-current="step"]')).toHaveCount(1);
    await this.assertResponsive();
    const alignment = await this.page.getByTestId(TestIds.creationWizard.source).locator('nav button').evaluateAll(buttons => buttons.map(button => {
      const bounds = button.getBoundingClientRect();
      const center = bounds.top + bounds.height / 2;
      return Array.from(button.querySelectorAll('.creation-wizard-number, .creation-wizard-title, .creation-wizard-state')).filter(child => getComputedStyle(child).position !== 'absolute').map(child => {
        const box = child.getBoundingClientRect();
        return Math.abs(box.top + box.height / 2 - center);
      });
    }));
    for (const offsets of alignment) for (const offset of offsets) expect(offset).toBeLessThanOrEqual(1);
  }
  private async assertNotCreated(base: string, name: string): Promise<void> {
    const response = await this.page.request.get(`${base}/api/datasources/${name}?scopeRef=%2Finstance`);
    expect(response.status()).toBe(404);
  }

  public async openDataset(base: string, name: string): Promise<void> {
    await this.page.goto(`${base}/data-sources/${name}?context=%2Finstance&scopeRef=%2Finstance#definition/configuration`);
    await expect(this.page.getByTestId(TestIds.sourcePolicies.definitionFields)).toBeEnabled();
  }
  public async openKnowledge(base: string, name: string): Promise<void> {
    await this.page.goto(`${base}/knowledge-sources/${name}?context=%2Finstance#definition/configuration`);
    await selectResourceSection(this.page, "definition/configuration");
    await expect(this.definition).toBeEnabled();
    await expect(this.page.locator('[data-definition-section="definition/configuration"]')).toBeVisible();
  }
  public async saveDataset(base: string, name: string, fixtures: SourcePolicyFixtures): Promise<void> {
    const before = (await fixtures.api('GET', `/api/datasources/${name}?scopeRef=%2Finstance`)).value.generation;
    await this.page.keyboard.press('Tab');
    await this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceSave).click();
    await expect.poll(async () => (await fixtures.api('GET', `/api/datasources/${name}?scopeRef=%2Finstance`)).value.generation).toBeGreaterThan(before);
    await expect(this.page).toHaveURL(new RegExp(`/data-sources/${name}\\?`));
  }
  public async saveKnowledge(name: string, fixtures: SourcePolicyFixtures): Promise<void> {
    const before = (await fixtures.api('GET', `/api/knowledgesources/${name}?scopeRef=%2Finstance`)).value.generation;
    await this.page.keyboard.press('Tab');
    await this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceSave).click();
    await expect.poll(async () => (await fixtures.api('GET', `/api/knowledgesources/${name}?scopeRef=%2Finstance`)).value.generation).toBeGreaterThan(before);
  }
  public async project(base: string, name: string, fixtures: SourcePolicyFixtures, failed = false): Promise<any> {
    const path = `/api/knowledgesources/${name}/projections?scopeRef=%2Finstance`;
    const prior = (await fixtures.api('GET', path)).value.map((value: any) => value.metadata.name);
    await this.page.goto(`${base}/knowledge-sources/${name}?context=%2Finstance#projections`);
    await selectResourceSection(this.page, "projections");
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceProjections)).toHaveAttribute('data-interactive', 'true');
    await this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceStartProjection).click();
    let selected: any;
    await expect.poll(async () => {
      selected = (await fixtures.api('GET', path)).value.find((value: any) => !prior.includes(value.metadata.name) && value.completedAt);
      return Boolean(selected);
    }, { timeout: 90_000 }).toBe(true);
    expect(Boolean(selected.snapshotName), JSON.stringify(selected)).toBe(!failed);
    await this.page.goto(`${base}/knowledge-sources/${name}?context=%2Finstance#projections`);
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceProjections)).toContainText(selected.metadata.name.slice(0, 20));
    return selected;
  }
  private async currentRead(name: string, fixtures: SourcePolicyFixtures): Promise<any> {
    let result = (await fixtures.api('POST', `/api/knowledgesources/${name}/content/read?scopeRef=%2Finstance`, {})).value;
    await expect.poll(async () => {
      if (result.state === 'preparing') result = (await fixtures.api('POST', `/api/knowledgesources/${name}/content/read?scopeRef=%2Finstance`, { preparationId: result.preparationId })).value;
      return result.state;
    }, { timeout: 90_000 }).not.toBe('preparing');
    return result;
  }
  private async assertActive(name: string, snapshot: string, fixtures: SourcePolicyFixtures): Promise<void> {
    const status = (await fixtures.api('POST', `/api/knowledgesources/${name}/content/status?scopeRef=%2Finstance`, {})).value;
    expect(status.activeSnapshotId).toBe(snapshot);
  }
  public async assertResponsive(): Promise<void> {
    expect(await this.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  }
}
