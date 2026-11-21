# Corrections des champs et de la sélection de modèle

Cette passe part de `ux/unification-formulaires`, après la PR [#1108](https://github.com/gbaudrit/agentstration/pull/1108), et corrige les points signalés sur la fiche Agent. Elle reste proposée dans une nouvelle PR en brouillon, à relire avant validation et fusion. Suivi : [FR champs #1090](https://github.com/gbaudrit/agentstration/issues/1090) et [FR sélection de modèle #1093](https://github.com/gbaudrit/agentstration/issues/1093).

La suite de ce travail est présentée dans le [lot de structure des ressources et actions communes](../structure-ressources/README.md). Les résultats ci-dessous décrivent la première passe.

## Changements proposés

- `FieldClearButton` réunit le bouton d’effacement des textes, nombres, choix et références de modèle. Une icône SVG centrée remplace le caractère typographique. Le bouton garde une cible de taille fixe et un libellé accessible ; l’effacement et le retour du focus restent gérés par le contrôle.
- Les textes, listes et nombres centrent la croix verticalement. Les champs multilignes la placent en haut à droite ; les contrôles natifs gardent leur emplacement à côté de la croix.
- La sélection de modèle utilise une seule carte : identité, périmètre, fournisseur, modèle, état, lien vers le profil et actions regroupées. La carte récapitulative supplémentaire et le lien isolé au milieu du formulaire sont supprimés.
- Le catalogue reste intégré à la page. Les filtres s’alignent sur une ligne à grande largeur, puis se répartissent sur mobile. Les résultats ont un état sélectionné explicite et une zone de défilement ; la pagination apparaît quand elle permet réellement de changer de page.
- Chaque action de choix est associée à la description de son profil. Ouvrir le catalogue place le focus dans la recherche ; choisir un profil le referme et rend le focus au déclencheur.
- Les sections des vues d’édition utilisent l’espacement commun. Les couleurs, rayons et dimensions continuent de dépendre des variables de thème.

Le stockage, les API, les limites de requête et les règles d’autorisation conservent leurs contrats existants. Les composants spécialisés restent dans leur famille.

## Aperçus

Captures de l’application locale avec des données de démonstration et un fournisseur Ollama simulé. Les captures isolent les composants aux largeurs 1 440 et 390 pixels ; la hauteur de capture est augmentée pour montrer le bloc sans les barres fixes. Le débordement du document est également contrôlé avec une hauteur de 844 pixels.

| État | Clair | Sombre |
| --- | --- | --- |
| Sélection — bureau | [Carte](selection-light-1440-collapsed.png) | [Carte](selection-dark-1440-collapsed.png) |
| Catalogue — bureau | [Catalogue](selection-light-1440-expanded.png) | [Catalogue](selection-dark-1440-expanded.png) |
| Sélection — mobile | [Carte](selection-light-390-collapsed.png) | [Carte](selection-dark-390-collapsed.png) |
| Catalogue — mobile | [Catalogue](selection-light-390-expanded.png) | [Catalogue](selection-dark-390-expanded.png) |
| Identité et effacement | [Champs](identite-light.png) | [Champs](identite-dark.png) |

[Écran Agent en sombre](agent-dark.png).

## Vérification

- Solution Release : aucune erreur ni avertissement.
- Lane rapide : 921 tests réussis, dont 392 tests des composants Console. Les deux nouveaux cas vérifient la carte unique, le choix courant, la fermeture et le focus, puis la conservation d’un profil situé hors de la page visible pendant recherche et pagination.
- Lane d’intégration complète : 1 023 cas, 1 010 réussites, 12 intégrations optionnelles ignorées et un échec intermittent déjà observé dans `ProviderPropagatesCallerCancellation` du fournisseur Git : attente d’un type exact `OperationCanceledException`, réception de son sous-type `TaskCanceledException`. Le module Git relancé séparément passe, 8 tests sur 8. Aucun fichier du fournisseur Git n’est modifié dans cette correction.
- Quatre parcours navigateur existants passent sur des instances isolées : création d’Agent, cycle de vie des modèles, nommage sur bureau et nommage sur mobile. Un premier passage sur une instance externe réutilisée a rencontré le conflit attendu sur le nom fixe d’un Agent créé par le passage précédent ; la reprise emploie les fixtures isolées du dépôt.
- Le contrôle visuel ponctuel vérifie la sélection sur un catalogue de plus de vingt profils, le filtrage sans modification du formulaire, l’effacement, le focus, les quatre combinaisons de largeur/apparence et l’absence d’erreur navigateur. Le centre des croix et icônes est aligné sur les champs simples ; listes et nombres gardent 24 pixels avant le bord pour les contrôles natifs. L’ancrage des multilignes reste à quatre pixels du haut.
- Les 37 clés du catalogue de sélection sont valides et symétriques en anglais et en français. Les styles ajoutés utilisent les variables existantes.

Les tests navigateur utilisent le Chromium système avec enregistrement vidéo désactivé. Cette passe rejoue les parcours concernés ; elle ne revendique pas une nouvelle exécution de l’intégralité des 103 cas de la livraison précédente.

Les nouveaux scénarios réutilisables [#1098](https://github.com/gbaudrit/agentstration/issues/1098) et [#1101](https://github.com/gbaudrit/agentstration/issues/1101) restent suivis par leurs FR. La validation UX et la fusion de la PR corrective restent requises avant leur réalisation, conformément aux [instructions du dépôt](https://github.com/gbaudrit/agentstration/blob/ux/unification-formulaires/AGENTS.md#ux-test-scenario-follow-up).
