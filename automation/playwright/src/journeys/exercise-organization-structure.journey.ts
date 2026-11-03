import { randomUUID } from 'node:crypto';
import { Checkpoints } from '../contracts/checkpoints.js';
import type { SupportedTestLocale } from '../locales/expected-text.js';
import { ProductPages } from '../pages/product.pages.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney, type ConsoleJourneySetup } from './prepare-console.journey.js';

export interface ExerciseOrganizationStructureInput extends ConsoleJourneySetup {
  locale?: SupportedTestLocale;
  tenantAdminUsername?: string;
  workspaceAdminUsername?: string;
  viewerUsername?: string;
  restrictedPassword?: string;
}

export const exerciseOrganizationStructure: Journey<ExerciseOrganizationStructureInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const locale = input.locale ?? 'en-US';
  await context.pages.consoleAdministration.restoreLanguage(context.consoleUrl, locale);
  const structure = context.pages.organizationWorkspaces;
  const checkpoint = async (name: string) => context.checkpoint({ name, page: context.pages.page, target: structure.structure });
  const suffix = randomUUID().slice(0, 8);
  const tenant = { name: `ux-tenant-${suffix}`, displayName: `UX Tenant ${suffix}` };
  const workspace = { name: `ux-workspace-${suffix}`, displayName: `UX Workspace ${suffix}` };
  await structure.selectInstance();
  await structure.openStructure(context.consoleUrl, '/instance');
  await structure.inspectInstance(locale);
  await checkpoint(Checkpoints.organizationStructure.instance);
  await structure.beginTenant(locale);
  const tenantId = await structure.submitTenant(tenant);
  await structure.expandTenant(tenant.name, locale);
  await checkpoint(Checkpoints.organizationStructure.tenantCreated);
  await structure.beginWorkspace(tenant, locale);
  await checkpoint(Checkpoints.organizationStructure.parentSelected);
  const workspaceId = await structure.submitWorkspace(workspace);
  await structure.assertWorkspaceParent(tenant.name, workspace.name);
  await structure.selectInstance();
  await structure.openStructure(context.consoleUrl, '/instance');
  await structure.searchStructure(workspace.name, locale);
  await checkpoint(Checkpoints.organizationStructure.workspaceCreated);
  await structure.openTenantLink(tenant, tenantId, locale);
  await checkpoint(Checkpoints.organizationStructure.tenantContext);
  await structure.openWorkspaceLink(workspace, workspaceId, locale);
  await checkpoint(Checkpoints.organizationStructure.workspaceContext);
  await structure.selectTenantForWorkspaceId(workspaceId);
  await structure.openStructure(context.consoleUrl, `/tenants/${tenantId}`);
  await structure.assertTenantContext(tenant.name, locale);
  await structure.openStructure(context.consoleUrl, `/tenants/${tenantId}`, true);
  await structure.assertTenantContext(tenant.name, locale);
  await structure.beginWorkspace(tenant, locale);
  await structure.inspectNameFailures(workspace, 'workspace', locale);
  await structure.dismissCreation();
  await structure.selectInstance();
  await structure.openStructure(context.consoleUrl, '/instance');
  await structure.beginTenant(locale);
  await structure.inspectNameFailures(tenant, 'tenant', locale);
  await checkpoint(Checkpoints.organizationStructure.validation);
  await structure.dismissCreation();

  const browser = context.pages.page.context().browser();
  if (!browser) throw new Error('The organization journey requires isolated browser contexts for account boundaries.');
  for (const account of ['tenantAdmin', 'workspaceAdmin', 'viewer'] as const) {
    const isolated = await browser.newContext({ viewport: context.pages.page.viewportSize()!, locale, colorScheme: 'dark' });
    try {
      const page = await isolated.newPage();
      const pages = new ProductPages(page);
      const usernames = { tenantAdmin: input.tenantAdminUsername ?? 'tenant-admin', workspaceAdmin: input.workspaceAdminUsername ?? 'workspace-admin', viewer: input.viewerUsername ?? 'structure-viewer' };
      await pages.login.signIn(context.consoleUrl, usernames[account], input.restrictedPassword ?? 'Fixture973!Access');
      await pages.consoleAdministration.restoreLanguage(context.consoleUrl, locale);
      const restricted = pages.organizationWorkspaces;
      const originalScope = await restricted.currentScope();
      if (account === 'viewer') {
        await restricted.inspectViewer(context.consoleUrl, originalScope, tenantId);
      } else if (account === 'tenantAdmin') {
        await restricted.selectTenantForWorkspace('default');
        const scope = await restricted.currentScope();
        await restricted.openStructure(context.consoleUrl, scope);
        const parent = await restricted.inspectRestrictedTenant(tenant.name, locale);
        await restricted.beginWorkspace(parent, locale);
        const sibling = { name: `ux-sibling-${suffix}`, displayName: `UX Sibling ${suffix}` };
        const siblingId = await restricted.submitWorkspace(sibling);
        await restricted.assertTenantContext(parent.name, locale);
        await restricted.select(siblingId);
        await restricted.openStructure(context.consoleUrl, `/workspaces/${siblingId}`);
        await restricted.assertWorkspaceContext(sibling.name, locale);
        await restricted.selectTenantForWorkspaceId(siblingId);
        await restricted.openStructure(context.consoleUrl, scope);
        await restricted.inspectDeniedOperations(context.consoleUrl, scope, tenantId);
      } else {
        await restricted.openStructure(context.consoleUrl, originalScope);
        const parent = await restricted.currentTenantDefinition();
        await restricted.beginWorkspace(parent, locale);
        const unassigned = { name: `ux-unassigned-${suffix}`, displayName: `UX Unassigned ${suffix}` };
        await restricted.submitInaccessibleWorkspace(unassigned, originalScope);
        await structure.assertPersistedWorkspace(context.consoleUrl, parent.name, unassigned.name);
        await restricted.inspectDeniedOperations(context.consoleUrl, originalScope, tenantId);
      }
      const checkpoints = { tenantAdmin: Checkpoints.organizationStructure.tenantAdmin, workspaceAdmin: Checkpoints.organizationStructure.workspaceAdmin, viewer: Checkpoints.organizationStructure.viewer };
      await context.checkpoint({ name: checkpoints[account], page, target: restricted.structure });
    } finally {
      await isolated.close();
    }
  }
};
