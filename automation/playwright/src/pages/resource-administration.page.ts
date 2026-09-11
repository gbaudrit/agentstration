import { type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

export type ResourceAdministrationMarker = keyof typeof TestIds.resourceAdministration;

export interface ResourceAdministrationRoute {
  path: string;
  marker: ResourceAdministrationMarker;
}

export const ResourceListRoutes: readonly ResourceAdministrationRoute[] = [
  { path: '/agents', marker: 'agents' },
  { path: '/deployments', marker: 'deployments' },
  { path: '/entries', marker: 'entries' },
  { path: '/modelprofiles', marker: 'modelProfiles' },
  { path: '/modelproviders', marker: 'modelProviders' },
  { path: '/runtimeprofiles', marker: 'runtimeProfiles' },
  { path: '/secrets', marker: 'secrets' },
  { path: '/vaults', marker: 'vaults' },
  { path: '/tools', marker: 'tools' },
  { path: '/tools/providers', marker: 'toolProviders' },
  { path: '/tools/definitions', marker: 'toolDefinitions' },
  { path: '/triggers', marker: 'triggers' },
] as const;

export const ResourceCreationRoutes: readonly ResourceAdministrationRoute[] = [
  { path: '/modelprofiles/new', marker: 'modelProfileEditor' },
  { path: '/modelproviders/new', marker: 'modelProviderEditor' },
  { path: '/runtimeprofiles/new', marker: 'runtimeProfileEditor' },
  { path: '/secrets/new', marker: 'secretEditor' },
  { path: '/vaults/new', marker: 'vaultEditor' },
  { path: '/tools/providers/new', marker: 'toolProviderEditor' },
  { path: '/tools/definitions/new', marker: 'toolDefinitionEditor' },
  { path: '/triggers/new', marker: 'triggerEditor' },
] as const;

export class ResourceAdministrationPage {
  public constructor(private readonly page: Page) {}

  public async open(consoleUrl: string, route: ResourceAdministrationRoute): Promise<Locator> {
    const response = await this.page.goto(`${consoleUrl}${route.path}`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Resource administration route '${route.path}' returned HTTP ${response?.status() ?? 'no response'}.`);
    const marker = this.page.getByTestId(TestIds.resourceAdministration[route.marker]);
    await marker.waitFor({ state: 'visible' });
    return marker;
  }

  public async openAll(consoleUrl: string, routes: readonly ResourceAdministrationRoute[]): Promise<Locator> {
    let marker: Locator | undefined;
    for (const route of routes) marker = await this.open(consoleUrl, route);
    if (!marker) throw new Error('At least one resource administration route is required.');
    return marker;
  }
}
