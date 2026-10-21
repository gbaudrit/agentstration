import { expect } from '@playwright/test';
import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';

export interface ExerciseResourceAccessInput {
  authorizedWorkspaceName: string;
  siblingWorkspaceName: string;
  vaultName: string;
  vaultDisplayName: string;
  workspaceSecretName: string;
  managedToolName?: string;
  username?: string;
  password?: string;
}

const invalidTargetEnglish = 'Select a valid descendant scope. Owner, parent, sibling, and unrelated scopes cannot receive this grant.';
const invalidTargetFrench = 'Sélectionnez un périmètre descendant valide. Les périmètres propriétaire, parent, frère et sans lien ne peuvent pas recevoir cette autorisation.';

export const exerciseResourceAccess: Journey<ExerciseResourceAccessInput> = async (context, input) => {
  validate(input);
  const { descendantSecrets: secrets, resourceAccess: access, organizationWorkspaces: workspaces } = context.pages;

  await context.pages.login.signIn(context.consoleUrl, input.username, input.password);
  const toolName = input.managedToolName ?? await access.ensureManagedUtilitiesTool(context.consoleUrl);
  const authorizedWorkspaceId = await workspaces.selectByName(input.authorizedWorkspaceName);
  const authorizedScope = `/workspaces/${authorizedWorkspaceId}`;

  await secrets.openNewVault(context.consoleUrl);
  const tenantScope = await secrets.selectScope('tenant');
  await secrets.createVault(input.vaultName, input.vaultDisplayName);

  await secrets.openNewSecret(context.consoleUrl);
  await assertVaultOffer(secrets, input.vaultName, tenantScope, false, 'authorized Workspace before grant');
  const siblingWorkspaceId = await workspaces.selectByName(input.siblingWorkspaceName);
  const siblingScope = `/workspaces/${siblingWorkspaceId}`;
  await secrets.openNewSecret(context.consoleUrl);
  await assertVaultOffer(secrets, input.vaultName, tenantScope, false, 'sibling Workspace');
  await context.checkpoint({ name: Checkpoints.resourceAccess.defaultDenied, page: context.pages.page, target: secrets.secretEditor });

  await workspaces.selectByName(input.authorizedWorkspaceName);
  await secrets.openVault(context.consoleUrl, input.vaultName, tenantScope);
  await access.openVaultAccess();
  await access.rejectInjectedTarget('/instance', invalidTargetEnglish);
  await context.checkpoint({ name: Checkpoints.resourceAccess.invalidTarget, page: context.pages.page, target: access.panel });
  await access.addGrant(authorizedScope);
  await context.checkpoint({ name: Checkpoints.resourceAccess.editableGranted, page: context.pages.page, target: access.grant(authorizedScope) });

  await secrets.openNewSecret(context.consoleUrl);
  await secrets.createSecret(input.workspaceSecretName, input.workspaceSecretName, input.vaultName, tenantScope);
  await workspaces.selectByName(input.siblingWorkspaceName);
  await secrets.openNewSecret(context.consoleUrl);
  await assertVaultOffer(secrets, input.vaultName, tenantScope, false, 'sibling Workspace after exact grant');
  await context.checkpoint({ name: Checkpoints.resourceAccess.siblingDenied, page: context.pages.page, target: secrets.secretEditor });

  await workspaces.selectByName(input.authorizedWorkspaceName);
  const managedIdentity = await access.openManagedTool(context.consoleUrl, toolName);
  expect(managedIdentity.originVersion).not.toBe('');
  await access.addGrant(authorizedScope);
  await context.checkpoint({ name: Checkpoints.resourceAccess.managedGranted, page: context.pages.page, target: access.grant(authorizedScope) });

  await workspaces.selectByName(input.siblingWorkspaceName);
  expect(siblingScope).not.toBe(authorizedScope);

  await context.pages.consoleAdministration.open(context.consoleUrl, '/settings/profile', 'profileSettings');
  await context.pages.consoleAdministration.restoreLanguage(context.consoleUrl, 'fr-FR');
  await access.openManagedTool(context.consoleUrl, toolName);
  await access.rejectInjectedTarget('/instance', invalidTargetFrench);
  await context.pages.consoleAdministration.restoreLanguage(context.consoleUrl, 'en-US');

  await access.openManagedTool(context.consoleUrl, toolName);
  await access.assertIdentity(managedIdentity);
  await access.removeGrant(authorizedScope);
  await access.assertIdentity(managedIdentity);

  await workspaces.selectByName(input.authorizedWorkspaceName);
  await secrets.openVault(context.consoleUrl, input.vaultName, tenantScope);
  await access.openVaultAccess();
  await access.removeGrant(authorizedScope);
  await secrets.openNewSecret(context.consoleUrl);
  await assertVaultOffer(secrets, input.vaultName, tenantScope, false, 'authorized Workspace after revocation');
  await context.checkpoint({ name: Checkpoints.resourceAccess.revoked, page: context.pages.page, target: secrets.secretEditor });
};

async function assertVaultOffer(
  secrets: import('../pages/descendant-secrets.page.js').DescendantSecretsPage,
  name: string,
  scopeRef: string,
  expected: boolean,
  context: string,
): Promise<void> {
  const offered = await secrets.vaultIsOffered(name, scopeRef);
  if (offered !== expected) throw new Error(`Vault '${name}' visibility was ${offered} for ${context}; expected ${expected}.`);
}

function validate(input: ExerciseResourceAccessInput): void {
  for (const key of ['authorizedWorkspaceName', 'siblingWorkspaceName', 'vaultName', 'vaultDisplayName', 'workspaceSecretName'] as const) {
    if (!input[key]?.trim()) throw new Error(`Resource Access journey input '${key}' is required.`);
  }
  if (input.authorizedWorkspaceName === input.siblingWorkspaceName) {
    throw new Error('Resource Access journey requires distinct authorized and sibling Workspaces.');
  }
}
