# Harmonisation de tous les formulaires — #1127

Cette livraison prolonge les pilotes #1090–#1095 et les neuf éditeurs #1124. Elle applique les composants communs aux champs ordinaires des autres familles existantes, y compris les inspecteurs du Designer, les formulaires dynamiques et les pages de compte rendues côté serveur. Les opérations, validations métier et droits restent dans leurs modules propriétaires.

## Inventaire et méthode

Le relevé couvre tous les fichiers `.razor` et `.cshtml` sous `src`, hors sorties `bin`/`obj`, avec des contrôles natifs ou communs, des formulaires, des identités et des éditeurs de schéma. Il distingue les composants routés, les composants imbriqués et les filtres de collection. Le relevé initial des seuls contrôles natifs/legacy comportait 100 fichiers et 391 contrôles ; ces nombres ne désignent pas des écrans.

La matrice ci-dessous comprend **50 composants routés et 75 patrons de routes**, dont les variantes création/édition, namespaces et lectures YAML. Une ligne peut intégrer plusieurs formulaires ou dialogues ; ce décompte ne signifie pas que 75 parcours complets ont été exécutés au navigateur. Les adaptateurs imbriqués sont recensés séparément.

## Contrat commun

- `FormField` associe libellé, indication obligatoire/facultative, aide, infobulle et validation au contrôle. Les libellés et aides riches ont des emplacements dédiés ; le bouton d’effacement n’est jamais imbriqué dans un label englobant.
- `ClearableText`, `ClearableNumber<T>` et `ClearableSelect<T>` conservent les types, erreurs et événements attendus par leurs propriétaires. L’effacement reste dans le champ, ne soumet pas le formulaire et restitue le focus. Un champ numérique non nullable vidé conserve une erreur et ne devient pas zéro.
- Les champs qui validaient au changement continuent à le faire : la frappe reste locale jusqu’au changement. Le bouton peut toutefois vider ce brouillon avant la perte de focus, sans déclencher une mutation intermédiaire.
- `FormCheckbox` conserve la sémantique booléenne native, avec ou sans `EditContext`. Les groupes radio exclusifs gardent leur contrôle natif.
- `FormGrid`, `FormSection` et `FormActions` acceptent classes et attributs de contexte. Les éditeurs spécialisés de code, fichiers, liaisons et schémas gardent leurs responsabilités.
- Les choix fixes de contexte et de préférence utilisent `AllowClear=false`. Les valeurs indisponibles et les références exactes de ressources restent représentées ; les permissions ne sont pas élargies par la présentation.
- Les couleurs, surfaces, espacements et états proviennent des variables sémantiques, avec clair/sombre aux deux tailles. Aucun package UX global n’est introduit.

## Écrans routés

| Famille | Composant | Routes |
| --- | --- | --- |
| resource editors | [AgentEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/AgentEditor.razor) | `/agents/new`<br />`/agents/{Name}` |
| operations | [AgentRunner.razor](../../../src/Agentstration.Console.Components/Components/Pages/AgentRunner.razor) | `/agents/{Name}/run`<br />`/runs/{RunId}` |
| administration | [BootstrapProfiles.razor](../../../src/Agentstration.Console.Components/Components/Pages/BootstrapProfiles.razor) | `/settings/bootstrap` |
| administration | [Cleanup.razor](../../../src/Agentstration.Console.Components/Components/Pages/Cleanup.razor) | `/cleanup` |
| resource editors | [DataSourceEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/DataSourceEditor.razor) | `/data-sources/new`<br />`/data-sources/{Name}` |
| resource editors | [DataSourceProfileEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/DataSourceProfileEditor.razor) | `/data-source-profiles/new`<br />`/data-source-profiles/{Name}` |
| resource editors | [EntryEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/EntryEditor.razor) | `/entries/{Name}`<br />`/namespaces/{EntryNamespace}/entries/{Name}` |
| distribution | [ExtensionDetails.razor](../../../src/Agentstration.Console.Components/Components/Pages/ExtensionDetails.razor) | `/extensions/{RegistrationName}`<br />`/extensions/enrollment/{EnrollmentInstanceId:guid}` |
| distribution | [Extensions.razor](../../../src/Agentstration.Console.Components/Components/Pages/Extensions.razor) | `/extensions` |
| resource editors | [FlowDetails.razor](../../../src/Agentstration.Console.Components/Components/Pages/FlowDetails.razor) | `/flows/{FlowId}`<br />`/namespaces/{FlowNamespace}/flows/{FlowId}` |
| resource editors | [FlowOrchestrationEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/FlowOrchestrationEditor.razor) | `/flows/{FlowId}/orchestration`<br />`/namespaces/{FlowNamespace}/flows/{FlowId}/orchestration` |
| operations | [FlowRunDetails.razor](../../../src/Agentstration.Console.Components/Components/Pages/FlowRunDetails.razor) | `/flow-runs/{RunId}` |
| operations | [KnowledgeSourceDetails.razor](../../../src/Agentstration.Console.Components/Components/Pages/KnowledgeSourceDetails.razor) | `/knowledge-sources/{Name}` |
| resource editors | [KnowledgeSourceEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/KnowledgeSourceEditor.razor) | `/knowledge-sources/new`<br />`/knowledge-sources/{Name}/edit` |
| resource editors | [ModelDetails.razor](../../../src/Agentstration.Console.Components/Components/Pages/ModelDetails.razor) | `/models/{Name}` |
| resource editors | [ModelProfileEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/ModelProfileEditor.razor) | `/modelprofiles/new`<br />`/modelprofiles/{Name}` |
| resource editors | [ModelProviderDetails.razor](../../../src/Agentstration.Console.Components/Components/Pages/ModelProviderDetails.razor) | `/modelproviders/new`<br />`/modelproviders/{Name}` |
| resource editors | [NamespacedAgentDetails.razor](../../../src/Agentstration.Console.Components/Components/Pages/NamespacedAgentDetails.razor) | `/namespaces/{AgentNamespace}/agents/{Name}` |
| resource editors | [NewFlow.razor](../../../src/Agentstration.Console.Components/Components/Pages/NewFlow.razor) | `/flows/new` |
| administration | [OrganizationMemberDetails.razor](../../../src/Agentstration.Console.Components/Components/Pages/OrganizationMemberDetails.razor) | `/settings/organization/members/{UserId:guid}` |
| administration | [OrganizationMembers.razor](../../../src/Agentstration.Console.Components/Components/Pages/OrganizationMembers.razor) | `/settings/organization/members` |
| administration | [OrganizationWorkspaces.razor](../../../src/Agentstration.Console.Components/Components/Pages/OrganizationWorkspaces.razor) | `/settings/organization/structure`<br />`/settings/organization/workspaces` |
| distribution | [PackComposer.razor](../../../src/Agentstration.Console.Components/Components/Pages/PackComposer.razor) | `/pack-projects/new` |
| distribution | [PackProjectDetails.razor](../../../src/Agentstration.Console.Components/Components/Pages/PackProjectDetails.razor) | `/pack-projects/{ProjectId:guid}` |
| distribution | [Packs.razor](../../../src/Agentstration.Console.Components/Components/Pages/Packs.razor) | `/packs` |
| resource editors | [ParameterEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/ParameterEditor.razor) | `/parameters/new`<br />`/parameters/{Name}` |
| administration | [ProfileSettings.razor](../../../src/Agentstration.Console.Components/Components/Pages/ProfileSettings.razor) | `/settings/profile` |
| distribution | [ResourcePlanDetails.razor](../../../src/Agentstration.Console.Components/Components/Pages/ResourcePlanDetails.razor) | `/resource-plans/{Id:guid}` |
| distribution | [ResourcePlans.razor](../../../src/Agentstration.Console.Components/Components/Pages/ResourcePlans.razor) | `/resource-plans` |
| resource editors | [RuntimeProfileEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/RuntimeProfileEditor.razor) | `/runtimeprofiles/new`<br />`/runtimeprofiles/{Name}` |
| resource editors | [SchemaEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/SchemaEditor.razor) | `/schemas/new`<br />`/schemas/{Name}` |
| resource editors | [SecretEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/SecretEditor.razor) | `/secrets/new`<br />`/secrets/{Name}` |
| resource editors | [SourceProviderDetails.razor](../../../src/Agentstration.Console.Components/Components/Pages/SourceProviderDetails.razor) | `/sourceproviders/new`<br />`/sourceproviders/{Name}` |
| sources | [SourceRegistries.razor](../../../src/Agentstration.Console.Components/Components/Pages/SourceRegistries.razor) | `/settings/source-registries`<br />`/settings/source-registries/new`<br />`/settings/source-registries/{Name}` |
| sources | [SourceRegistryDiscovery.razor](../../../src/Agentstration.Console.Components/Components/Pages/SourceRegistryDiscovery.razor) | `/settings/source-registries/discovery` |
| sources | [Sources.razor](../../../src/Agentstration.Console.Components/Components/Pages/Sources.razor) | `/settings/sources`<br />`/settings/sources/{Publisher}/{Name}` |
| operations | [StagedArtifactDetails.razor](../../../src/Agentstration.Console.Components/Components/Pages/StagedArtifactDetails.razor) | `/artifacts/staged/{Id}` |
| resource editors | [ToolCategoryEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/ToolCategoryEditor.razor) | `/tools/categories/new`<br />`/tools/categories/{Name}` |
| resource editors | [ToolDefinitionEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/ToolDefinitionEditor.razor) | `/tools/definitions/new`<br />`/tools/definitions/{Name}` |
| resource editors | [ToolDetails.razor](../../../src/Agentstration.Console.Components/Components/Pages/ToolDetails.razor) | `/tools/{Name}` |
| resource editors | [ToolProviderEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/ToolProviderEditor.razor) | `/tools/providers/new`<br />`/tools/providers/{Name}` |
| operations | [TriggerDetails.razor](../../../src/Agentstration.Console.Components/Components/Pages/TriggerDetails.razor) | `/triggers/{Name}` |
| resource editors | [TriggerEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/TriggerEditor.razor) | `/triggers/new`<br />`/triggers/{Name}/edit` |
| resource editors | [VaultEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/VaultEditor.razor) | `/vaults/new`<br />`/vaults/{Name}` |
| administration | [Workspaces.razor](../../../src/Agentstration.Console.Components/Components/Pages/Workspaces.razor) | `/workspaces` |
| server account forms | [Login.cshtml](../../../src/Agentstration.Console.Web/Pages/Login.cshtml) | `/login` |
| server account forms | [Pat.cshtml](../../../src/Agentstration.Web/Pages/Account/Pat.cshtml) | `/account/pat` |
| server account forms | [Security.cshtml](../../../src/Agentstration.Web/Pages/Account/Security.cshtml) | `/account/security` |
| server account forms | [Bootstrap.cshtml](../../../src/Agentstration.Web/Pages/Bootstrap.cshtml) | `/bootstrap` |
| server account forms | [Login.cshtml](../../../src/Agentstration.Web/Pages/Login.cshtml) | `/login` |

## Composants imbriqués

Les champs ordinaires des créateurs contextuels Paramètre/Secret, des liaisons de valeurs, de l’administration des accès, des sélections de périmètre, des configurations d’outils et d’acquisition, de la composition de Packs, des inspecteurs Flow et des formulaires Workplace consomment le même contrat. `JsonSchemaField` partage le libellé/aide/erreur et les contrôles scalaires, tout en conservant les valeurs JSON typées, objets, listes, chemins verrouillés et replis explicites.

- [FlowRunTimeline.razor](../../../src/Agentstration.Console.Components/Components/FlowRunTimeline.razor)
- [OrchestrationDefinitionEditor.razor](../../../src/Agentstration.Console.Components/Components/Flows/OrchestrationDefinitionEditor.razor)
- [ModelProfilePicker.razor](../../../src/Agentstration.Console.Components/Components/ModelProfiles/ModelProfilePicker.razor)
- [AgentToolConfiguration.razor](../../../src/Agentstration.Console.Components/Components/Pages/AgentToolConfiguration.razor)
- [AgentToolSetConfiguration.razor](../../../src/Agentstration.Console.Components/Components/Pages/AgentToolSetConfiguration.razor)
- [DataSourceAcquisitionArtifacts.razor](../../../src/Agentstration.Console.Components/Components/Pages/DataSourceAcquisitionArtifacts.razor)
- [DataSourceAcquisitionToolEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/DataSourceAcquisitionToolEditor.razor)
- [DescendantUseGrantEditor.razor](../../../src/Agentstration.Console.Components/Components/Pages/DescendantUseGrantEditor.razor)
- [ResourceAccessPanel.razor](../../../src/Agentstration.Console.Components/Components/Pages/ResourceAccessPanel.razor)
- [PackAgentDefinitionForm.razor](../../../src/Agentstration.Console.Components/Components/Shared/PackAgentDefinitionForm.razor)
- [ResourceScopeFormField.razor](../../../src/Agentstration.Console.Components/Components/Shared/ResourceScopeFormField.razor)
- [ResourceScopePicker.razor](../../../src/Agentstration.Console.Components/Components/Shared/ResourceScopePicker.razor)
- [ContextualParameterCreator.razor](../../../src/Agentstration.Console.Components/Components/Values/ContextualParameterCreator.razor)
- [ContextualSecretCreator.razor](../../../src/Agentstration.Console.Components/Components/Values/ContextualSecretCreator.razor)
- [ValueBindingRequirementEditor.razor](../../../src/Agentstration.Console.Components/Components/Values/ValueBindingRequirementEditor.razor)
- [IconPicker.razor](../../../src/Agentstration.Web.Components/IconPicker.razor)
- [JsonSchemaField.razor](../../../src/Agentstration.Web.Components/JsonSchema/JsonSchemaField.razor)
- [JsonSchemaInputEditor.razor](../../../src/Agentstration.Web.Components/JsonSchema/JsonSchemaInputEditor.razor)
- [LabelsEditor.razor](../../../src/Agentstration.Web.Components/LabelsEditor.razor)
- [ResourceIdentityFields.razor](../../../src/Agentstration.Web.Components/ResourceIdentityFields.razor)
- [SchemaDocumentEditor.razor](../../../src/Agentstration.Web.Components/Schemas/SchemaDocumentEditor.razor)
- [SchemaPropertyNode.razor](../../../src/Agentstration.Web.Components/Schemas/SchemaPropertyNode.razor)
- [SearchBox.razor](../../../src/Agentstration.Web.Components/SearchBox.razor)
- [SecretReferencePicker.razor](../../../src/Agentstration.Web.Components/SecretReferencePicker.razor)
- [FlowAgentInspector.razor](../../../src/Agentstration.Web.FlowDesigner/Components/FlowAgentInspector.razor)
- [FlowCallInspector.razor](../../../src/Agentstration.Web.FlowDesigner/Components/FlowCallInspector.razor)
- [FlowConditionInspector.razor](../../../src/Agentstration.Web.FlowDesigner/Components/FlowConditionInspector.razor)
- [FlowDesigner.razor](../../../src/Agentstration.Web.FlowDesigner/Components/FlowDesigner.razor)
- [FlowInputInspector.razor](../../../src/Agentstration.Web.FlowDesigner/Components/FlowInputInspector.razor)
- [FlowOutputInspector.razor](../../../src/Agentstration.Web.FlowDesigner/Components/FlowOutputInspector.razor)
- [FlowRouterInspector.razor](../../../src/Agentstration.Web.FlowDesigner/Components/FlowRouterInspector.razor)
- [FlowToolInspector.razor](../../../src/Agentstration.Web.FlowDesigner/Components/FlowToolInspector.razor)
- [FlowTransformInspector.razor](../../../src/Agentstration.Web.FlowDesigner/Components/FlowTransformInspector.razor)
- [SchemaBindingEditor.razor](../../../src/Agentstration.Web.FlowDesigner/Components/SchemaBindingEditor.razor)
- [ConversationComposer.razor](../../../src/Agentstration.Workplace.Components/ConversationComposer.razor)
- [EntryFieldRenderer.razor](../../../src/Agentstration.Workplace.Components/EntryFieldRenderer.razor)
- [PendingActionPanel.razor](../../../src/Agentstration.Workplace.Components/PendingActionPanel.razor)
- [PromptEntry.razor](../../../src/Agentstration.Workplace.Components/PromptEntry.razor)

## Pages Razor et sécurité des valeurs

Les pages `/login` des deux hôtes, `/bootstrap`, `/account/security` et `/account/pat` utilisent le même vocabulaire de champs via `ClearableControlTagHelper` et une amélioration progressive JavaScript. Elles gardent leurs formulaires POST, noms de champs, model binding, antiforgery et validation serveur. Sans JavaScript, leurs contrôles natifs et leur soumission restent utilisables. Une sélection effacée poste explicitement une valeur vide, plutôt que d’omettre le champ et de substituer une valeur par défaut.

Les pages anonymes suivent l’apparence système. Les pages de compte authentifiées lisent la préférence existante ; elles ne l’écrivent pas. Les mots de passe et nouvelles valeurs secrètes gardent leurs types, autocomplete et comportement d’écriture seule. La mise en forme n’ajoute aucune lecture de valeur secrète, suppression, sauvegarde automatique ou publication.

## Hors formulaire

Les listes et grilles sont suivies par #1121. Les filtres purs, la recherche de navigation, la palette de commandes et les formulaires de déconnexion sans champ modifiable restent dans leurs composants existants. Les contrôles natifs internes aux primitives sont leurs implémentations, pas des oublis d’adoption. Les radios, fichiers, champs cachés antiforgery et cases à cocher postées par Razor gardent leur sémantique native.

Les écrans de listes qui hébergent aussi de vrais formulaires (Sources, Packs, organisation, espaces de travail) reçoivent les champs communs dans ces formulaires ; leur modèle de collection n’est pas refondu.

## Validation

Le build Release de la solution passe sans avertissement ni erreur. Les 958 tests rapides passent, dont 14 contrôles supplémentaires sur les primitives, les formulaires serveur, les métadonnées de Pack et la prévention du retour des contrôles ordinaires non partagés.

Les vérifications navigateur des parcours existants couvrent création/publication, administration, organisation et droits, Sources/registres, composition de Packs, contrats et transitions du Designer, schémas dynamiques et Workplace. Les tests de Sources ont été adaptés au nouvel arrêt clavier sur le bouton d’effacement ; le parcours vérifie le bouton, l’aide, puis la sélection, sans supprimer l’assertion de focus. Les 15 cas de la campagne ciblée Sources/distribution/organisation/champs Flow passent après cette correction. Les vérifications complémentaires et la lane Integration sont encore en cours au moment de cette première publication en brouillon ; leurs résultats définitifs seront reportés dans la PR.

Les contrôles ponctuels sur les vrais hôtes locaux vérifient 144 combinaisons d’éditeurs (18 variantes × deux langues × deux apparences × deux tailles), 72 interactions de champs des pages d’initialisation (neuf champs × huit combinaisons) et huit variantes du formulaire Workplace. Ils vérifient la valeur vide, le retour du focus, l’absence de navigation/soumission et le débordement de page. Les essais serveur comprennent une initialisation réelle, une sélection d’expiration vide rejetée sans création de jeton, une connexion refusée sans réaffichage du mot de passe et une connexion sans JavaScript.

Ces contrôles ponctuels ne sont pas présentés comme un nouveau scénario réutilisable. La couverture #1128 est préparée dans une PR empilée distincte, à la demande explicite du mainteneur ; sa préparation avant fusion ne vaut pas validation UX finale. Le fichier `inventory.json` conserve le relevé des routes, composants imbriqués et exclusions de collections.


## Captures de contrôle

Les captures montrent les vrais écrans locaux, après effacement d’un champ. La validation et les états disponibles sont ceux de leurs modules. Les variantes de langue sont vérifiées par le relevé de validation ; les captures conservées sont en français.

| Exemple | Sombre, bureau | Clair, mobile |
| --- | --- | --- |
| Profil de source | [Capture](captures/profil-source-dark-1440.png) | [Capture](captures/profil-source-light-390.png) |
| Fournisseur de modèle | [Capture](captures/fournisseur-modele-dark-1440.png) | [Capture](captures/fournisseur-modele-light-390.png) |
| Registre de Sources | [Capture](captures/registre-dark-1440.png) | [Capture](captures/registre-light-390.png) |
| Composition de Pack | [Capture](captures/composition-pack-dark-1440.png) | [Capture](captures/composition-pack-light-390.png) |
| Workplace | [Capture](captures/workplace-dark-1440.png) | [Capture](captures/workplace-light-390.png) |
| Initialisation du compte | [Capture](captures/bootstrap-dark-1440.png) | [Capture](captures/bootstrap-light-390.png) |

Le contrôle visuel a également conduit à faire suivre l’apparence sélectionnée aux champs mobiles du Workplace, à son logo mobile et aux titres voisins du formulaire.
