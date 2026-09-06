export const TestIds = {
  console: {
    shell: 'console-shell',
    platformOverview: 'platform-overview',
    themeToggle: 'theme-toggle',
    workspaceSelector: 'workspace-selector',
  },
  login: {
    username: 'login-username',
    password: 'login-password',
    submit: 'login-submit',
  },
  agentEditor: {
    form: 'agent-editor-form',
    identitySection: 'agent-identity-section',
    behaviorSection: 'agent-behavior-section',
    name: 'agent-name',
    displayName: 'agent-display-name',
    description: 'agent-description',
    modelProfile: 'model-profile-select',
    runtimeProfile: 'agent-runtime-profile',
    instructions: 'agent-instructions',
    createAndDeploy: 'agent-create-and-deploy',
  },
  organizationWorkspaces: {
    createForm: 'workspace-create-form',
    displayName: 'workspace-display-name',
    technicalName: 'workspace-technical-name',
    create: 'workspace-create',
    row: 'workspace-row',
  },
} as const;
