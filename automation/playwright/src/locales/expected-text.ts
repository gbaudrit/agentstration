export const SupportedTestLocales = ['en-US', 'fr-FR'] as const;
export type SupportedTestLocale = typeof SupportedTestLocales[number];

export interface ExpectedText {
  navigation: {
    main: string;
    overview: string;
    settings: string;
    profile: string;
    groups: readonly {
      heading: string;
      links: readonly { label: string; url: string }[];
    }[];
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
      profile: 'Profile',
      groups: [
        { heading: '', links: [{ label: 'Overview', url: '/' }] },
        { heading: 'Design', links: [{ label: 'Agents', url: '/agents' }, { label: 'Flows', url: '/flows' }, { label: 'Entries', url: '/entries' }, { label: 'Resource plans', url: '/resource-plans' }, { label: 'Model profiles', url: '/modelprofiles' }] },
        { heading: 'Automate', links: [{ label: 'Triggers', url: '/triggers' }] },
        { heading: 'Operate', links: [{ label: 'Conversations', url: '/conversations' }, { label: 'Tasks', url: '/tasks' }] },
        { heading: 'Observe', links: [{ label: 'Deployments', url: '/deployments' }, { label: 'Agent runs', url: '/agent-runs' }, { label: 'Flow runs', url: '/flow-runs' }, { label: 'Run events', url: '/run-events' }] },
        { heading: 'Workplace', links: [{ label: 'Configuration', url: '/workspaces' }] },
        { heading: 'Resources', links: [{ label: 'Packs', url: '/packs' }, { label: 'Sources', url: '/settings/sources' }, { label: 'Source registries', url: '/settings/source-registries' }] },
        { heading: 'Integrations', links: [{ label: 'MCP & Tools', url: '/tools' }, { label: 'Extensions', url: '/extensions' }, { label: 'Model providers', url: '/modelproviders' }, { label: 'Source providers', url: '/sourceproviders' }] },
        { heading: 'Configuration', links: [{ label: 'Runtime profiles', url: '/runtimeprofiles' }, { label: 'Secrets', url: '/secrets' }, { label: 'Resource scopes', url: '/settings/resource-scopes' }] },
        { heading: 'System', links: [{ label: 'Organization', url: '/settings/organization' }, { label: 'Bootstrap', url: '/settings/bootstrap' }, { label: 'Cleanup', url: '/cleanup' }, { label: 'Settings', url: '/settings' }] },
      ],
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
      profile: 'Profil',
      groups: [
        { heading: '', links: [{ label: 'Vue d’ensemble', url: '/' }] },
        { heading: 'Concevoir', links: [{ label: 'Agents', url: '/agents' }, { label: 'Flows', url: '/flows' }, { label: 'Entrées', url: '/entries' }, { label: 'Plans de ressources', url: '/resource-plans' }, { label: 'Profils de modèle', url: '/modelprofiles' }] },
        { heading: 'Automatiser', links: [{ label: 'Déclencheurs', url: '/triggers' }] },
        { heading: 'Exploiter', links: [{ label: 'Conversations', url: '/conversations' }, { label: 'Tâches', url: '/tasks' }] },
        { heading: 'Observer', links: [{ label: 'Déploiements', url: '/deployments' }, { label: 'Exécutions d’agents', url: '/agent-runs' }, { label: 'Exécutions de Flows', url: '/flow-runs' }, { label: 'Événements d’exécution', url: '/run-events' }] },
        { heading: 'Workplace', links: [{ label: 'Configuration', url: '/workspaces' }] },
        { heading: 'Ressources', links: [{ label: 'Packs', url: '/packs' }, { label: 'Sources', url: '/settings/sources' }, { label: 'Registres de Sources', url: '/settings/source-registries' }] },
        { heading: 'Intégrations', links: [{ label: 'MCP & Outils', url: '/tools' }, { label: 'Extensions', url: '/extensions' }, { label: 'Fournisseurs de modèles', url: '/modelproviders' }, { label: 'Fournisseurs de Sources', url: '/sourceproviders' }] },
        { heading: 'Configuration', links: [{ label: 'Profils d’exécution', url: '/runtimeprofiles' }, { label: 'Secrets', url: '/secrets' }, { label: 'Périmètres', url: '/settings/resource-scopes' }] },
        { heading: 'Système', links: [{ label: 'Organisation', url: '/settings/organization' }, { label: 'Bootstrap', url: '/settings/bootstrap' }, { label: 'Nettoyage', url: '/cleanup' }, { label: 'Paramètres', url: '/settings' }] },
      ],
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
