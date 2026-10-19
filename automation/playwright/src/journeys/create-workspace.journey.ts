import { Checkpoints } from '../contracts/checkpoints.js';
import type { WorkspaceDefinition } from '../pages/organization-workspaces.page.js';
import type { Journey } from './journey.js';

export type CreateWorkspaceInput = WorkspaceDefinition & {
  username?: string;
  password?: string;
};

export const createWorkspace: Journey<CreateWorkspaceInput> = async (context, input) => {
  validate(input);
  await context.pages.login.signIn(context.consoleUrl, input.username, input.password);
  await context.pages.ensureTheme(context.theme ?? 'dark');
  await context.pages.organizationWorkspaces.open(context.consoleUrl);
  await context.checkpoint({
    name: Checkpoints.createWorkspace.formEmpty,
    page: context.pages.page,
    target: context.pages.organizationWorkspaces.createForm,
  });

  await context.pages.organizationWorkspaces.fillIdentity(input);
  await context.checkpoint({
    name: Checkpoints.createWorkspace.identityComplete,
    page: context.pages.page,
    target: context.pages.organizationWorkspaces.createForm,
  });

  await context.pages.organizationWorkspaces.create(input);
  await context.checkpoint({
    name: Checkpoints.createWorkspace.created,
    page: context.pages.page,
    target: context.pages.organizationWorkspaces.row(input.name),
  });

  await context.pages.organizationWorkspaces.selectByName(input.name);
  await context.checkpoint({
    name: Checkpoints.createWorkspace.selected,
    page: context.pages.page,
    target: context.pages.organizationWorkspaces.createForm,
  });
};

function validate(input: CreateWorkspaceInput): void {
  if (!input.name?.trim()) throw new Error("Create workspace journey input 'name' is required.");
  if (!input.displayName?.trim()) throw new Error("Create workspace journey input 'displayName' is required.");
}
