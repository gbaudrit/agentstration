# Alignement et identité qualifiée dans l’en-tête

Complément de [PR #1114](https://github.com/gbaudrit/agentstration/pull/1114), en brouillon vers `ux/unification-formulaires`.

L’icône de la ressource s’aligne sur le haut du titre, indépendamment de la présence d’une description. Le namespace est regroupé avec le nom technique dans un petit encart avec une icône de dossier, après le périmètre et la génération : `namespace/nom-technique`.

Le bouton copie cette valeur qualifiée complète, sans espaces autour du `/`. L’encart et le bouton exposent la valeur complète dans leur infobulle. Les chemins longs restent sur une ligne avec ellipsis ; la description conserve sa propre ligne et son infobulle. Le composant partagé `ResourceHeader` accepte désormais `Namespace`. Sans ce paramètre, la copie conserve le nom technique seul.

## Captures

Les deux exemples utilisent une description longue, en français, dans l’application locale. Le second possède aussi un namespace et un nom technique longs.

| Exemple | Clair bureau | Sombre bureau | Clair mobile | Sombre mobile |
| --- | --- | --- | --- | --- |
| Nom court, namespace `default` | [Voir](court-light-1440.png) | [Voir](court-dark-1440.png) | [Voir](court-light-390.png) | [Voir](court-dark-390.png) |
| Chemin long | [Voir](long-light-1440.png) | [Voir](long-dark-1440.png) | [Voir](long-light-390.png) | [Voir](long-dark-390.png) |

## Vérification

Le contrôle ponctuel vérifie l’alignement haut de l’icône, le regroupement de l’identité en dernier, le contenu réel du presse-papiers, les infobulles complètes malgré l’ellipsis, les deux thèmes et l’absence de débordement horizontal à 1 440 et 390 px. Un test de composant couvre la copie qualifiée et la compatibilité des appelants sans namespace.

Les huit cas passent sans erreur JavaScript, ainsi que les 64 tests du projet de composants, les 928 tests rapides et les deux parcours existants de nommage sur bureau/mobile. La compilation Release passe sans avertissement ni erreur.

Les nouveaux scénarios réutilisables restent différés dans les enfants natifs existants jusqu’à validation UX explicite et fusion. Cette passe ne valide ni ne fusionne la PR.
