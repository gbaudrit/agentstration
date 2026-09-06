export const SupportedTestLocales = ['en-US', 'fr-FR'] as const;
export type SupportedTestLocale = typeof SupportedTestLocales[number];

export interface ExpectedText {
  organizationWorkspaces: {
    title: string;
    createWorkspace: string;
    displayName: string;
    technicalName: string;
    currentWorkspace: string;
  };
}

export const ExpectedTextByLocale = {
  'en-US': {
    organizationWorkspaces: {
      title: 'Workspaces',
      createWorkspace: 'Create Workspace',
      displayName: 'Display name',
      technicalName: 'Technical name',
      currentWorkspace: 'Current workspace',
    },
  },
  'fr-FR': {
    organizationWorkspaces: {
      title: 'Espaces de travail',
      createWorkspace: 'Créer un espace de travail',
      displayName: 'Nom affiché',
      technicalName: 'Nom technique',
      currentWorkspace: 'Espace de travail actuel',
    },
  },
} as const satisfies Record<SupportedTestLocale, ExpectedText>;
