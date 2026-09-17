import { expect, test } from '@playwright/test';

test('review a Resource Plan from intent through changes, graph, validation, activity and stale state', async ({ page }) => {
  await page.goto('/login');
  await page.locator('#Input_UserName').fill('admin');
  await page.locator('#Input_Password').fill('admin');
  await page.locator('button[type="submit"]').click();
  await expect(page).toHaveURL(/\/$/);

  const suffix = Date.now().toString(36);
  const extensionName = `review-extension-${suffix}`;
  const providerName = `review-provider-${suffix}`;
  const modelName = `review-model-${suffix}`;
  const runtimeName = `review-runtime-${suffix}`;
  for (const [path, data] of [
    ['/api/extensionregistrations', { name: extensionName, properties: { displayName: 'Review fixture extension', endpoint: `http://127.0.0.1:5199/health/${suffix}` } }],
    ['/api/modelproviders', { name: providerName, properties: { displayName: 'Review fixture provider', extension: { name: extensionName }, contributionId: 'ollama' } }],
    ['/api/modelprofiles', { name: modelName, properties: { displayName: 'Review fixture model', provider: { name: providerName }, model: { name: 'fixture-model' } } }],
    ['/api/runtimeprofiles', { name: runtimeName, properties: { displayName: 'Review fixture runtime', runtimeType: 'microsoft-agent-framework' } }],
  ]) {
    const response = await page.request.post(path, { data });
    expect(response.status(), await response.text()).toBe(201);
  }

  const title = `Playwright review ${Date.now()}`;
  const content = {
    solution: { summary: 'Route support requests', outcomes: ['Requests are triaged'] },
    roles: [{ logicalId: 'triage', displayName: 'Triage agent', purpose: 'Classify requests', responsibilities: ['Classify'], capabilities: ['Text analysis'] }],
    workflows: [{ logicalId: 'routing', displayName: 'Routing flow', objective: 'Route requests', participants: ['triage'], collaboration: 'Ordered' }],
  };
  const created = await page.request.post('/api/resource-plans/', {
    data: { title, goal: 'Improve support routing', content: { schemaVersion: 'resource-planning.agentstration.io/v1', document: content }, flowRunId: 'e2e-flow-run' },
  });
  expect(created.status()).toBe(201);
  const plan = await created.json();
  const ready = await page.request.post(`/api/resource-plans/${plan.id.value}/status`, {
    // The API requires the ETag returned when the plan was created.
    headers: { 'If-Match': created.headers().etag },
    data: { status: 1 },
  });
  expect(ready.ok(), await ready.text()).toBeTruthy();

  await page.goto('/resource-plans');
  const row = page.locator('tr', { hasText: title });
  await expect(row).toBeVisible({ timeout: 30_000 });
  await row.getByRole('link', { name: title }).click();
  await expect(page.getByRole('tab')).toHaveCount(6, { timeout: 30_000 });
  await expect(page.getByRole('tab', { name: 'Application' })).toBeVisible();
  await expect(page.getByText('Route support requests')).toBeVisible();

  await expect(page.getByRole('button', { name: 'Refresh proposal' })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Review plan proposal' })).toBeDisabled();
  const model = page.getByRole('combobox', { name: 'Triage agent · Model profile' });
  const runtime = page.getByRole('combobox', { name: 'Triage agent · Runtime profile' });
  const modelChoice = await model.locator('option', { hasText: modelName }).getAttribute('value');
  const runtimeChoice = await runtime.locator('option', { hasText: runtimeName }).getAttribute('value');
  await model.selectOption(modelChoice);
  await expect(page.locator('.resource-plan-binding-progress')).toContainText('Choices saved automatically');
  const partialDraft = await page.request.get(`/api/resource-plans/${plan.id.value}/bindings`);
  expect(partialDraft.ok(), await partialDraft.text()).toBeTruthy();
  expect((await partialDraft.json()).bindings[0].modelProfile.name).toBe(modelName);
  await page.getByRole('link', { name: 'Back to plans' }).click();
  await page.locator('tr', { hasText: title }).getByRole('link', { name: title }).click();
  await expect(page.getByRole('combobox', { name: 'Triage agent · Model profile' })).toHaveValue(modelChoice);
  await expect(page.getByRole('combobox', { name: 'Triage agent · Runtime profile' })).toHaveValue('');
  await page.getByRole('combobox', { name: 'Triage agent · Runtime profile' }).selectOption(runtimeChoice);
  await expect(page.getByRole('button', { name: 'Review plan proposal' })).toBeEnabled();
  await page.getByRole('link', { name: 'Back to plans' }).click();
  await page.locator('tr', { hasText: title }).getByRole('link', { name: title }).click();
  await expect(page.getByRole('combobox', { name: 'Triage agent · Model profile' })).toHaveValue(modelChoice);
  await expect(page.getByRole('combobox', { name: 'Triage agent · Runtime profile' })).toHaveValue(runtimeChoice);
  await expect(page.locator('.resource-plan-binding-footer')).toContainText('Choices saved automatically');
  await page.getByRole('button', { name: 'Review plan proposal' }).click();
  await expect(page.locator('.resource-plan-review-notice')).toContainText('Plan proposal');
  await expect(page.locator('.resource-plan-review-notice')).toContainText('2 proposed resources');
  await page.locator('.resource-plan-review-notice').getByRole('button', { name: 'Save this proposal' }).click();
  await expect(page.locator('.resource-plan-recorded-notice')).toContainText('Saved proposal');
  await expect(page.locator('.resource-plan-change')).toHaveCount(2);
  await expect(page.getByText('Create', { exact: true }).first()).toBeVisible();
  await expect(page.locator('.resource-plan-change-summary')).toContainText('2');
  await expect(page.locator('.resource-plan-change').nth(1).locator('.resource-plan-change-highlights')).toContainText('Sequential');
  await expect(page.locator('.resource-plan-change-diff').first()).not.toHaveAttribute('open', '');
  await page.locator('.resource-plan-change-diff summary').first().click();
  await expect(page.locator('.resource-plan-change-diff').first()).toContainText('Model profile');
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBeTruthy();
  await page.setViewportSize({ width: 1280, height: 720 });

  await page.getByRole('tab', { name: 'Graph' }).click();
  await expect(page.locator('.topology-node')).toHaveCount(2);
  await expect(page.locator('.topology-edge')).toHaveCount(1);
  await page.locator('.topology-node').first().click();
  await expect(page.locator('.resource-plan-graph-details')).toContainText('triage');
  await page.getByRole('tab', { name: 'Verification' }).click();
  await page.getByRole('button', { name: 'Check proposal' }).click();
  await expect(page.locator('.resource-plan-validation-summary')).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBeTruthy();
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.getByRole('tab', { name: 'Activity' }).click();
  await expect(page.locator('a[href="/flow-runs/e2e-flow-run"]')).toBeVisible();

  const latest = await page.request.get(`/api/resource-plans/${plan.id.value}`);
  expect(latest.ok()).toBeTruthy();
  const refined = await page.request.put(`/api/resource-plans/${plan.id.value}`, {
    headers: { 'If-Match': latest.headers().etag },
    data: { title, goal: 'Improve support routing again', content: { schemaVersion: 'resource-planning.agentstration.io/v1', document: content } },
  });
  expect(refined.ok()).toBeTruthy();
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeEnabled({ timeout: 30_000 });
  await expect(page.getByText('ChangeSet belongs to an earlier revision')).toBeVisible({ timeout: 30_000 });
  await page.getByRole('tab', { name: 'Verification' }).click();
  await expect(page.getByRole('button', { name: 'Check proposal' })).toBeDisabled();

  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('tab', { name: 'Graph' }).click();
  await expect(page.locator('.topology-viewport')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBeTruthy();
});
