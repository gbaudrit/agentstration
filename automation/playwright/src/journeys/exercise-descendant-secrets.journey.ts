import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';

export interface ScopedResourceIdentity {
  name: string;
  displayName: string;
}

export interface ExerciseDescendantSecretsInput {
  workspaceName: string;
  vault: ScopedResourceIdentity;
  workspaceSecret: ScopedResourceIdentity;
  tenantSecret: ScopedResourceIdentity;
  username?: string;
  password?: string;
}

export const exerciseDescendantSecrets: Journey<ExerciseDescendantSecretsInput> = async (context, input) => {
  validate(input);
  const page = context.pages.descendantSecrets;
  await context.pages.login.signIn(context.consoleUrl, input.username, input.password);
  await context.pages.organizationWorkspaces.selectByName(input.workspaceName);

  await page.openNewVault(context.consoleUrl);
  const tenantScope = await page.selectScope('tenant');
  await page.createVault(input.vault.name, input.vault.displayName);
  await page.scopeBadge(tenantScope).waitFor({ state: 'visible' });
  await context.checkpoint({ name: Checkpoints.descendantSecrets.vaultCreated, page: context.pages.page, target: page.vaultEditor });

  await page.openNewSecret(context.consoleUrl);
  const workspaceScope = await page.selectScope('workspace');
  if (await page.vaultIsOffered(input.vault.name, tenantScope)) {
    throw new Error('A tenant Vault was offered to a Workspace Secret before a descendant-use grant.');
  }
  await context.checkpoint({ name: Checkpoints.descendantSecrets.vaultUnavailable, page: context.pages.page, target: page.secretEditor });

  await page.openVault(context.consoleUrl, input.vault.name, tenantScope);
  await page.addGrant(workspaceScope);
  await page.saveChanges();
  await page.openVault(context.consoleUrl, input.vault.name, tenantScope);
  await page.grantRow(workspaceScope).waitFor({ state: 'visible' });
  await context.checkpoint({ name: Checkpoints.descendantSecrets.vaultGranted, page: context.pages.page, target: page.grantRow(workspaceScope) });

  await page.openNewSecret(context.consoleUrl);
  await page.selectScope('workspace');
  if (!await page.vaultIsOffered(input.vault.name, tenantScope)) {
    throw new Error('The granted tenant Vault is not offered to the Workspace Secret.');
  }
  await page.createSecret(input.workspaceSecret.name, input.workspaceSecret.displayName, input.vault.name, tenantScope);
  await page.openSecret(context.consoleUrl, input.workspaceSecret.name, workspaceScope);
  await page.scopeBadge(workspaceScope).waitFor({ state: 'visible' });
  if (await page.selectedVault() !== `${tenantScope}|default|${input.vault.name}`) {
    throw new Error('The Workspace Secret did not retain its exact ancestor Vault reference.');
  }
  await context.checkpoint({ name: Checkpoints.descendantSecrets.workspaceSecretCreated, page: context.pages.page, target: page.secretEditor });

  await page.openNewSecret(context.consoleUrl);
  await page.selectScope('tenant');
  await page.createSecret(input.tenantSecret.name, input.tenantSecret.displayName, input.vault.name, tenantScope);
  await page.openSecret(context.consoleUrl, input.tenantSecret.name, tenantScope);
  await page.scopeBadge(tenantScope).waitFor({ state: 'visible' });
  await page.addGrant(workspaceScope);
  await page.saveChanges();
  await page.openSecret(context.consoleUrl, input.tenantSecret.name, tenantScope);
  await page.grantRow(workspaceScope).waitFor({ state: 'visible' });
  await context.checkpoint({ name: Checkpoints.descendantSecrets.secretGranted, page: context.pages.page, target: page.grantRow(workspaceScope) });

  await page.removeGrant(workspaceScope);
  await page.saveChanges();
  await page.openSecret(context.consoleUrl, input.tenantSecret.name, tenantScope);
  await page.scopeBadge(tenantScope).waitFor({ state: 'visible' });
  if (await page.grantRow(workspaceScope).count() !== 0) {
    throw new Error('The revoked descendant-use grant reappeared after reloading the Secret.');
  }
  await context.checkpoint({ name: Checkpoints.descendantSecrets.secretRevoked, page: context.pages.page, target: page.secretEditor });
};

function validate(input: ExerciseDescendantSecretsInput): void {
  if (!input.workspaceName?.trim()) throw new Error("Descendant Secrets journey input 'workspaceName' is required.");
  for (const [kind, resource] of [['vault', input.vault], ['workspaceSecret', input.workspaceSecret], ['tenantSecret', input.tenantSecret]] as const) {
    if (!resource?.name?.trim() || !resource.displayName?.trim()) {
      throw new Error(`Descendant Secrets journey input '${kind}' requires name and displayName.`);
    }
  }
}
