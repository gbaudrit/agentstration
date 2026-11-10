# YAML, étiquettes et densité des écrans

Correctif de [PR #1114](https://github.com/gbaudrit/agentstration/pull/1114), toujours en brouillon vers `ux/unification-formulaires`. Ce rapport complète le [lot précédent](../structure-ressources/README.md).

## Corrections

- Une nouvelle ligne d’étiquette entièrement vide est un emplacement de saisie : elle ne produit ni erreur immédiate, ni modification de la ressource, ni paire `=`.
- Les lignes partielles ou dupliquées restent dans le composant. Le modèle lié conserve les dernières valeurs valides ; les erreurs du contexte de formulaire bloquent l’enregistrement sans perdre la saisie.
- L’erreur de ligne n’est plus répétée sous le panneau. Le résumé global de validation conserve son rôle dans le formulaire.
- Le YAML reste affichable. Si le formulaire est invalide, la définition enregistrée est présentée en lecture seule avec une explication et un retour à Définition. Corriger les champs rend l’édition et l’enregistrement du YAML disponibles ; consulter le YAML ne supprime pas le brouillon incomplet.
- Le type, le nom et l’état sont sur une même ligne d’en-tête ; les métadonnées et la copie du nom technique occupent une seconde ligne compacte. Les actions gardent leur disposition responsive.
- Le jeton commun `--form-section-gap` passe de 24 à 12 pixels pour les panneaux d’édition et de consultation.

## Captures de l’application

| Vue | Clair | Sombre |
| --- | --- | --- |
| En-tête bureau | [Voir](entete-light-1440.png) | [Voir](entete-dark-1440.png) |
| En-tête mobile | [Voir](entete-light-390.png) | [Voir](entete-dark-390.png) |
| Vue d’ensemble | [Voir](vue-ensemble-light.png) | [Voir](vue-ensemble-dark.png) |
| YAML bureau | [Voir](yaml-light-1440.png) | [Voir](yaml-dark-1440.png) |
| YAML mobile | [Voir](yaml-light-390.png) | [Voir](yaml-dark-390.png) |

[Étiquette vide sans erreur](etiquette-vide-light.png) · [YAML disponible malgré une étiquette incomplète](yaml-etiquette-incomplete-light.png).

Données locales de démonstration et fournisseur simulé. Les captures emploient une hauteur augmentée pour montrer les blocs ; les débordements sont vérifiés séparément à 844 pixels de haut. L’en-tête mesure 82 pixels sur bureau, 129 sur mobile avec ses actions. Les quatre écarts entre panneaux de la vue d’ensemble mesurent chacun 12 pixels, dans les deux thèmes.

## Vérification

- Solution Release : aucune erreur ni avertissement.
- Lane rapide : 926 tests réussis. Le test partagé vérifie les lignes vides/partielles/dupliquées et la préservation de la valeur liée. Un test de régression Console vérifie qu’une étiquette incomplète ne rend jamais la définition Agent non sérialisable en YAML.
- Parcours existants concernés : 4/4 réussis, création d’Agent, nommage sur bureau/mobile et contrats de schéma.
- Vérification dans l’application : ligne vide ignorée, YAML non vide sans erreur technique, ligne partielle conservée au retour, erreur locale unique, lecture seule pendant l’invalidité, édition puis sauvegarde YAML après correction, deux thèmes et deux largeurs, aucun débordement horizontal ni erreur navigateur.
- Lane d’intégration : 1 011 réussites, 12 intégrations optionnelles ignorées, aucun échec.
- Build du site documentaire réussi ; catalogues anglais/français valides et symétriques, sans doublon.

Les nouveaux scénarios réutilisables restent suivis par les enfants existants #1098/#1099/#1100/#1101/#1103, bloqués jusqu’à validation UX explicite et fusion. Cette passe maintient les tests existants et ajoute le test de régression du défaut signalé.
