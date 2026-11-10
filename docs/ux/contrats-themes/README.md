# Contrats lisibles dans les deux thèmes

Correction dans [PR #1114](https://github.com/gbaudrit/agentstration/pull/1114), en brouillon vers `ux/unification-formulaires`.

## Correction

Le conteneur de `SchemaBindingEditor` utilisait `--surface-subtle` et `--border-color`, absentes du contrat de thème actuel. Leurs valeurs de repli imposaient un fond clair sous un texte clair en mode sombre.

Le composant partagé par les contrats Agent, Flow et ToolDefinition utilise maintenant les surfaces, bordures et textes du thème actif. Le bouton de source sélectionné utilise aussi la couleur de texte associée à la couleur primaire du thème. Aucun changement des contrats métier ni nouvelle dépendance.

Les colonnes et sélecteurs sont contraints à la largeur disponible. Les noms longs et l’URL du dialecte ne forcent plus un débordement horizontal du panneau sur mobile.

## Captures

Application locale en français, sur bureau (1 440 px) et mobile (390 px). Les captures montrent le contrat d’entrée ; le contrat de sortie est vérifié avec les mêmes états. La hauteur des captures est augmentée pour présenter le panneau complet. Les contrôles de débordement utilisent une hauteur de 844 px.

| État | Clair bureau | Sombre bureau | Clair mobile | Sombre mobile |
| --- | --- | --- | --- | --- |
| Sans contrat | [Voir](vide-light-1440.png) | [Voir](vide-dark-1440.png) | [Voir](vide-light-390.png) | [Voir](vide-dark-390.png) |
| Contrat intégré | [Voir](integre-light-1440.png) | [Voir](integre-dark-1440.png) | [Voir](integre-light-390.png) | [Voir](integre-dark-390.png) |
| Schéma partagé publié | [Voir](partage-light-1440.png) | [Voir](partage-dark-1440.png) | [Voir](partage-light-390.png) | [Voir](partage-dark-390.png) |

## Vérification

Le contrôle ponctuel dans l’application couvre 24 états : entrée/sortie, vide/intégré/partagé, clair/sombre, bureau/mobile. Il vérifie le contraste des titres, des aides et du bouton sélectionné, le fond sombre, la version publiée choisie, la suppression du contrat et l’absence de débordement horizontal.

Les 24 états passent dans la version recompilée, sans erreur JavaScript. Contrastes minimaux mesurés : titres 14,92:1, aides 4,55:1, bouton sélectionné 5,17:1. La compilation Release passe sans avertissement ni erreur ; les 927 tests rapides passent.

Les scénarios réutilisables différés restent bloqués jusqu’à validation UX explicite et fusion. Cette correction utilise les parcours existants et un contrôle ponctuel ; la PR reste en brouillon.
