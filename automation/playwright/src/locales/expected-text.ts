export const SupportedTestLocales = ['en-US', 'fr-FR'] as const;
export type SupportedTestLocale = typeof SupportedTestLocales[number];

export interface ExpectedText {
  navigation: {
    main: string;
    overview: string;
    settings: string;
  };
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
    navigation: {
      main: 'Main navigation',
      overview: 'Overview',
      settings: 'Settings',
    },
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
    navigation: {
      main: 'Navigation principale',
      overview: 'Vue d’ensemble',
      settings: 'Paramètres',
    },
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
