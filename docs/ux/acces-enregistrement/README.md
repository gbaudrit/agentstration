# Enregistrer une autorisation depuis la sélection

Correction de [PR #1114](https://github.com/gbaudrit/agentstration/pull/1114), en brouillon vers `ux/unification-formulaires`.

## Comportement

Avant cette correction, sélectionner un périmètre ne modifiait pas la liste des autorisations. Il fallait encore cliquer sur « Ajouter une autorisation » avant de pouvoir l’enregistrer.

- **Nouvelle autorisation** ouvre la saisie du périmètre et des droits.
- **Enregistrer les accès** inclut directement cette sélection, en même temps que les changements de la liste. Une étape d’ajout distincte est facultative.
- **Ajouter à la liste** prépare une autorisation sans l’enregistrer, pour pouvoir en saisir plusieurs avant un enregistrement commun.
- **Annuler l’ajout** efface la sélection et ses droits sans modifier les autorisations déjà préparées dans la liste.
- **Annuler les modifications** restaure la dernière politique enregistrée et efface aussi la sélection en cours.

Une aide dans le formulaire explique l’enregistrement direct et l’usage facultatif de la liste. Les libellés et l’aide sont traduits en anglais et en français.

Un périmètre invalide ou une autorisation sans action empêche l’enregistrement. L’enregistrement conserve le contrôle de concurrence ETag, l’identité exacte de la ressource et les règles de descendance existantes. Si l’API refuse l’enregistrement, les changements restent dans la liste locale pour être corrigés, annulés ou réessayés.

## Captures

Application locale en français, avec une sélection valide, puis la même autorisation relue après enregistrement.

| État | Clair bureau | Sombre bureau | Clair mobile | Sombre mobile |
| --- | --- | --- | --- | --- |
| Sélection prête à enregistrer | [Voir](selection-light-1440.png) | [Voir](selection-dark-1440.png) | [Voir](selection-light-390.png) | [Voir](selection-dark-390.png) |
| Autorisation enregistrée | [Voir](enregistre-light-1440.png) | [Voir](enregistre-dark-1440.png) | [Voir](enregistre-light-390.png) | [Voir](enregistre-dark-390.png) |

## Vérification

Les tests de composants couvrent l’enregistrement sans clic sur Ajouter, les libellés anglais/français, la sauvegarde simultanée d’une ligne préparée et d’une sélection, le maintien de l’ETag, les deux annulations et les saisies invalides.

La compilation Release passe sans avertissement ni erreur. Les 401 tests du projet de composants et les 935 tests rapides passent. Le parcours existant de gestion des accès passe avec l’enregistrement direct.

Le contrôle ponctuel dans l’application passe en clair/sombre à 1 440 et 390 px : sélection puis enregistrement direct, relecture après rechargement, ajout en lot, annulations, refus des droits vides et du périmètre propriétaire, absence de débordement horizontal. Une modification concurrente de la politique provoque bien un refus ETag ; les changements préparés restent visibles et la politique du serveur est conservée. Après annulation et rechargement, une nouvelle saisie peut être enregistrée.

Le parcours navigateur existant de gestion des accès utilise désormais directement Enregistrer après la sélection. Aucun nouveau parcours réutilisable n’est créé ; les enfants natifs déjà ouverts restent bloqués jusqu’à validation UX explicite et fusion.
