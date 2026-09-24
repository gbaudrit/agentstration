import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';

export interface ExerciseFoundrySecretBindingInput {
  workspaceName: string;
  providerName: string;
  providerDisplayName: string;
  secretValue: string;
  username?: string;
  password?: string;
}

export const exerciseFoundrySecretBinding: Journey<ExerciseFoundrySecretBindingInput> = async (context, input) => {
  if (!input.workspaceName?.trim() || !input.providerName?.trim() || !input.providerDisplayName?.trim()) throw new Error('Foundry Secret Binding journey requires workspace and provider identities.');
  const page = context.pages.foundrySecretBinding;
  await context.pages.login.signIn(context.consoleUrl, input.username, input.password);
  await context.pages.organizationWorkspaces.selectByName(input.workspaceName);
  await context.pages.descendantSecrets.openNewVault(context.consoleUrl);
  await context.pages.descendantSecrets.selectScope('tenant');
  await context.pages.descendantSecrets.createVault(`${input.providerName}-vault`, `${input.providerDisplayName} Vault`);
  await context.pages.descendantSecrets.initializeVault();
  await page.createProvider(context.consoleUrl, input.providerName, input.providerDisplayName);
  await page.createParameter('projectEndpoint', 'https://foundry.example.test/api/projects/browser');
  await page.createParameter('inferenceEndpoint', 'https://foundry.example.test/openai/v1');
  await page.createParameter('authenticationMode', 'ApiKey');
  const secretName = await page.createSecret('credential', input.secretValue);
  await page.saveProvider(input.providerName);
  await context.checkpoint({ name: Checkpoints.foundrySecretBinding.secretBound, page: context.pages.page, target: page.requirement('credential') });
  await page.openSecret(context.consoleUrl, secretName);
  await page.deleteSecretValue();
  await page.openProvider(context.consoleUrl, input.providerName);
  await page.expectUnavailable('credential');
  await context.checkpoint({ name: Checkpoints.foundrySecretBinding.secretUnavailable, page: context.pages.page, target: page.requirement('credential') });
};
