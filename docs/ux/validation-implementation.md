# Validation de la livraison UX initiale

Programme [#1089](https://github.com/gbaudrit/agentstration/issues/1089), branche d’intégration `ux/unification-formulaires`. La [matrice d’adoption](adoption-des-composants.md) fixe le périmètre effectivement livré et les familles reportées ; les [contrats](component-contracts.md) décrivent les composants et les points d’extension pour les thèmes.

PR de livraison : [#1107](https://github.com/gbaudrit/agentstration/pull/1107) et [#1108](https://github.com/gbaudrit/agentstration/pull/1108).

## Vérifications .NET

Exécution en Release, fournisseur IA déterministe, sans service LLM distant :

| Contrôle | Résultat |
| --- | --- |
| Solution complète, `dotnet build Agentstration.slnx --configuration Release --no-restore` | Réussi, aucune erreur ni avertissement. |
| Lane rapide, seuil 777, parallélisme 4 | 919 réussis, aucun échec ni cas ignoré. |
| Lane d’intégration, seuil 954, parallélisme 2 | 1 023 cas : 1 011 réussis, 12 ignorés, aucun échec. |
| Composants Console après les corrections de cycle de vie | 390 réussis. |

Les cas ignorés correspondent aux intégrations optionnelles, notamment PostgreSQL et les fournisseurs distants, dont les prérequis ne sont pas configurés ici. L’adaptateur PostgreSQL compile ; ce compte rendu ne prétend pas certifier son exécution sur un serveur PostgreSQL réel.

Les tests ajoutés ou réparés couvrent notamment l’effacement avec focus et sans soumission, zéro numérique distinct du vide, énumération obligatoire vide conservée comme saisie invalide, libellés/aides/erreurs associés, absence de compteur inventé, protection des brouillons et navigation après sauvegarde avant le prochain rendu, onglets au clavier et réponses de recherche obsolètes, fermeture du sélecteur pendant son chargement, arrêt du chargement du profil après départ de la fiche et recalage d’une page après réduction du catalogue. Les tests API couvrent les recherches au-delà de la première page, les caractères accentués, les curseurs invalides, les homonymes et les droits. Un test de stockage vérifie que la désérialisation reste limitée à la fenêtre demandée.

## Contrôles sur l’application réelle

Les données des captures sont des fixtures locales de revue, sans appel à un modèle distant. Le contrôle ponctuel sur Chromium vérifie :

- navigation des onglets Agent avec flèches et Home, sélection et focus cohérents ;
- croix d’effacement distincte des flèches natives des listes et des champs numériques ;
- vue d’ensemble Agent fondée sur l’état enregistré, distincte du formulaire ;
- brouillon structuré et YAML invalide conservés entre les onglets ; conflit YAML explicite et abandon volontaire ;
- recherche de profil sans modification de la définition, effacement dans le champ avec focus conservé ;
- annulation et confirmation d’une navigation avec modifications ;
- dialogue d’installation de Pack : tabulation contenue, Échap et retour du focus ;
- profil de modèle : énumération vide invalide, brouillon conservé entre vues et sauvegarde confirmée par relecture API ;
- Paramètre : choix obligatoire vidé sans retour au premier choix ;
- création Agent : nom technique dérivé puis actualisé après passage par Contrats ;
- filtre du catalogue Outils et effacement ;
- Workplace : filtre de statut sélectionné puis vidé dans le champ, avec focus conservé ;
- absence de débordement horizontal à 1 440 et 390 pixels, apparences claire et sombre ;
- surcharge des variables de thème : rayons du champ et de la collection à 18 pixels, espacement de collection et de pagination à 24 pixels, couleur d’action modifiée sans modifier les composants.

Ces contrôles ponctuels ne constituent pas de nouveaux parcours réutilisables du catalogue Playwright.

## Captures de l’implémentation

| Vue | Clair | Sombre |
| --- | --- | --- |
| Agent — vue d’ensemble | [Capture](implementation/agent-light.png) | [Capture](implementation/agent-dark.png) |
| Agent — édition, bureau | [Capture](implementation/agent-edit-light-1440.png) | [Capture](implementation/agent-edit-dark-1440.png) |
| Agent — édition, mobile | [Capture](implementation/agent-edit-light-390.png) | [Capture](implementation/agent-edit-dark-390.png) |
| Workplace — tâches, bureau | [Capture](implementation/workplace-light-1440.png) | [Capture](implementation/workplace-dark-1440.png) |
| Workplace — tâches, mobile | [Capture](implementation/workplace-light-390.png) | [Capture](implementation/workplace-dark-390.png) |

La [preuve de surcharge de thème](implementation/theme-override.png) illustre le point d’extension ; elle ne livre pas un sélecteur de thèmes nommés. Les captures de la [maquette](maquette-ecran-type.md) restent séparées des captures produit.

## Régressions navigateur et suites

Les parcours existants Agent, administration des modèles, ressources héritées et navigation entre périmètres sont adaptés à la vue d’ensemble par défaut et au choix de modèle inline. Les assertions d’usages parcourent les continuations au lieu de supposer que tous les résultats sont présents dans une page. La compilation TypeScript passe. Le build Docusaurus complet, la validation des ADR et la validation/génération LikeC4 passent également. Les liens vers les instructions du dépôt et le fichier HTML autonome de la maquette sont explicites pour rester accessibles depuis le site de documentation.

La campagne `npm run test:smoke` a couvert les **103 cas** : 100 réussites au premier passage, puis les trois cas échoués ont été corrigés et ont tous réussi avec `--last-failed`. Les reprises vérifient le libellé obligatoire par son nom accessible, recherchent les outils de la fixture dans le catalogue paginé et attendent la vue d’ensemble enregistrée avant d’ouvrir l’édition après création. Les variantes ciblées des sources (3) et de l’inventaire d’extensions (4) passent également. Après les derniers ajustements, sept parcours supplémentaires de création, de nommage, de lecture seule et de navigation entre périmètres passent sur bureau et mobile.

La campagne utilise le Chromium système et une configuration temporaire qui conserve les projets, langues, tailles, assertions et traces du dépôt ; l’enregistrement vidéo est désactivé, le binaire ffmpeg de Playwright étant indisponible dans l’environnement. La configuration du dépôt n’est pas modifiée.

Le contrôle supplémentaire du catalogue statique comporte deux échecs préexistants : décalage entre quelques routes et leurs entrées, et présence de `trackingIssue: 753` sur une surface marquée couverte. Les déclarations `@page` de tous les fichiers concernés et `application-surfaces.ts` ont été comparées au point de départ `main` (`8a9fafd`) : les entrées de ces deux contrôles sont identiques. Les deux autres contrôles, dont le garde de santé des pages, passent. Ces incohérences du catalogue ne sont pas présentées comme corrigées par cette livraison.

Les nouveaux scénarios réutilisables #1098 à #1106 restent ouverts : [AGENTS.md](https://github.com/gbaudrit/agentstration/blob/ux/unification-formulaires/AGENTS.md#ux-test-scenario-follow-up) exige une validation UX explicite du mainteneur et la fusion de la PR avant leur réalisation. Les FR parentes restent ouvertes jusqu’à ces scénarios ou une dérogation documentée.
