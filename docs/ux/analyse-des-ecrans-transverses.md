# Composants communs aux listes fiches et écrans Workplace

L’extension de l’analyse confirme l’intérêt d’un socle interne de composants. Les besoins dépassent les formulaires : listes, filtres, états de chargement, onglets, dialogues, références et informations d’exploitation demandent aussi des règles communes. L’IHM possède déjà une partie de ces briques et plusieurs adaptations mobiles utiles.

Ce document complète le [catalogue des formulaires](unification-des-formulaires.md). La recommandation est de consolider les composants existants et leurs comportements avant de choisir une bibliothèque globale. Un composant externe de tableau reste une option ciblée, à comparer aux fonctions réellement nécessaires.

## Périmètre et preuves

Analyse du 9 novembre 2026 sur `main` au commit `8a9fafd7432e28c7aab23109c43593cc90b35c66`, branche `ux/unification-formulaires`.

- **23 écrans supplémentaires parcourus** : 20 routes Console et 3 routes Workplace, avec lecture du DOM et captures en français, à 1440 × 1000 puis 390 × 844. L’instance locale utilise le bootstrap Development ; aucune opération d’installation, de publication, de suppression ou d’exécution n’a été soumise.
- **Deux fiches existantes ouvertes en complément** : outil intégré `agentstration.artifact.import` et profil de source `artifact-import-builtin`. Le dialogue d’installation de Pack est ouvert puis fermé sans fournir d’archive.
- **Interactions vérifiées** : recherche sans résultat et retour à la liste Structure ; ouverture et navigation clavier du dialogue Pack ; onglet Tâches de l’Activité Workplace ; présence de la cible de navigation Déploiements dans Flows.
- **États peuplés disponibles** : catalogue de 33 outils, liste de 3 profils de source, profil d’exécution intégré, compte administrateur, structure d’un tenant et d’un Workspace, tableau de 200 événements d’audit. Les listes Agents, Flows, Entrées, Packs, Tâches, Plans et conversations sont vides dans cette instance.
- **Lecture de code complémentaire** : détails de tâche et de Flow Run, revue de Resource Plan, artefacts, activité et actions en attente. Leurs états métier peuplés restent à valider dans le navigateur ; les constats correspondants ci-dessous sont issus du code.

L’absence de débordement horizontal du document sur les 23 écrans à 390 px est vérifiée. Cela ne valide pas à elle seule les tableaux longs, toutes les tailles de contenu, le clavier virtuel ou la navigation au lecteur d’écran.

## Écrans examinés et besoins associés

| Famille et routes parcourues | Observations | Besoins de composants |
| --- | --- | --- |
| Vue d’ensemble — `/` | Métriques groupées par usage, derniers événements et éléments nécessitant une attention ; composants `DashboardMetricCard` et `MetricCard` déjà présents. | Consolider groupe de métriques, état de chargement local et lien vers la liste correspondante. Conserver ces composants existants. |
| Agents et Flows — `/agents`, `/flows` | Agents propose recherche, filtre et actualisation ; Flows présente une table et une navigation de section différente. | Composition de liste, filtres, état vide contextualisé et navigation cohérente. |
| Catalogue d’outils — `/tools`, `/tools/definitions` | 33 outils, identité, provenance, disponibilité et accès à une fiche ; les lignes deviennent des cartes sur mobile. | Consolider table/carte de ressource, recherche et accès aux détails ; choisir les métadonnées prioritaires par taille d’écran. |
| Profils et sources — `/runtimeprofiles`, `/data-source-profiles`, `/data-sources` | Tables d’inventaire, liens Ouvrir, périmètres et états vides ; les fiches Outil et Profil de source utilisent des présentations d’en-tête différentes. | Identité et référence de ressource communes, résumé de fiche et actions prévisibles. |
| Catalogue d’Entrées — `/entries` | Présentation en cartes propre aux points d’entrée Workplace ; état vide avec action de création. | Réutiliser une structure de carte accessible tout en conservant cette présentation orientée catalogue. |
| Packs — `/packs` et dialogue d’installation | Métriques, installation, création de projet, aperçu et dialogues propres au domaine. | Dialogue accessible et étapes de prévisualisation/confirmation ; réutiliser les composants de champ et d’action. |
| Supervision — `/tasks`, `/flow-runs` | Tâches applique explicitement les filtres et expose la pagination ; Flow Runs filtre une collection chargée avec des contrôles locaux. | Contrat commun des filtres, résumé des résultats, pagination et état de connexion temps réel distinct du chargement des données. |
| Conversations et plans — `/conversations`, `/resource-plans` | Conversations utilise des lignes en cartes et un historique borné ; Plans un tableau, un filtre d’état et une pagination. | Composition de collection adaptable, contexte de Workspace visible et portée de l’historique explicite. |
| Structure — `/settings/organization/structure?context=%2Finstance` | Arbre Tenant/Workspace, recherche, dépliage, contexte courant et création contextualisée ; recherche sans résultat et retour à la structure vérifiés. | S’appuyer sur cet écran pour les états filtrés et la navigation hiérarchique. Extraire les comportements réutilisés avec le sélecteur de contexte. |
| Membres et audit — `/settings/organization/members?context=%2Finstance`, `/settings/organization/security-audit?context=%2Finstance` | Membres combine inventaire et création ; Audit affiche des événements techniques avec identifiants abrégés et dates. | Formulaire court, référence lisible, date/heure et indication des limites de résultat ; filtres d’audit à préciser selon les capacités de l’API. |
| Administration — `/settings/runtime-workers?context=%2Finstance`, `/settings/bootstrap?context=%2Finstance`, `/settings/profile` | Inventaire de Workers ; sélection de profils avec prévisualisation ; préférences de compte sauvegardées immédiatement. | Distinguer liste, assistant et réglage à effet immédiat. Réutiliser états, choix et retour d’action avec les comportements appropriés. |
| Accueil Workplace — `/w/default` | Navigation entre Dashboards, indicateurs d’attention et tableau de bord vide. | Consolider sélection du Dashboard, état vide et cartes d’entrée ; garder le vocabulaire utilisateur final. |
| Activité Workplace — `/w/default/tasks` | Conversations et Tâches dans des onglets avec compteurs ; filtre de statut dans Tâches, cartes et états vides spécifiques. | Onglets accessibles, résumé de collection, statut et filtres partageant les primitives des formulaires. |
| Notifications Workplace — `/w/default/notifications` | `NotificationCenter` existe déjà et gère les lignes lues/non lues et les actions associées. | Consolider dates, état vide, état d’attente et retour d’erreur sans créer un second centre de notifications. |

## Constats concrets à intégrer au socle

| Réf. | Preuve | Besoin commun et résultat attendu |
| --- | --- | --- |
| E01 | **Recherche Agents préremplie avec `search`**, constatée à l’ouverture de `/agents`. Dans le code, le paramètre chaîne est écrit `Value="search"` au lieu de lier la variable. | Le composant de recherche doit être accompagné d’exemples d’intégration corrects et d’un état initial vérifié. Réutiliser la règle d’effacement interne du catalogue des champs. |
| E02 | **Lien Déploiements sans cible dans Flows** : `/flows#deployments` est présent, aucun élément `id="deployments"` n’existe dans la page inspectée. | La navigation partagée distingue lien vers une route, ancre existante et changement de panneau. Une rubrique visible doit avoir une destination utilisable ou une indisponibilité expliquée. |
| E03 | **Filtres Flow Runs incomplètement nommés** : la recherche expose le label anglais `Search` ; les deux sélecteurs n’ont ni label associé ni nom ARIA dans le DOM inspecté. Version et Déploiement reposent sur des placeholders. | Réutiliser le contrat de champ pour les barres de filtre : label accessible, aide si nécessaire, valeur contrôlée et nom localisé. Le placeholder complète le label. |
| E04 | **Dialogue Pack au clavier** : après ouverture, le focus reste hors du dialogue ; Échap ne le ferme pas ; Maj+Tab depuis le premier bouton permet de sortir. | Un composant de dialogue doit gérer focus initial, confinement du focus pour une modale, Échap selon l’état de l’opération et retour au déclencheur. Le simple rôle `dialog` ne fournit pas ces interactions. |
| E05 | **Audit borné à 200 événements** : 200 lignes observées, appel `GetSecurityAuditEventsAsync(200, ...)`, aucune indication visible de cette limite ni pagination dans l’écran. | Une liste signale sa portée : total connu, page ou derniers résultats disponibles. Ne pas laisser penser qu’un historique borné est exhaustif. Une pagination complète dépend d’un contrat API adapté. |
| E06 | **Libellés techniques ou anglais dans l’interface française** : `TaskStatus.Draft` apparaît dans le filtre d’activité Workplace ; Tâches Console affiche `Offline`. Le composant `RealtimeStatus` contient directement les chaînes anglaises. | Couvrir tous les états présentés par une traduction et un rendu partagé. Séparer la connexion du flux temps réel de la disponibilité des données déjà chargées. |
| E07 | **Les états filtrés sont traités différemment** : Structure masque les éléments non correspondants puis rétablit le tenant après effacement ; `TaskOperationsTable` distingue explicitement absence initiale et recherche vide. Agents utilise la même branche `Filtered.Count == 0` dans les deux cas. | Consolider des variantes de l’état de liste : première utilisation, aucun résultat, accès indisponible, erreur de service et chargement. L’action proposée doit correspondre à la cause. |
| E08 | **Adaptation mobile déjà utile dans Outils** : les 33 lignes se présentent en cartes avec identité, fournisseur et disponibilité. Aucun débordement du document à 390 px dans les écrans parcourus. | Réutiliser cette adaptation comme variante documentée de la collection. Définir par écran les colonnes essentielles ; une table d’audit peut conserver un défilement interne lorsque la comparaison l’exige. |
| E09 | **Dates et identifiants formatés localement** : les composants de Tâches, Flow Runs, Conversations, Audit et Artefacts utilisent des formats et longueurs d’identifiant différents. `ArtifactReference` fournit déjà une composition nom/identifiant/lien. | Extraire un affichage commun de date, durée et référence : nom lisible, détail technique accessible, date absolue disponible et fuseau explicite lorsque nécessaire. Les règles de confidentialité restent propres au contenu. |

Les constats E01 à E08 combinent inspection du navigateur et lecture de l’implémentation. E09 repose sur la comparaison de code. Les descriptions de ressources provenant de manifestes ou de fournisseurs ne sont pas assimilées automatiquement à des chaînes produit à traduire.

## Composants existants à consolider

Comptage statique des fichiers `.razor` consommateurs, sans inférer le nombre de parcours ni leur conformité :

| Composant | Fichiers consommateurs | Orientation |
| --- | --- | --- |
| `PageHeader` / `ResourceHeader` | 86 / 5 | Clarifier leurs usages pour une page, une ressource ou un résumé ; partager identité, statut, métadonnées et emplacement des actions. |
| `DataGrid` | 54 | Garder un point d’intégration commun pour les tableaux ; préciser les capacités requises avant un remplacement technique. |
| `ResourceToolbar` / `FilterBar` / `SearchBox` | 5 / 3 / 8 | Consolider la composition et les noms accessibles, avec modes de filtrage explicites. |
| `ResourceTabs` | 26 | Faire porter au composant les interactions et relations accessibles des panneaux ; conserver des liens pour la navigation entre pages. |
| `LoadingState` / `EmptyState` / `ErrorState` | 92 / 66 / 41 | Compléter les variantes et localiser les valeurs produit par défaut encore en anglais. |
| `StatusBadge` / `StatusIndicator` | 81 / 3 | Partager la présentation sans fusionner les états métier des différents domaines. |
| `ConfirmationDialog` | 16 | Consolider localisation et interactions clavier ; réutiliser un socle de dialogue également adapté aux créations et aperçus. |

Les noms, styles ou modèles métier ne doivent pas être déplacés dans un composant générique seulement parce que deux écrans ont une apparence proche.

## Besoins supplémentaires de composants

Les noms proposés décrivent des responsabilités ; ils ne constituent pas un engagement de créer chacun une nouvelle classe.

| Besoin | Partir de | Contrat à unifier |
| --- | --- | --- |
| **Composition de liste et de filtres** | `ResourceToolbar`, `SearchBox`, `FilterBar`, `TaskOperationsTable` | Recherche, filtres actifs, compteur et portée des résultats, actualisation, état filtré et pagination. Vider un filtre reste intégré au champ ; une éventuelle réinitialisation de l’ensemble est une action de groupe distincte. Préciser si le filtrage est immédiat ou appliqué explicitement, et conserver l’état au retour d’une fiche lorsque pertinent. |
| **Table et carte de ressource** | `DataGrid`, catalogue Outils, cartes d’Entrées et Conversations | Identité, statut, métadonnées prioritaires et action d’ouverture ; variante mobile définie par écran. Tri, sélection ou pagination sont ajoutés lorsqu’ils répondent à un besoin concret, avec le support API nécessaire. |
| **État de page ou de collection** | `LoadingState`, `EmptyState`, `ErrorState`, `ServiceUnavailableState` | Présenter la cause, le contexte et une action utile ; distinguer chargement initial, rafraîchissement, absence de donnée, absence de résultat, refus d’accès et service indisponible. Préserver les données lisibles pendant un rafraîchissement quand elles restent valides. |
| **Navigation et onglets** | `ResourceTabs`, `ContractDirectionTabs`, onglets Activité | Gabarit de fiche avec Vue d’ensemble, Édition, Accès et YAML. La vue d’ensemble reste distincte du formulaire. Une sélection annoncée, relations tab/panneau, navigation clavier, compteurs et contenu activé. Les liens entre routes gardent leur sémantique de lien. Les saisies restent conservées entre panneaux. |
| **Dialogue, panneau et actions contextuelles** | `ConfirmationDialog`, dialogues Pack, créateurs contextuels, inspecteur Agent | Focus, fermeture, attente, erreur et retour au déclencheur ; distinguer modale et inspecteur non modal. Les menus de ligne exposent les actions autorisées et des noms accessibles. |
| **Résumé et référence de ressource** | `ResourceHeader`, `ResourceScopeBadge`, `ArtifactReference` | Nom d’affichage et nom technique présentés ensemble ; génération du nom technique à la création et stabilité après enregistrement. Périmètre, statut et liens utiles ; indiquer clairement lecture seule, héritage ou gestion par Pack. Une liste « Utilisé par » peut être longue : fournir une section dédiée, recherche, filtre de type et pagination, avec un simple résumé dans la fiche d’édition. |
| **Statut, date, durée et connexion** | Composants de statut existants, `RealtimeStatus`, affichages temporels des listes | Texte localisé, présentation cohérente et date absolue accessible en complément du relatif. Les adaptateurs de domaine fournissent le sens ; un indicateur de connexion ne remplace pas le statut de la tâche ou du service. |
| **Historique, résultat et artefact** | `FlowRunTimeline`, `TaskActivityView`, `TaskResultView`, `ArtifactCard`, `PendingActionPanel` | Partager date, événement, statut, références et accès au contenu. Garder les interprétations propres à l’exploitation et au Workplace, les graphes de Flow et la gestion des demandes humaines dans leurs composants existants. |

## Incidence sur le choix d’une bibliothèque

L’analyse ne justifie pas à ce stade l’adoption d’une bibliothèque complète pour toute l’UX. Une grande partie des besoins relève de la composition et de règles de comportement que l’IHM doit définir quelle que soit la technologie retenue.

L’orientation proposée est donc un **socle interne réduit**, fondé sur les composants et contrôles existants, avec des intégrations spécialisées lorsque leur gain est établi. Pour les tableaux, comparer les besoins concrets de tri, filtres, sélection, pagination, virtualisation et adaptation mobile avec les possibilités du `DataGrid` actuel. QuickGrid, MudBlazor et Radzen restent des candidats documentés dans le catalogue ; aucun choix ni ajout de dépendance n’est effectué.

Le prototype de référence devrait réunir **Paramètre**, pour le formulaire simple, **Outils**, pour une liste réellement peuplée avec variante mobile, et **Pack**, pour le dialogue. Le Profil de modèle complète ensuite la validation des champs et dépendances plus complexes.

## Ordre de travail proposé

1. **Fiabiliser les primitives** : champ avec effacement intégré, label/aide/erreur, états localisés, dialogue au clavier et navigation par onglets. Inclure les défauts E01 à E04 dans les critères de validation des composants concernés.
2. **Composer une liste de référence** : utiliser Outils et Tâches pour définir filtres, résultats, actualisation, pagination et lecture mobile. Comparer les modes immédiat et explicite sans modifier silencieusement leur effet.
3. **Consolider les fiches** : résumé, référence, statut et actions de lecture/édition ; conserver les règles de périmètre et de gouvernance.
4. **Étendre aux parcours d’activité** : valider avec des données déterministes peuplées une tâche, une conversation, une demande humaine et un résultat avec artefact, puis adopter les primitives communes dans les composants existants.
5. **Décider des intégrations ciblées** : mesurer les besoins de tableau et le coût de maintenance restant après le prototype. L’accès aux données continue de passer par les clients API existants.

Les scénarios d’exécution, la soumission des opérations d’administration, les droits non administrateur, les historiques paginés, le lecteur d’écran et tous les états temps réel restent à couvrir dans les futurs travaux. L’étude n’affirme pas leur validation.

## Sources principales

- [Listes, fiches et administration Console](https://github.com/gbaudrit/agentstration/tree/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Console.Components/Components/Pages)
- [Tableau et composants communs](https://github.com/gbaudrit/agentstration/tree/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Web.Components)
- [Supervision des tâches et statut temps réel](https://github.com/gbaudrit/agentstration/tree/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Console.Components/Components/WorkOperations)
- [Référence d’artefact](https://github.com/gbaudrit/agentstration/blob/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Console.Components/Components/Pages/ArtifactReference.razor)
- [Écrans Workplace](https://github.com/gbaudrit/agentstration/tree/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Workplace.Web/Components/Pages)
- [Composants de conversation, activité et résultats](https://github.com/gbaudrit/agentstration/tree/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Workplace.Components)
