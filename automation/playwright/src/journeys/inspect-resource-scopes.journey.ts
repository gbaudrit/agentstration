import { Checkpoints } from '../contracts/checkpoints.js';
import type { SupportedTestLocale } from '../locales/expected-text.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney, type ConsoleJourneySetup } from './prepare-console.journey.js';

export interface InspectResourceScopesInput extends ConsoleJourneySetup {
  locale?: SupportedTestLocale;
}

export const inspectResourceScopes: Journey<InspectResourceScopesInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const locale = input.locale ?? 'en-US';
  await context.pages.consoleAdministration.restoreLanguage(context.consoleUrl, locale);
  const scopes = context.pages.resourceScopes;
  await scopes.open(context.consoleUrl);
  const workspace = await scopes.selectedWorkspace();
  await scopes.assertScopedRoot('workspace', `/workspaces/${workspace.id}`);
  await context.pages.hierarchicalContext.selectInstance();
  await scopes.open(context.consoleUrl, '/instance');
  const tenantScope = await scopes.exerciseHierarchy(workspace.name);
  await context.checkpoint({ name: Checkpoints.resourceScopes.hierarchy, page: context.pages.page, target: scopes.inventory });
  await context.pages.hierarchicalContext.selectTenant(tenantScope.split('/').at(-1)!);
  await scopes.open(context.consoleUrl, tenantScope);
  await scopes.assertScopedRoot('tenant', tenantScope);
  await context.checkpoint({ name: Checkpoints.resourceScopes.tenantRoot, page: context.pages.page, target: scopes.inventory });
  await context.pages.hierarchicalContext.selectWorkspaceWithKeyboard(workspace.id, workspace.name);
  await scopes.open(context.consoleUrl, `/workspaces/${workspace.id}`);
  await scopes.assertScopedRoot('workspace', `/workspaces/${workspace.id}`);
  await scopes.exerciseFilters(locale);
  await scopes.assertResponsiveStyles();
  await context.checkpoint({ name: Checkpoints.resourceScopes.filtered, page: context.pages.page, target: scopes.inventory });
  await scopes.openLinkedResource();
  await context.checkpoint({ name: Checkpoints.resourceScopes.navigated, page: context.pages.page });
};
