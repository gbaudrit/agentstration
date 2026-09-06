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
  flowEditor: {
    createOrchestration: string;
  };
  entryEditor: {
    createEntry: string;
    publishPinnedVersion: string;
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
    flowEditor: {
      createOrchestration: 'Create Orchestration',
    },
    entryEditor: {
      createEntry: 'Create entry',
      publishPinnedVersion: 'Publish pinned version',
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
    flowEditor: {
      createOrchestration: 'Créer l’orchestration',
    },
    entryEditor: {
      createEntry: 'Créer une entrée',
      publishPinnedVersion: 'Publier la version épinglée',
    },
  },
} as const satisfies Record<SupportedTestLocale, ExpectedText>;
