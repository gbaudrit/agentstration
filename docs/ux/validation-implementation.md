# Validation de la livraison UX initiale

Programme [#1089](https://github.com/gbaudrit/agentstration/issues/1089), branche d’intégration `ux/unification-formulaires`. La [matrice d’adoption](adoption-des-composants.md) fixe le périmètre effectivement livré et les familles reportées ; les [contrats](component-contracts.md) décrivent les composants et les points d’extension pour les thèmes.

## Vérifications .NET

Exécution en Release, fournisseur IA déterministe, sans service LLM distant :

| Contrôle | Résultat |
| --- | --- |
| Solution complète, `dotnet build Agentstration.slnx --configuration Release --no-restore` | Réussi, aucune erreur ni avertissement. |
| Lane rapide, seuil 777, parallélisme 4 | 917 réussis, aucun échec ni cas ignoré. |
| Lane d’intégration, seuil 954, parallélisme 2 | 1 023 cas : 1 011 réussis, 12 ignorés, aucun échec. |
| Composants Console après les corrections de cycle de vie | 388 réussis. |

Les cas ignorés correspondent aux intégrations optionnelles, notamment PostgreSQL et les fournisseurs distants, dont les prérequis ne sont pas configurés ici. L’adaptateur PostgreSQL compile ; ce compte rendu ne prétend pas certifier son exécution sur un serveur PostgreSQL réel.

Les tests ajoutés ou réparés couvrent notamment l’effacement avec focus et sans soumission, zéro numérique distinct du vide, énumération obligatoire vide conservée comme saisie invalide, libellés/aides/erreurs associés, absence de compteur inventé, protection des brouillons et navigation après sauvegarde avant le prochain rendu, onglets au clavier et réponses de recherche obsolètes et fermeture du sélecteur pendant son chargement. Les tests API couvrent les recherches au-delà de la première page, les caractères accentués, les curseurs invalides, les homonymes et les droits. Un test de stockage vérifie que la désérialisation reste limitée à la fenêtre demandée.

## Contrôles sur l’application réelle

Les données des captures sont des fixtures locales de revue, sans appel à un modèle distant. Le contrôle ponctuel sur Chromium vérifie :

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
- surcharge des variables de thème : rayon de champ à 18 pixels et couleur d’action modifiée sans modifier le composant.

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

Les parcours existants Agent, administration des modèles, ressources héritées et navigation entre périmètres sont adaptés à la vue d’ensemble par défaut et au choix de modèle inline. Les assertions d’usages parcourent les continuations au lieu de supposer que tous les résultats sont présents dans une page. La compilation TypeScript passe.

Le résultat de la campagne smoke complète est consigné après son exécution.

Le contrôle supplémentaire du catalogue statique comporte deux échecs préexistants : décalage entre quelques routes et leurs entrées, et présence de `trackingIssue: 753` sur une surface marquée couverte. Les déclarations `@page` de tous les fichiers concernés et `application-surfaces.ts` ont été comparées au point de départ `main` (`8a9fafd`) : les entrées de ces deux contrôles sont identiques. Les deux autres contrôles, dont le garde de santé des pages, passent. Ces incohérences du catalogue ne sont pas présentées comme corrigées par cette livraison.

Les nouveaux scénarios réutilisables #1098 à #1106 restent ouverts : [AGENTS.md](../../AGENTS.md#ux-test-scenario-follow-up) exige une validation UX explicite du mainteneur et la fusion de la PR avant leur réalisation. Les FR parentes restent ouvertes jusqu’à ces scénarios ou une dérogation documentée.
