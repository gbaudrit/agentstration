export const campaignWorkspaceEnvironment = {
  name: 'AGENTSTRATION_WORKSPACE_NAME',
} as const;

export function resolveCampaignWorkspaceName(
  explicitName: string | undefined,
  environment: NodeJS.ProcessEnv = process.env,
): string | undefined {
  if (explicitName !== undefined) {
    const normalized = optional(explicitName);
    if (!normalized) throw new Error("Journey workspaceName must not be empty.");
    return normalized;
  }

  return optional(environment[campaignWorkspaceEnvironment.name]);
}

function optional(value: string | undefined): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}
