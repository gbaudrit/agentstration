export const SupportedTestLocales = ['en-US', 'fr-FR'] as const;
export type SupportedTestLocale = typeof SupportedTestLocales[number];

export const GovernedInventoryTextByLocale = {
  'en-US': {
    resources: 'Managed resources', contributions: 'Contributions', installed: 'Installed', degraded: 'Degraded',
    managed: 'Managed · read-only', technical: 'Technical details', publications: 'Published versions',
    available: 'Available', unavailable: 'Unavailable', missing: 'Not materialized', conflict: 'Conflict',
    definition: 'Definition', revisions: 'Revisions', yaml: 'YAML', deployments: 'Deployments',
    kinds: ['ToolSet', 'Flow', 'Data Source Profile'], scope: 'Instance',
    bundleVersion: 'Bundle version', extensionVersion: 'Extension version',
    mutations: ['Delete', 'Save', 'Save changes', 'Publish', 'Activate', 'Apply YAML'],
    yamlEditor: 'Open YAML editor',
    delete: 'Delete', save: 'Save',
  },
  'fr-FR': {
    resources: 'Ressources gérées', contributions: 'Contributions', installed: 'Installée', degraded: 'Dégradée',
    managed: 'Gérée · lecture seule', technical: 'Détails techniques', publications: 'Versions publiées',
    available: 'Disponible', unavailable: 'Indisponible', missing: 'Non matérialisée', conflict: 'En conflit',
    definition: 'Définition', revisions: 'Révisions', yaml: 'YAML', deployments: 'Déploiements',
    kinds: ['ToolSet', 'Flow', 'Profil de source de données'], scope: 'Instance',
    bundleVersion: 'Version du bundle', extensionVersion: 'Version de l’extension',
    mutations: ['Supprimer', 'Enregistrer', 'Enregistrer les modifications', 'Publier', 'Activer', 'Appliquer le YAML'],
    yamlEditor: 'Ouvrir l’éditeur YAML',
    delete: 'Supprimer', save: 'Enregistrer',
  },
} as const;

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
  platformHealth: {
    label: string;
    connecting: string;
    operational: string;
    partiallyUnavailable: string;
  };
  platformOverview: {
    groups: readonly string[];
    labels: readonly string[];
  };
  toolExecution: {
    overview: string;
    execution: string;
    simulation: string;
    realExecution: string;
    runTool: string;
    input: string;
  };
  acquisitionFailure: { errorLegend: string };
  governedKnowledge: {
    deleteToolSet: string; confirmToolSetDelete: string; draftSaved: string; publishExposure: string; openToolSet: string; pinnedHelp: string; activeHelp: string;
    newExpiration: string; extendRetention: string; cancel: string; confirmDelete: string;
  };
  sourceLifecycle: {
    displayName: string; technicalName: string; acquisitionFlow: string; configurationSchema: string;
    save: string; yaml: string; revisions: string; definition: string; version: string;
    publish: string; activate: string; enabled: string; addDataSource: string; notReady: string;
  };
  schemaDrivenSources: {
    formMode: string;
    rawMode: string;
  };
  contextSelector: {
    change: string;
    choose: string;
    close: string;
    search: string;
    instance: string;
    tenant: string;
    workspace: string;
    globalAdministration: string;
    tenantAdministration: string;
    confirmDiscard: string;
  };
  inheritedResource: {
    title: string;
    message: string;
    changeScope: string;
  };
  flowDiagnostics: {
    modelProfileUnavailable: string;
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
        { heading: 'Design', links: [{ label: 'Agents', url: '/agents' }, { label: 'Flows', url: '/flows' }, { label: 'Schemas', url: '/schemas' }, { label: 'Entries', url: '/entries' }, { label: 'Resource plans', url: '/resource-plans' }, { label: 'Model profiles', url: '/modelprofiles' }, { label: 'Data Source Profiles', url: '/data-source-profiles' }] },
        { heading: 'Automate', links: [{ label: 'Triggers', url: '/triggers' }] },
        { heading: 'Operate', links: [{ label: 'Conversations', url: '/conversations' }, { label: 'Tasks', url: '/tasks' }] },
        { heading: 'Observe', links: [{ label: 'Agent instances', url: '/agent-instances' }, { label: 'Agent runs', url: '/agent-runs' }, { label: 'Flow runs', url: '/flow-runs' }, { label: 'Run events', url: '/run-events' }] },
        { heading: 'Workplace', links: [{ label: 'Configuration', url: '/workspaces' }] },
        { heading: 'Resources', links: [{ label: 'Packs', url: '/packs' }, { label: 'Data Sources', url: '/data-sources' }, { label: 'Knowledge Sources', url: '/knowledge-sources' }, { label: 'Artifacts', url: '/artifacts' }, { label: 'Sources', url: '/settings/sources' }, { label: 'Source registries', url: '/settings/source-registries' }] },
        { heading: 'Integrations', links: [{ label: 'MCP & Tools', url: '/tools' }, { label: 'Extensions', url: '/extensions' }, { label: 'Connected Services', url: '/connected-services' }, { label: 'Storages', url: '/storages' }, { label: 'Storage types', url: '/storage-types' }, { label: 'Model providers', url: '/modelproviders' }, { label: 'Source providers', url: '/sourceproviders' }] },
        { heading: 'Configuration', links: [{ label: 'Runtime profiles', url: '/runtimeprofiles' }, { label: 'Secrets', url: '/secrets' }, { label: 'Resource scopes', url: '/settings/resource-scopes' }] },
        { heading: 'System', links: [{ label: 'Workers', url: '/settings/runtime-workers' }, { label: 'Organization', url: '/settings/organization' }, { label: 'Bootstrap', url: '/settings/bootstrap' }, { label: 'Cleanup', url: '/cleanup' }, { label: 'Settings', url: '/settings' }] },
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
    platformHealth: {
      label: 'Platform',
      connecting: 'Connecting',
      operational: 'Operational',
      partiallyUnavailable: 'Partially unavailable',
    },
    platformOverview: {
      groups: ['Build / Configure', 'Operate', 'Supervise'],
      labels: ['Defined agents', 'Defined flows', 'Extensions', 'Model providers', 'Enabled triggers', 'Agent runs', 'Flow runs', 'Tasks running', 'Active Agent instances', 'Needs attention', 'Notifications'],
    },
    toolExecution: {
      overview: 'Overview',
      execution: 'Execution',
      simulation: 'Simulation',
      realExecution: 'Real execution',
      runTool: 'Run this tool',
      input: 'Input',
    },
    acquisitionFailure: { errorLegend: 'Error' },
    governedKnowledge: { deleteToolSet: 'Delete', confirmToolSetDelete: 'Delete ToolSet', draftSaved: 'saved without changing the active deployment.', publishExposure: 'Publish Tool exposure', openToolSet: 'Open ToolSet', pinnedHelp: 'Version 1.0.0 will be pinned; later additions do not extend a published Agent revision.', activeHelp: 'This immutable evidence controls the Tools available to the active Agent revision.', newExpiration: 'New expiration', extendRetention: 'Extend retention', cancel: 'Cancel', confirmDelete: 'Delete permanently' },
    sourceLifecycle: { displayName: 'Display name', technicalName: 'Technical name', acquisitionFlow: 'Acquisition Flow', configurationSchema: 'Configuration schema (JSON)', save: 'Save', yaml: 'YAML', revisions: 'Revisions', definition: 'Definition', version: 'Version', publish: 'Publish', activate: 'Activate', enabled: 'Enabled', addDataSource: 'Add a Data Source', notReady: 'Not ready' },
    schemaDrivenSources: {
      formMode: 'Form',
      rawMode: 'Raw JSON',
    },
    contextSelector: {
      change: 'Change Console context',
      choose: 'Choose a context',
      close: 'Close context selector',
      search: 'Search Instances, Tenants or Workspaces…',
      instance: 'Instance',
      tenant: 'Tenant',
      workspace: 'Workspace',
      globalAdministration: 'Global administration',
      tenantAdministration: 'Tenant administration',
      confirmDiscard: 'You have unsaved changes. Discard them and change context?',
    },
    inheritedResource: {
      title: 'View only',
      message: 'To modify this resource, go to its owner scope.',
      changeScope: 'Change scope',
    },
    flowDiagnostics: {
      modelProfileUnavailable: 'An Agent model profile required by this Flow is unavailable in the execution context. Check its access policy and republish the Agent if its pinned revision changed.',
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
        { heading: 'Concevoir', links: [{ label: 'Agents', url: '/agents' }, { label: 'Flows', url: '/flows' }, { label: 'Schémas', url: '/schemas' }, { label: 'Entrées', url: '/entries' }, { label: 'Plans de ressources', url: '/resource-plans' }, { label: 'Profils de modèle', url: '/modelprofiles' }, { label: 'Profils de source de données', url: '/data-source-profiles' }] },
        { heading: 'Automatiser', links: [{ label: 'Déclencheurs', url: '/triggers' }] },
        { heading: 'Exploiter', links: [{ label: 'Conversations', url: '/conversations' }, { label: 'Tâches', url: '/tasks' }] },
        { heading: 'Observer', links: [{ label: 'Instances d’agent', url: '/agent-instances' }, { label: 'Exécutions d’agents', url: '/agent-runs' }, { label: 'Exécutions de Flows', url: '/flow-runs' }, { label: 'Événements d’exécution', url: '/run-events' }] },
        { heading: 'Workplace', links: [{ label: 'Configuration', url: '/workspaces' }] },
        { heading: 'Ressources', links: [{ label: 'Packs', url: '/packs' }, { label: 'Sources de données', url: '/data-sources' }, { label: 'Sources de connaissances', url: '/knowledge-sources' }, { label: 'Artefacts', url: '/artifacts' }, { label: 'Sources', url: '/settings/sources' }, { label: 'Registres de Sources', url: '/settings/source-registries' }] },
        { heading: 'Intégrations', links: [{ label: 'MCP & Outils', url: '/tools' }, { label: 'Extensions', url: '/extensions' }, { label: 'Services connectés', url: '/connected-services' }, { label: 'Stockages', url: '/storages' }, { label: 'Types de stockage', url: '/storage-types' }, { label: 'Fournisseurs de modèles', url: '/modelproviders' }, { label: 'Fournisseurs de Sources', url: '/sourceproviders' }] },
        { heading: 'Configuration', links: [{ label: 'Profils d’exécution', url: '/runtimeprofiles' }, { label: 'Secrets', url: '/secrets' }, { label: 'Périmètres', url: '/settings/resource-scopes' }] },
        { heading: 'Système', links: [{ label: 'Workers', url: '/settings/runtime-workers' }, { label: 'Organisation', url: '/settings/organization' }, { label: 'Bootstrap', url: '/settings/bootstrap' }, { label: 'Nettoyage', url: '/cleanup' }, { label: 'Paramètres', url: '/settings' }] },
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
    platformHealth: {
      label: 'Plateforme',
      connecting: 'Connexion en cours',
      operational: 'Opérationnelle',
      partiallyUnavailable: 'Partiellement indisponible',
    },
    platformOverview: {
      groups: ['Créer / Configurer', 'Exploiter', 'Superviser'],
      labels: ['Agents définis', 'Flows définis', 'Extensions', 'Fournisseurs de modèles', 'Déclencheurs actifs', 'Exécutions d’agents', 'Exécutions de Flows', 'Tâches en cours', 'Instances d’agent actives', 'Nécessite votre attention', 'Notifications'],
    },
    toolExecution: {
      overview: 'Vue d’ensemble',
      execution: 'Exécution',
      simulation: 'Simulation',
      realExecution: 'Exécution réelle',
      runTool: 'Exécuter cet outil',
      input: 'Entrées',
    },
    acquisitionFailure: { errorLegend: 'Erreur' },
    governedKnowledge: { deleteToolSet: 'Supprimer', confirmToolSetDelete: 'Supprimer le ToolSet', draftSaved: 'enregistrée sans modifier le déploiement actif.', publishExposure: 'Publier l’exposition des Tools', openToolSet: 'Ouvrir le ToolSet', pinnedHelp: 'La version 1.0.0 sera figée ; les ajouts ultérieurs n’étendent pas une révision d’Agent déjà publiée.', activeHelp: 'Cette preuve immuable contrôle les outils disponibles pour la révision active de l’Agent.', newExpiration: 'Nouvelle expiration', extendRetention: 'Prolonger la rétention', cancel: 'Annuler', confirmDelete: 'Supprimer définitivement' },
    sourceLifecycle: { displayName: 'Nom d’affichage', technicalName: 'Nom technique', acquisitionFlow: 'Flow d’acquisition', configurationSchema: 'Schéma de configuration (JSON)', save: 'Enregistrer', yaml: 'YAML', revisions: 'Révisions', definition: 'Définition', version: 'Version', publish: 'Publier', activate: 'Activer', enabled: 'Activée', addDataSource: 'Ajouter une source de données', notReady: 'Non prête' },
    schemaDrivenSources: {
      formMode: 'Formulaire',
      rawMode: 'JSON brut',
    },
    contextSelector: {
      change: 'Changer de contexte Console',
      choose: 'Choisir un contexte',
      close: 'Fermer le sélecteur de contexte',
      search: 'Rechercher une Instance, un Tenant ou un Workspace…',
      instance: 'Instance',
      tenant: 'Tenant',
      workspace: 'Workspace',
      globalAdministration: 'Administration globale',
      tenantAdministration: 'Administration du Tenant',
      confirmDiscard: 'Des modifications ne sont pas enregistrées. Les abandonner et changer de contexte ?',
    },
    inheritedResource: {
      title: 'Consultation uniquement',
      message: 'Pour modifier cette ressource, acc\u00e9dez \u00e0 son p\u00e9rim\u00e8tre propri\u00e9taire.',
      changeScope: 'Changer de p\u00e9rim\u00e8tre',
    },
    flowDiagnostics: {
      modelProfileUnavailable: 'Un profil de mod\u00e8le requis par un agent de ce Flow est indisponible dans le p\u00e9rim\u00e8tre d\u2019ex\u00e9cution. V\u00e9rifiez sa politique d\u2019acc\u00e8s et republiez l\u2019agent si sa r\u00e9vision \u00e9pingl\u00e9e a chang\u00e9.',
    },
  },
} as const satisfies Record<SupportedTestLocale, ExpectedText>;

// Independent browser expectations for the inventory contract.
export const ResourceScopeTextByLocale = {
  'en-US': {
    count: (visible: number, total: number) => `Showing ${visible} of ${total} resources`,
    allKinds: (total: number) => `All kinds (${total})`,
    empty: 'No matching resources',
  },
  'fr-FR': {
    count: (visible: number, total: number) => `Affichage de ${visible} ressource(s) sur ${total}`,
    allKinds: (total: number) => `Tous les types (${total})`,
    empty: 'Aucune ressource correspondante',
  },
} as const;


export const OrganizationStructureTextByLocale = {
  'en-US': {
    tenantConflict: 'A Tenant with this technical name already exists.', workspaceConflict: 'A Workspace with this technical name already exists in this Tenant.',
    invalid: 'Check the names and parent. Technical names require 2–64 letters, digits or hyphens; reserved names are unavailable.',
    title: 'Structure', current: 'Current context', parentInstance: 'PARENT INSTANCE', parentTenant: 'PARENT TENANT',
    search: 'Search the structure', empty: 'This Tenant has no business Workspaces.',
    openTenant: (name: string) => `Open Tenant ${name}`,
    openWorkspace: (name: string) => `Open Workspace ${name}`,
  },
  'fr-FR': {
    tenantConflict: 'Un tenant portant ce nom technique existe déjà.', workspaceConflict: 'Un espace de travail portant ce nom technique existe déjà dans ce tenant.',
    invalid: 'Vérifiez les noms et le parent. Le nom technique doit contenir entre 2 et 64 lettres, chiffres ou tirets ; les noms réservés sont indisponibles.',
    title: 'Structure', current: 'Contexte actuel', parentInstance: 'INSTANCE PARENTE', parentTenant: 'TENANT PARENT',
    search: 'Rechercher dans la structure', empty: 'Ce tenant ne contient aucun espace de travail métier.',
    openTenant: (name: string) => `Ouvrir le tenant ${name}`,
    openWorkspace: (name: string) => `Ouvrir l’espace de travail ${name}`,
  },
} as const;
