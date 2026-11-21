# État des lieux UX de la fiche agent

Cette étude de cas alimente l’[état des lieux global des composants de formulaire](unification-des-formulaires.md). La fiche agent illustre les besoins de l’IHM ; elle ne définit pas à elle seule le périmètre de l’unification.

La fiche agent présente une base visuelle cohérente, mais son organisation privilégie les détails techniques avant la tâche de configuration. Les premiers correctifs doivent protéger les saisies et fiabiliser l’indication des modifications. Viennent ensuite la clarification des actions et la réduction de la charge de lecture.

## Périmètre et méthode

Évaluation du 9 novembre 2026, sur `main` au commit `8a9fafd7432e28c7aab23109c43593cc90b35c66`. Travail poursuivi dans la branche `ux/unification-formulaires`.

Point de départ : capture fournie de l’agent « Agent d’accueil », à l’état prêt. L’examen porte sur la création et l’édition d’un agent dans la Console, le sélecteur de modèle, les vues formulaire et YAML, et l’accès à l’exécution. Il constitue une première tranche de l’état des lieux produit ; les Flows, Packs, Resource Plans et parcours Workplace restent à examiner.

Méthode : lecture de l’implémentation et inspection dans Chromium de l’application locale compilée avec le SDK .NET 10.0.300, bootstrap Development, interface française, aux dimensions 1652 × 1211 et 390 × 844. La fiche existante utilise des ressources de test locales au périmètre Instance, avec un fournisseur indisponible. L’exécution d’un modèle et un déploiement réussi ne sont pas validés ; l’état prêt est examiné à partir de la capture et du code. Aucun test utilisateur n’a été réalisé : les effets sur la compréhension sont des hypothèses ergonomiques.

## Points à conserver

- Nom de l’agent, statut et action d’exécution immédiatement repérables.
- Distinction explicite entre définition enregistrée et génération active, utile à l’exploitation.
- Sections de formulaire identifiées, labels visibles et aides sur les identifiants.
- Séparation entre enregistrement du brouillon et application au runtime.
- Accès au profil de modèle et au fournisseur pour approfondir un diagnostic.
- Passage en une colonne sur mobile ; aucun débordement horizontal sur les pages de création et d’édition valides testées à 390 px.

## Constats prioritaires

L’ordre proposé exprime une priorité UX pour cette revue, pas une classification d’incidents ni un engagement de réalisation.

| Réf. | Constat et preuve | Conséquence | Résultat attendu |
| --- | --- | --- | --- |
| UX01 | **Perte des modifications lors d’une navigation.** Modifier le nom d’affichage, cliquer sur « Exécuter », puis revenir : le nom initial réapparaît, sans dialogue de confirmation. Reproduit dans Chromium ; aucune protection des saisies dans l’éditeur [S1]. | Une action courante peut effacer le travail en cours. | Avant de quitter une fiche modifiée, permettre de rester ou d’abandonner explicitement. Couvrir navigation interne, retour et fermeture/rechargement. |
| UX02 | **Perte du YAML non appliqué.** Modifier le YAML, ouvrir « Définition », puis revenir sur « YAML » : la saisie disparaît. Reproduit ; `ShowSource` régénère le texte depuis le modèle du formulaire [S1]. | L’utilisateur peut perdre une configuration avant de l’avoir enregistrée. | Conserver le texte entre les vues ou demander une décision avant son remplacement. Distinguer application au formulaire, enregistrement et déploiement. |
| UX03 | **Faux signal de modification.** Saisir une recherche dans le sélecteur de modèle fait apparaître la barre de modifications sans changer le profil sélectionné. Reproduit ; `MarkDirty` écoute tous les événements du formulaire, dont la recherche [S1, S2]. | L’utilisateur est invité à enregistrer une modification inexistante ; la réconciliation est également désactivée par `isDirty`. | Une recherche ou un filtre ne change pas l’état de la définition. Modifier puis rétablir une valeur devrait aussi être évalué par comparaison avec la version enregistrée. |
| UX04 | **Français incomplet sur une information décisive.** La barre affiche « Unsaved changes » et son explication en anglais, avec des boutons français. Reproduit ; les valeurs par défaut de `UnsavedChangesBar` ne sont pas localisées [S3]. | La distinction entre sauvegarde et application devient moins accessible au moment de décider. | Localiser titre et explication en français et en anglais, conformément aux règles du dépôt. |
| UX05 | **Deux destinations annoncées pour une même page.** « Exécuter » et « Exécutions » pointent tous deux vers `/agents/{Name}/run`. Confirmé dans le DOM et le code [S1]. | Un lecteur cherchant l’historique arrive sur l’outil d’exécution. | Donner des destinations et libellés distincts à l’essai d’un agent et à son historique. |

## Lisibilité et compréhension

| Réf. | Constat | Effet probable et piste proposée |
| --- | --- | --- |
| UX06 | **Le comportement arrive tard.** Dans la capture, les instructions ne sont pas visibles sur les 1211 premiers pixels. L’identité, l’état souhaité/actif, la recherche et le résumé du modèle les précèdent. L’ordre est confirmé dans l’éditeur [S1]. | Pour configurer l’agent, le lecteur doit parcourir beaucoup d’informations avant d’atteindre son rôle. Présenter d’abord nom, description et instructions ; regrouper modèle, exécution et paramètres avancés ensuite. |
| UX07 | **Un état sain occupe beaucoup de place.** La capture répète « Génération 1 » dans l’en-tête, les deux colonnes d’état et la section identité. Le composant conserve les deux colonnes même lorsque les états correspondent [S4]. | La surveillance technique prend le dessus sur l’édition. Proposer un résumé compact « À jour · Génération 1 », avec détails accessibles ; développer la comparaison en cas d’écart. |
| UX08 | **Les actions parlent surtout le langage de l’architecture.** « Réconcilier le déploiement », « Appliquer la génération », « Enregistrer et appliquer » et « Enregistrer le brouillon » coexistent [S1]. | Leur différence demande une connaissance préalable du produit. Tester des libellés qui explicitent l’effet : enregistrer la définition, déployer cette version, tester l’agent. Préserver la distinction réelle entre ces opérations. |
| UX09 | **Des rubriques semblent disponibles sans l’être.** « Déploiements », « Métriques » et « Événements » sont des `span` grisés dans la navigation, sans explication [S1]. | La raison de l’indisponibilité reste ambiguë : fonctionnalité future, droits ou absence de données. Masquer les rubriques non implémentées ou afficher une explication explicite et accessible. |
| UX10 | **Le choix du modèle répète beaucoup de détails.** Recherche, option longue, carte du profil choisi, puis configuration déclarée et résolue présentent des informations communes [S1, S2]. Sur une installation neuve, le message « Créez un profil de modèle » n’offre pas de lien direct. | Simplifier le choix courant autour du nom, du modèle et de sa disponibilité ; réserver périmètre, namespace et résolution au détail. Prévoir une action de création autorisée qui conserve la saisie de l’agent. |
| UX11 | **L’identité mêle champs métier et techniques.** Le gestionnaire `prompt-agent`, le périmètre, le nom technique et l’UID sont présentés très tôt. Nom technique et périmètre sont verrouillés à l’édition [S1]. | Conserver le nom technique auprès du nom d’affichage : sa génération automatique à la création doit être visible. Présenter clairement son immutabilité après création ; déplacer le gestionnaire et les informations d’exploitation secondaires dans une zone avancée avec une explication. |

## Accessibilité à traiter

- **Contrastes.** Les couleurs du thème clair donnent environ 4,06:1 pour le blanc sur le bleu primaire `#3678f6`, 3,10:1 pour le vert de succès `#28a66f` sur blanc, et 3,02:1 pour le placeholder `#8492a6` sur `#f8fafd` [S5]. Ces combinaisons sont sous le seuil WCAG AA de 4,5:1 pour du texte courant. Les mesures concernent ces paires de couleurs ; vérifier chaque état rendu, les fonds composés et le thème sombre avant de conclure sur la conformité globale.
- **État de navigation.** Les boutons de la fiche exposent leur sélection par une classe CSS, sans attribut de sélection dans le DOM inspecté. Choisir une sémantique cohérente pour les changements de panneau et les liens, puis vérifier l’annonce du panneau actif au lecteur d’écran [S1, S6].
- **Taille des aides.** Plusieurs aides et métadonnées utilisent `.72rem`, soit environ 11,5 px avec une racine à 16 px [S5]. Tester lecture et zoom à 200 %, sans considérer la petite taille seule comme une preuve de non-conformité.
- **Mobile.** Le repli en une colonne fonctionne sur les deux pages valides inspectées. La navigation des onglets nécessite un défilement horizontal. Restent à vérifier le clavier virtuel, les actions proches de la navigation basse, les noms longs, les alertes et les erreurs.

## Parcours vérifiés

| Vérification locale | Observation |
| --- | --- |
| Connexion Development puis `/agents/new`, français | Formulaire accessible ; modèle absent sur instance neuve, runtime intégré proposé. |
| Création et édition, 1652 × 1211 puis 390 × 844 | Captures inspectées ; largeur du document égale à 390 px sur les deux pages mobiles valides. |
| Recherche de profil sans nouvelle sélection | Barre de modifications déclenchée ; texte explicatif anglais. |
| YAML modifié puis aller-retour vers Définition | Saisie YAML non appliquée perdue. |
| Nom modifié puis navigation vers Exécuter et retour | Aucune confirmation ; modification non enregistrée perdue. |
| Liens et rubriques de la navigation | Destination identique pour Exécuter et Exécutions ; trois rubriques non interactives. |

Les scripts d’inspection ponctuels ne constituent pas une nouvelle suite de régression. Aucun comportement applicatif n’est modifié par cet état des lieux.

## Ordre de travail proposé

1. **Conserver les saisies** : traiter UX01 et UX02, avec des vérifications de navigation et de bascule YAML/formulaire.
2. **Fiabiliser le retour d’état** : traiter UX03 et UX04, puis distinguer clairement définition modifiée, enregistrée et déployée.
3. **Clarifier les destinations** : corriger UX05 et UX09 ; tester les libellés d’actions avec des utilisateurs de la Console.
4. **Alléger la fiche** : rapprocher les instructions du début, compacter l’état synchronisé et réduire les répétitions du modèle.
5. **Valider l’accessibilité et élargir l’audit** : clavier, lecteur d’écran, zoom, thèmes ; puis parcours liste → fiche → essai → historique, agents gérés par Pack, Flows et Workplace.

Pour les futures modifications de parcours, appliquer le suivi de validation UX et de scénarios décrit dans `AGENTS.md`. Cette revue ne crée ni issue ni engagement de refonte.

## Sources de l’implémentation

- [S1 — AgentEditor, structure, navigation et sauvegarde](https://github.com/gbaudrit/agentstration/blob/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Console.Components/Components/Pages/AgentEditor.razor)
- [S2 — ModelProfilePicker](https://github.com/gbaudrit/agentstration/blob/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Console.Components/Components/ModelProfiles/ModelProfilePicker.razor)
- [S3 — UnsavedChangesBar](https://github.com/gbaudrit/agentstration/blob/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Web.Components/UnsavedChangesBar.razor)
- [S4 — StateComparison](https://github.com/gbaudrit/agentstration/blob/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Web.Components/StateComparison.razor)
- [S5 — Couleurs, typographie et adaptations](https://github.com/gbaudrit/agentstration/blob/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Web.Components/wwwroot/design-tokens.css)
- [S6 — ResourceTabs](https://github.com/gbaudrit/agentstration/blob/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Web.Components/ResourceTabs.razor)
