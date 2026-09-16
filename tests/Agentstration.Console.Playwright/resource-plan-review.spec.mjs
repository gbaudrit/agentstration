import { expect, test } from '@playwright/test';

test('review a Resource Plan from intent through changes, graph, validation, activity and stale state', async ({ page }) => {
  await page.goto('/login');
  await page.locator('#Input_UserName').fill('admin');
  await page.locator('#Input_Password').fill('admin');
  await page.locator('button[type="submit"]').click();
  await expect(page).toHaveURL(/\/$/);

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
  await expect(page.getByRole('tab')).toHaveCount(5);
  await expect(page.getByText('Route support requests')).toBeVisible();

  await page.getByRole('button', { name: 'Re-materialize' }).click();
  await expect(page.getByRole('button', { name: 'Create ChangeSet' })).toBeEnabled();
  await page.getByRole('button', { name: 'Create ChangeSet' }).click();
  await expect(page.locator('.resource-plan-change')).toHaveCount(2);
  await expect(page.getByText('Create', { exact: true }).first()).toBeVisible();

  await page.getByRole('tab', { name: 'Graph' }).click();
  await expect(page.locator('.topology-node')).toHaveCount(2);
  await expect(page.locator('.topology-edge')).toHaveCount(1);
  await page.locator('.topology-node').first().click();
  await expect(page.locator('.resource-plan-graph-details')).toContainText('triage');
  await page.getByRole('tab', { name: 'Validation' }).click();
  await page.getByRole('button', { name: 'Revalidate' }).click();
  await expect(page.getByText('Ready', { exact: true }).first()).toBeVisible();
  await page.getByRole('tab', { name: 'Activity' }).click();
  await expect(page.locator('a[href="/flow-runs/e2e-flow-run"]')).toBeVisible();

  const latest = await page.request.get(`/api/resource-plans/${plan.id.value}`);
  expect(latest.ok()).toBeTruthy();
  const refined = await page.request.put(`/api/resource-plans/${plan.id.value}`, {
    headers: { 'If-Match': latest.headers().etag },
    data: { title, goal: 'Improve support routing again', content: { schemaVersion: 'resource-planning.agentstration.io/v1', document: content } },
  });
  expect(refined.ok()).toBeTruthy();
  await page.getByRole('button', { name: 'Refresh' }).click();
  await expect(page.getByText('ChangeSet belongs to an earlier revision')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Revalidate' })).toBeDisabled();

  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('tab', { name: 'Graph' }).click();
  await expect(page.locator('.topology-viewport')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBeTruthy();
});
