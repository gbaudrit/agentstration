import { Checkpoints } from '../contracts/checkpoints.js';
import { TestIds } from '../contracts/test-ids.js';
import { ExpectedTextByLocale } from '../locales/expected-text.js';
import type { Journey } from './journey.js';

export interface ExerciseInheritedResourceReadOnlyInput {
  username?: string;
  password?: string;
}

export const exerciseInheritedResourceReadOnly: Journey<ExerciseInheritedResourceReadOnlyInput> = async (
  context,
  input,
) => {
  await context.pages.login.signIn(context.consoleUrl, input.username, input.password);
  const execution = await context.pages.executionScopes.ensureFixture(context.consoleUrl);
  const page = context.pages.inheritedResourceReadOnly;
  const tenantFixture = await page.ensureTenantAgent(context.consoleUrl, execution);
  const english = ExpectedTextByLocale['en-US'].inheritedResource;

  await context.pages.hierarchicalContext.selectWorkspaceWithKeyboard(execution.workspaceId, 'default');
  await page.openAgent(context.consoleUrl, execution.agentName, execution.scopes.instance, execution.scopes.workspace);
  await page.assertInheritedNotice(english, 'Instance');
  await page.assertAgentReadOnly();
  await context.checkpoint({
    name: Checkpoints.inheritedResourceReadOnly.instanceAgent,
    page: context.pages.page,
    target: page.notice,
  });
  await context.pages.hierarchicalContext.expectAgentUseAccepted(context.consoleUrl, execution);
  await page.changeToOwnerScope();
  await page.assertAgentManageable(execution.scopes.instance);

  await page.openModelProfile(
    context.consoleUrl,
    execution.modelProfileName,
    execution.scopes.instance,
    execution.scopes.workspace,
  );
  await page.assertInheritedNotice(english, 'Instance');
  await page.assertModelProfileReadOnly();
  await context.checkpoint({
    name: Checkpoints.inheritedResourceReadOnly.instanceModelProfile,
    page: context.pages.page,
    target: page.notice,
  });
  await page.assertDescendantMutationRejected(
    context.consoleUrl,
    execution.modelProfileName,
    execution.scopes.workspace,
  );
  await page.assertModelProfileSelectable(
    context.consoleUrl,
    execution.modelProfileName,
    execution.scopes.workspace,
  );

  await context.pages.consoleAdministration.restoreLanguage(context.consoleUrl, 'fr-FR');
  await page.openModelProfile(
    context.consoleUrl,
    execution.modelProfileName,
    execution.scopes.instance,
    execution.scopes.workspace,
  );
  await page.assertInheritedNotice(ExpectedTextByLocale['fr-FR'].inheritedResource, 'Instance');
  await page.changeToOwnerScope();
  await page.assertModelProfileManageable();
  await context.pages.consoleAdministration.restoreLanguage(context.consoleUrl, 'en-US');
  await context.checkpoint({
    name: Checkpoints.inheritedResourceReadOnly.ownerAdministration,
    page: context.pages.page,
    target: context.pages.page.getByTestId(TestIds.modelAdministration.profileForm),
  });

  await page.openAgent(
    context.consoleUrl,
    tenantFixture.tenantAgentName,
    execution.scopes.tenant,
    execution.scopes.workspace,
  );
  await page.assertInheritedNotice(english, 'Tenant');
  await page.assertAgentReadOnly();
  await context.checkpoint({
    name: Checkpoints.inheritedResourceReadOnly.tenantAgent,
    page: context.pages.page,
    target: page.notice,
  });
  await page.changeToOwnerScope();
  await page.assertAgentManageable(execution.scopes.tenant);
};
