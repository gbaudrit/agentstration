# Adoption des composants communs

Programme [#1089](https://github.com/gbaudrit/agentstration/issues/1089), branche d’intégration `ux/unification-formulaires`. Ce document distingue la livraison initiale de la migration ultérieure de l’ensemble de l’IHM. Les scénarios Playwright réutilisables restent suivis par #1098 à #1106, avec les conditions de validation UX du dépôt.

## Socle livré

| Besoin | Point d’intégration |
| --- | --- |
| Label, aide, nécessité, erreur associée | `FormField`, présentation de champ partagée, emplacement `Validation` |
| Effacement dans le contrôle | `ClearableText`, `ClearableNumber<T>`, `ClearableSelect<T>` ; focus conservé, état vide explicite |
| Structure du formulaire | `FormSection`, `FormGrid`, `FormActions`, styles et variables existants consolidés |
| Identité | `ResourceIdentityFields` existant, règles de nommage conservées ; deux champs adjacents |
| Protection des brouillons | `UnsavedNavigationGuard`, `FormDraftMonitor`, comparaison avec l’état enregistré dans les éditeurs |
| Navigation de fiche | `ResourcePageTabs`, panneaux conservés après leur première ouverture, onglets métier préservés |
| Collections | `ResourceToolbar`, `SearchBox`, `CollectionPager`, `DataGrid` et cartes mobiles existants |
| Choix d’une référence | `ModelProfilePicker` intégré à la page, recherche serveur, filtres, pagination et référence exacte |
| Dialogue | `ModalFrame` natif partagé par `ConfirmationDialog` et l’installation de Pack ; focus, Échap, attente et retour au déclencheur |
| Thèmes futurs | Variables sémantiques, couleurs de texte sur les actions, états de champ, espacements et rayons ; préférence clair/sombre/système compatible |

Le comportement de sauvegarde reste dans chaque famille. Le socle ne devient ni un moteur générique de formulaires, ni un moteur de requêtes de ressources. JSON Schema, les liaisons typées et les créateurs contextuels conservent leurs responsabilités existantes.

## Matrice de la livraison initiale

« Adopté » décrit le périmètre indiqué, sans certifier toutes les fonctions de la famille. La validation effectivement exécutée est consignée dans le [compte rendu de validation](validation-implementation.md).

| Écran ou famille | Disposition | Périmètre |
| --- | --- | --- |
| Paramètre — création et édition | Adopté | Identité, champs, effacement, erreurs, disposition et actions ; protection des valeurs saisies ou invalides. |
| Profil de modèle — fiche | Adopté | Vue d’ensemble enregistrée, Édition, Accès existant, YAML en lecture, onglet Usages compatible ; formulaire conservé entre vues. |
| Profil de modèle — paramètres | Adopté | Texte, choix fournisseur/modèle, valeurs numériques et énumérations, erreurs et actions communes. Les options guidées spécialisées gardent leur éditeur. |
| Profil de modèle — « Utilisé par » | Adopté | Recherche et continuation serveur des consommateurs Agent autorisés ; références exactes, totaux inconnus explicitement conservés. |
| Agent — fiche | Adopté | Vue d’ensemble enregistrée, Édition, Accès, YAML éditable, Contrats et Outils ; identité et brouillons conservés. |
| Agent — choix du modèle | Adopté | Catalogue inline avec recherche, fournisseur, périmètre et pagination ; aucune fenêtre modale ; charger/filtrer ne modifie pas la définition. |
| Profil d’exécution — formulaire | Adopté | Champs et choix communs, disposition, validation, verrouillage pendant la sauvegarde et protection du brouillon. |
| Déclencheur — formulaire | Adopté | Champs communs et actions ; politiques métier et éditeur de calendrier conservés ; saisies invalides protégées. |
| Définition d’outil — formulaire | Adopté | Identité, description, référence de Flow, version et délai ; actions communes et brouillon protégé ; contrats fournis par le Flow conservés. |
| Outils — catalogue | Adopté | Recherche, disponibilité, état filtré et pagination du catalogue chargé ; cartes mobiles existantes conservées. Le compteur indique les ressources chargées. |
| Packs — installation | Adopté | Cadre de dialogue accessible commun, focus, Échap et état d’attente ; prévisualisation et installation inchangées. |
| Workplace — activité/tâches | Adopté | Présentation et effacement du filtre d’état ; enum et filtrage métier existants préservés. |
| Confirmation et recherche partagées | Adopté | Localisation des valeurs produit par défaut ; bénéficient aussi aux consommateurs existants hors des pilotes. |
| Secrets, Coffres et éditeurs contextuels | Partiel | Nouvelle présentation d’identité commune lorsqu’ils utilisent `ResourceIdentityFields` ; remplacement de secret et opérations métier conservés. Migration complète reportée. |
| Schémas, sources, Entrées et auteurs de Packs | Partiel | Identité commune lorsque déjà composée ; moteurs spécialisés conservés. Migration complète reportée. |
| Inventaire des périmètres et Structure | Réutilisation | Comportements existants, dont #815 ; pas de refonte de l’arbre ni des droits. |
| Listes Profils, Flows, Entrées, Packs, Sources | Reporté | Les nouveaux composants sont disponibles ; pas de promesse de migration intégrale dans cette livraison. |
| Tâches Console, Flow Runs, conversations, plans, artefacts, événements et audit | Reporté | Conserver les composants métier et bornes actuels ; optimisation Flow Runs suivie par #989. |
| Administration : membres, accès, organisation et profil | Reporté | Pas de modification des parcours métier ; les composants communs déjà consommés peuvent recevoir les améliorations du socle. |
| Workplace : accueil, conversation, détails de tâche, notifications | Reporté | Pas de gabarit ressource ni de quatre onglets imposés aux écrans opérationnels. |
| Authentification et compte en Razor Pages | Reporté | Compatibilité des conventions visuelles, sans imposer un composant Blazor à une page Razor. |
| Flow Designer | Conservé | Pas de réécriture de son état ni du mécanisme corrigé par #988. |
| Autres dialogues spécifiques de Pack | Reporté | Le socle modal est disponible ; cette vague vise l’installation et les confirmations partagées. |

## Limites et suites

- Le premier « Utilisé par » réel concerne les profils de modèles et leurs Agents consommateurs. L’API ne prétend pas exposer un graphe générique d’usages Agent/Flow/Entrée ; le jeu de données plus large de la maquette reste illustratif.
- Les filtres Outils portent sur le catalogue déjà chargé, annoncé comme tel. Les collections de modèles utilisent les nouvelles requêtes bornées ; les autres inventaires gardent leurs contrats actuels.
- Les deux apparences utilisent des variables communes. Le catalogue de thèmes nommés, leur sélection persistée, leur distribution et un éditeur de thèmes restent des capacités ultérieures.
- Aucun package de grille ou bibliothèque UX globale n’est ajouté. Une future évaluation spécialisée devra partir d’un besoin concret dépassant `DataGrid` et ses cartes mobiles.
- Les défauts indépendants de l’audit gardent leur suivi propre ; cette matrice ne les présente pas tous comme corrigés.

Voir les [contrats techniques](component-contracts.md), le [plan et les tickets](plan-fr-unification.md) et la [maquette de référence](maquette-ecran-type.md).


## Formulaires complémentaires — #1124

La livraison complémentaire [#1124](https://github.com/gbaudrit/agentstration/issues/1124) étend les champs communs à neuf éditeurs : Secrets, Coffres, fournisseurs d’outils, catégories d’outils, Sources de données, Profils de source de données, Sources de connaissances, Entrées et Schémas. Elle porte sur les champs ordinaires et leurs associations label/aide/validation ; elle conserve les commandes et éditeurs spécialisés de chaque famille.

La [matrice détaillée et les captures](formulaires-complementaires/README.md) distinguent cette adoption des champs d’une refonte complète des fiches. Les listes restent suivies séparément dans #1121 ; les écrans d’administration, les formulaires de registres de Sources et les autres surfaces reportées ci-dessus conservent leur disposition. Le nouveau scénario réutilisable #1125 attend la validation UX explicite et la fusion de la PR d’implémentation.


## Extension à tous les formulaires — #1127

La [matrice complète](formulaires-tous-ecrans/README.md) remplace les reports de familles ci-dessus pour les **champs de formulaire** : administration, Sources/registres, Packs, créateurs contextuels, formulaires dynamiques Workplace, inspecteurs Flow et pages Razor de compte. Les comportements métier et les éditeurs spécialisés sont conservés. Les dispositions propres à chaque famille restent adaptées à son usage ; les listes #1121 restent séparées. La couverture associée #1128 sera une PR empilée en brouillon, conformément à la demande du mainteneur.
