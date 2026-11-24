import { expect, type Page } from '@playwright/test';

export async function waitForModelDiscovery(page: Page, consoleUrl: string, providerName: string,
  headers?: Record<string, string>): Promise<void> {
  await expect.poll(async () => {
    const response = await page.request.get(
      `${consoleUrl}/api/modelproviders/${encodeURIComponent(providerName)}/status`, { headers });
    expect(response.ok(), await response.text()).toBeTruthy();
    const status = await response.json() as { discovery?: { state: string; errorCode?: string } };
    if (status.discovery?.state === 'failed') throw new Error(`Model discovery failed: ${status.discovery.errorCode}`);
    return status.discovery?.state;
  }, { timeout: 45_000, message: 'Wait for accepted model discovery to finish' }).toBe('succeeded');
}
