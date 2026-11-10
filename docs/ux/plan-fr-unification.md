# Plan des FR d’unification de l’IHM

État : **issues publiées sous le parent [#1089](https://github.com/gbaudrit/agentstration/issues/1089)**. Branche d’intégration publiée : `ux/unification-formulaires`. Les identifiants `FR-00` à `FR-06` et `T-01` à `T-02` servent uniquement à ce plan ; ce ne sont pas des numéros GitHub. L’ordre ci-dessous exprime des dépendances de réalisation, sans attribuer de priorité ni d’estimation.

Le besoin est de rendre les formulaires et collections prévisibles dans toute l’IHM, à partir des composants existants. Agent est un cas complexe de validation, pas le modèle métier imposé aux autres écrans. La référence est le [catalogue des besoins](unification-des-formulaires.md), complété par l’[analyse transverse](analyse-des-ecrans-transverses.md) et la [maquette interactive](maquette-ecran-type.md).

## Découpage proposé

| Identifiant | Livrable | Résultat attendu | Dépendances de réalisation |
| --- | --- | --- | --- |
| [FR-00](fr/FR-00-resource-administration.md) | Cadrage parent | Règles communes, pilotes et adoption bornée ; suivi des enfants jusqu’à leur validation. | Aucune |
| [T-01](fr/T-01-form-foundations.md) | Socle de formulaire | Champ, section, grille de formulaire, thèmes et états communs, en consolidant l’existant. | Cadrage FR-00 |
| [T-02](fr/T-02-collection-foundations.md) | Socle de collection | Barre de recherche/filtres, pagination et états ; adaptateurs sur les tables/cartes existantes. | Cadrage FR-00 ; conventions T-01 |
| [FR-01](fr/FR-01-clearable-fields.md) | Champs et identité | Effacement dans le champ, validation cohérente ; nom technique près du nom d’affichage, génération existante conservée. Pilotes Paramètre puis Profil de modèle. | T-01 |
| [FR-02](fr/FR-02-safe-editing.md) | Édition et sauvegarde | Modifications réelles, brouillons conservés, navigation protégée et actions de sauvegarde cohérentes. Pilote Agent. | FR-01 |
| [FR-03](fr/FR-03-resource-pages.md) | Gabarit de fiche | Vue d’ensemble distincte de l’Édition, Accès et YAML, avec conservation des onglets métier. Pilotes Profil de modèle puis Agent. | FR-02 |
| [FR-04](fr/FR-04-resource-reference.md) | Choix d’une référence | Sélecteur de profil de modèle intégré à la page, recherchable, filtrable et paginé, sans popup. | FR-01, T-02 |
| [FR-05](fr/FR-05-resource-usages.md) | Liste « Utilisé par » | Consultation filtrée et paginée des consommateurs autorisés. Premier cas réel : Profil de modèle. | FR-03, T-02 ; réutilisation de #898 |
| [FR-06](fr/FR-06-phased-adoption.md) | Adoption par vagues | Formulaires Console supplémentaires, liste Outils, dialogue Pack et filtres Workplace ; matrice explicite des écrans reportés. | Pilotes FR-01 à FR-05 acceptés |

Les deux tâches techniques n’ajoutent pas de parcours produit seules : elles livrent les briques et leur contrat de test. Leur adoption visible se fait dans les FR. Les noms de composants indiquent des responsabilités, pas neuf nouvelles abstractions obligatoires.

## Ordre de réalisation

1. **Stabiliser le socle** : T-01 ; puis FR-01 et T-02 peuvent avancer indépendamment. Valider le formulaire simple Paramètre avant de généraliser.
2. **Valider les cas complexes** : FR-02 puis FR-03 pour l’édition et la fiche ; FR-04 peut avancer en parallèle dès que champs et collections sont disponibles. Le Profil de modèle valide aussi les références et les usages réels.
3. **Traiter les volumes** : FR-05 avec les limites réelles du contrat API. La pagination visuelle d’un tableau ne suffit pas si toutes les données sont téléchargées en arrière-plan.
4. **Étendre par petites PR** : FR-06, famille par famille, avec contrôles clair/sombre et desktop/mobile. Clore FR-00 après les livraisons et leurs scénarios de test, ou exception documentée par le mainteneur.

On ne conditionne pas l’amélioration d’un champ à la migration de toute l’IHM. Le pilote Agent peut se limiter au formulaire et au choix du modèle avant que toutes ses vues ne soient adoptées.

## Règles de produit à conserver dans les FR

- La croix d’effacement est **dans le champ**, accessible au clavier et nommée ; elle laisse le focus sur la saisie. Elle ne supprime ni une ressource ni un secret stocké. Le vide respecte le type du champ : absence numérique ≠ zéro, vide ≠ valeur par défaut implicite.
- Le nom technique reste à proximité du nom d’affichage. Réutiliser la génération, la dérogation manuelle à la création et la stabilité après sauvegarde déjà livrées par #539 ; ne pas redéfinir les règles de nommage.
- Le sélecteur de modèle affiche la sélection courante séparément de la recherche. Filtrer ne modifie pas la ressource. Deux noms identiques dans deux périmètres restent deux références distinctes.
- La vue d’ensemble décrit l’état enregistré ; le formulaire expose le brouillon et sa sauvegarde. La distinction état souhaité/déploiement actif reste propre aux familles concernées.
- « Utilisé par » dispose d’une collection dédiée dans la vue d’ensemble ; l’édition en montre un résumé. Le pilote est Profil de modèle, dont les usages sont déjà exposés ; la maquette Agent n’est pas la preuve d’une API d’usages générique.
- Les quatre vues communes visent les ressources administrables qui possèdent ces capacités. Ne pas inventer YAML ou Accès pour les tâches, conversations ou autres écrans opérationnels. Conserver les droits, routes et onglets supplémentaires existants.
- Les thèmes clair et sombre, le mobile, le focus et les traductions en-US/fr-FR sont des critères de chaque livraison. Le prototype fournit une intention visuelle, pas une spécification qui retire l’édition YAML existante.

## Réutilisation et chevauchements identifiés

Recherche effectuée dans les issues ouvertes et fermées le 9 novembre 2026. L’audit du code porte sur `8a9fafd7432e28c7aab23109c43593cc90b35c66`. Les issues distantes peuvent décrire des travaux plus récents ou des branches d’intégration : vérifier leur état et les fusions avant chaque implémentation et avant publication, sans supposer que tout est déjà sur cette branche.

| Existant | État consulté | Traitement dans ce plan |
| --- | --- | --- |
| [#539 — génération du nom technique](https://github.com/gbaudrit/agentstration/issues/539), [#578 — scénario associé](https://github.com/gbaudrit/agentstration/issues/578) | Fermés | Réutiliser `ResourceIdentityFields` et les régressions existantes ; nouveau besoin : composition et effacement communs. |
| [#795 — onglet Accès réutilisable](https://github.com/gbaudrit/agentstration/issues/795) | Fermé | Composer le panneau existant dans FR-03 ; ne pas recréer l’administration des droits. |
| [#898 — usages des profils de modèles](https://github.com/gbaudrit/agentstration/issues/898) | Fermé | FR-05 améliore la présentation et le traitement borné ; conserver l’énumération autorisée et les avertissements d’impact. |
| [#509 — création contextuelle Paramètre/Secret](https://github.com/gbaudrit/agentstration/issues/509) | Fermé | Réutiliser ces éditeurs quand le métier les propose ; pas de moteur de création générique. |
| [#815 — filtres de l’inventaire de périmètre](https://github.com/gbaudrit/agentstration/issues/815) | Fermé | Exemple de filtres et compteurs à consolider dans T-02 ; pas de nouvelle refonte de cet écran. |
| [#988 — sauvegarde concurrente du Flow Designer](https://github.com/gbaudrit/agentstration/issues/988) | Fermé | Conserver sa correction ; FR-02 vise les formulaires ordinaires, pas l’état interne du Designer. |
| [#1051 — valeurs liées typées](https://github.com/gbaudrit/agentstration/issues/1051), [#1054 — littéraux guidés par schéma](https://github.com/gbaudrit/agentstration/issues/1054) | Ouverts | Gardent la responsabilité des liaisons et de la sémantique des littéraux ; FR-01/04 ne les remplacent pas. |
| [#1026 — noms qualifiés](https://github.com/gbaudrit/agentstration/issues/1026) | Ouvert | Garde les conventions d’origine et de qualification ; les composants affichent les coordonnées existantes. |
| [#989 — chargement borné des Flow Runs](https://github.com/gbaudrit/agentstration/issues/989) | Ouvert | Garde l’optimisation API/agrégats ; T-02 fournit la présentation, sans réimplémenter ce chantier. |
| [#215 — résilience des API séparées](https://github.com/gbaudrit/agentstration/issues/215) | Ouvert | Réutiliser les erreurs sûres et états disponibles ; FR-02 ne redéfinit ni session ni BFF. |

## Préparer plusieurs thèmes

À terme, l’IHM doit pouvoir proposer des thèmes nommés. Le socle T-01 sépare cette identité visuelle du choix d’apparence clair/sombre/système existant. Les composants consomment des variables sémantiques pour les surfaces, textes, bordures, focus, validation, espacements et rayons. Un thème pourra les redéfinir sans changer les formulaires.

La première livraison inclut les contrats, leur documentation et une surcharge de test démontrant cette extensibilité. Elle conserve le contrat de préférence actuel ; le catalogue de thèmes, leur sélection persistée, leur distribution et un éventuel éditeur visuel feront l’objet de lots ultérieurs.

## Composant externe pour les grilles

Le plan **ne nécessite pas de bibliothèque UX globale**. T-02 commence par `DataGrid`, les cartes mobiles d’Outils et les filtres existants. QuickGrid, MudBlazor et Radzen restent des candidats à une comparaison ciblée si un besoin mesuré de tri, sélection ou virtualisation dépasse ce socle. La décision documentera accessibilité, thème, licence, maintenance, rendu serveur et pagination API. Pas de migration générale liée à cette évaluation.

## Publication et validation

Les neuf descriptions respectent les formulaires du dépôt, avec titres et corps en anglais. Les numéros publiés et les relations natives vérifiées sont consignés ci-dessous. Les FR utilisent `enhancement`, les tâches de socle `technical-task`, avec `triage:agent` et `triage:pending-review` pour la classification proposée ; aucune priorité n’est attribuée.

La publication a créé FR-00, puis attaché les huit lots par les **Sub-issues natives GitHub** avec leurs dépendances documentées séparément. Les liens textuels ne remplacent pas cette relation. FR-00 suit les scénarios des lots, sans imposer un scénario parent qui dupliquerait tous les parcours.

Pour chaque FR qui change un parcours, créer après son numéro et avant sa PR UX un enfant distinct `technical-task` + `test-scenario-task`. Les sections « UX test scenario follow-up » en donnent déjà les routes, fixtures, actions, succès et échecs attendus. Le nouveau scénario Playwright réutilisable attend la validation UX explicite du mainteneur et la fusion de la PR, conformément à [AGENTS.md](https://github.com/gbaudrit/agentstration/blob/ux/unification-formulaires/AGENTS.md). Cette attente ne concerne pas les tests de comportement, la réparation des régressions existantes ni les smoke tests requis dans la PR UX.

Vérifier le catalogue [application-surfaces.ts](../../automation/playwright/src/coverage/application-surfaces.ts) avant de conclure qu’un parcours existant suffit. Une route déclarée couverte ne prouve pas que les nouveaux comportements le sont. La clôture des parents attend les enfants ou une dérogation documentée.

Chaque PR produit inclut la validation ciblée, les smoke tests clavier/desktop/mobile et clair/sombre, les traductions et les deux lanes fonctionnelles hors ligne selon les commandes et seuils du dépôt au moment de la livraison. Aucun service LLM externe ne devient nécessaire. Les changements d’API de pagination ajoutent leurs tests de contrat et d’isolation.

Les défauts ponctuels recensés dans l’audit restent à qualifier avec le formulaire Bug s’ils ne sont pas résolus directement par l’adoption d’un composant ; ce plan ne les présente pas comme de nouvelles capacités. Les écrans non retenus dans les vagues initiales restent explicitement reportés dans FR-06.

## Tickets publiés

| Lot | Issue |
| --- | --- |
| FR-00 | [#1089](https://github.com/gbaudrit/agentstration/issues/1089) |
| FR-01 | [#1090](https://github.com/gbaudrit/agentstration/issues/1090) |
| FR-02 | [#1091](https://github.com/gbaudrit/agentstration/issues/1091) |
| FR-03 | [#1092](https://github.com/gbaudrit/agentstration/issues/1092) |
| FR-04 | [#1093](https://github.com/gbaudrit/agentstration/issues/1093) |
| FR-05 | [#1094](https://github.com/gbaudrit/agentstration/issues/1094) |
| FR-06 | [#1095](https://github.com/gbaudrit/agentstration/issues/1095) |
| T-01 | [#1096](https://github.com/gbaudrit/agentstration/issues/1096) |
| T-02 | [#1097](https://github.com/gbaudrit/agentstration/issues/1097) |

Les relations natives parent/enfant sont vérifiées. Les scénarios #1098 à #1106 sont rattachés à leurs FR et attendent validation UX et fusion.

## Livraison initiale

Socle : [PR #1107](https://github.com/gbaudrit/agentstration/pull/1107). Écrans pilotes et validation : [PR #1108](https://github.com/gbaudrit/agentstration/pull/1108).

Le socle et les écrans pilotes sont implémentés sur la branche d’intégration. Consulter la [matrice d’adoption et des reports](adoption-des-composants.md), les [contrats des composants et des thèmes](component-contracts.md) et le [compte rendu de validation avec captures](validation-implementation.md). Les thèmes nommés restent une capacité future, distincte de l’apparence clair/sombre/système déjà disponible. Les FR restent ouvertes pour le suivi des scénarios et de la validation UX.
