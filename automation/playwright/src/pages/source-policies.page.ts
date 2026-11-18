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

  public async exerciseLifecycle(base: string, control: SourcePolicyTestControl, locale: SupportedTestLocale): Promise<void> {
    this.page.setDefaultTimeout(15_000);
    const fixtures = new SourcePolicyFixtures(this.page, base);
    const fixture = await fixtures.prepare();
    const direct = `direct-${fixture.suffix}`; const stored = `stored-${fixture.suffix}`; const knowledge = `knowledge-${fixture.suffix}`;
    const text = SourcePolicyTextByLocale[locale];
    await control.change({});
    await this.createDirect(base, fixture, direct, text.ttl);
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
    await this.page.goto(`${base}/data-sources/${direct}?context=%2Finstance&scopeRef=%2Finstance&view=acquisitions`);
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
    await this.page.goto(`${base}/knowledge-sources/${knowledge}?context=%2Finstance&view=snapshots`);
    await expect(this.page.getByTestId(TestIds.sourceLifecycle.snapshots)).toContainText(text.retired);
    await this.assertResponsive();
    await control.change({});
  }

  private async createDirect(base: string, fixture: SourcePolicyFixture, name: string, ttlLabel: string): Promise<void> {
    await this.page.goto(`${base}/data-sources/new?composition=dataset&context=%2Finstance`);
    await expect(this.page.getByTestId(TestIds.sourcePolicies.definitionFields)).toHaveAttribute('data-interactive', 'true');
    await this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceDisplayName).fill(name);
    await this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceDisplayName).blur();
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceName)).toHaveValue(name);
    await this.page.getByTestId(TestIds.sourcePolicies.service).selectOption(fixture.service.uid);
    await this.page.getByTestId(TestIds.sourcePolicies.operation).selectOption(`${fixture.operation.uid}:${fixture.operation.generation}:${fixture.operation.definition.definitionDigest}`);
    await this.page.locator('[data-schema-path="$.dataset"] input').fill('synthetic-direct');
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceSave)).toBeDisabled();
    await this.page.getByTestId(TestIds.sourcePolicies.direct).check();
    await expect(this.page.getByTestId(TestIds.sourcePolicies.lifecycle)).toContainText(ttlLabel);
    await this.page.getByTestId(TestIds.sourcePolicies.ttl).fill('01:00:00');
    await this.page.getByTestId(TestIds.sourcePolicies.ttl).blur();
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceSave)).toBeDisabled();
    await this.page.getByTestId(TestIds.sourcePolicies.ttl).fill('');
    await this.page.getByTestId(TestIds.sourcePolicies.ttl).blur();
    await this.page.getByTestId(TestIds.sourcePolicies.history).check();
    await this.page.getByTestId(TestIds.sourcePolicies.retention).fill('24:00:00');
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceSave)).toBeDisabled();
    await this.page.getByTestId(TestIds.sourcePolicies.history).uncheck();
    await this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceSave).click();
    await expect(this.page).toHaveURL(new RegExp(`/data-sources/${name}\\?`));
    await this.openDataset(base, name);
    await expect(this.page.getByTestId(TestIds.sourcePolicies.direct)).toBeChecked();
    await expect(this.page.getByTestId(TestIds.sourcePolicies.storage)).toHaveValue('');
  }

  public async openDataset(base: string, name: string): Promise<void> {
    await this.page.goto(`${base}/data-sources/${name}?context=%2Finstance&scopeRef=%2Finstance&view=definition`);
    await expect(this.page.getByTestId(TestIds.sourcePolicies.definitionFields)).toHaveAttribute('data-interactive', 'true');
  }
  public async openKnowledge(base: string, name: string): Promise<void> {
    await this.page.goto(`${base}/knowledge-sources/${name}?context=%2Finstance&view=definition`);
    await expect(this.definition).toHaveAttribute('data-interactive', 'true');
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
    await this.page.goto(`${base}/knowledge-sources/${name}?context=%2Finstance&view=projections`);
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceProjections)).toHaveAttribute('data-interactive', 'true');
    await this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceStartProjection).click();
    let selected: any;
    await expect.poll(async () => {
      selected = (await fixtures.api('GET', path)).value.find((value: any) => !prior.includes(value.metadata.name) && value.completedAt);
      return Boolean(selected);
    }, { timeout: 90_000 }).toBe(true);
    expect(Boolean(selected.snapshotName), JSON.stringify(selected)).toBe(!failed);
    await this.page.goto(`${base}/knowledge-sources/${name}?context=%2Finstance&view=projections`);
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
