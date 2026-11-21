# Structure des ressources et actions communes

Ce lot de [PR #1114](https://github.com/gbaudrit/agentstration/pull/1114) prolonge les corrections des champs et de la sélection de modèle. La cible reste `ux/unification-formulaires` ; la PR reste en brouillon, sans validation UX ni fusion. Suivi : [#1090](https://github.com/gbaudrit/agentstration/issues/1090), [#1091](https://github.com/gbaudrit/agentstration/issues/1091), [#1092](https://github.com/gbaudrit/agentstration/issues/1092), [#1093](https://github.com/gbaudrit/agentstration/issues/1093) et [#1095](https://github.com/gbaudrit/agentstration/issues/1095).

Les ajustements suivants de densité et de YAML sont décrits dans le [rapport de correction d’affichage](../corrections-affichage/README.md). Les mesures et résultats ci-dessous décrivent la passe précédente.

## Organisation

L’onglet **Définition** remplace Édition. Sur l’Agent, les panneaux sont **Identité**, **Comportement**, **Configuration du modèle**, **Exécution** et **Étiquettes**. La description rejoint l’identité ; le gestionnaire reste avec les instructions. Le choix de modèle et ses informations restent ensemble, avant le profil d’exécution.

**Vue d’ensemble** rassemble l’identité enregistrée, les informations spécifiques, les étiquettes en badges, la comparaison état souhaité/déploiement actif et la résolution du modèle. Une configuration enregistrée qui attend son application garde un avertissement compact dans Définition. Les confirmations d’action deviennent des notifications temporaires, fermables, indépendantes des panneaux.

Dans **Accès**, un résumé compact indique le propriétaire et la gouvernance. Les autorisations occupent le panneau principal ; les détails techniques restent repliables. L’ajout permet de rechercher un périmètre et de choisir Découvrir, Utiliser et la propagation. L’annulation restaure la dernière politique chargée ou enregistrée.

## Composants et adoption

| Composant | Responsabilité |
| --- | --- |
| `ResourceIdentitySummary` | Consultation de l’identité commune, description vide explicite, périmètre, namespace et badges courts. Adopté sur Agent et Profil de modèle ; complète `ResourceIdentityFields`. |
| `CopyableValue` | Copie du nom technique, avec retour accessible. Utilisé dans l’en-tête Agent et les résumés. |
| `LabelsEditor` / `LabelsSummary` | Édition par lignes clé/valeur, erreurs associées, ajout/suppression ; badges en consultation. Premier usage sur l’Agent, sans changer le format enregistré. |
| `ResourceIcon` | Image ou SVG chargé comme image, variante sombre facultative, icône de catalogue ou contenu fourni par l’appelant ; proportions conservées et repli sur l’icône de famille. Intégré à `ResourceHeader`. |
| `ResourceActionBar` | Action principale, actions secondaires et emplacement facultatif de débordement ; intégré à `ResourceHeader`. Les écrans gardent commandes et permissions. |
| `ResourceAccessPanel` | Composant existant réorganisé pour ses consommateurs, avec recherche, ajout explicite, annulation et sauvegarde. |
| `UnsavedChangesBar` | Surface neutre, Annuler / Enregistrer / Enregistrer et appliquer ; disposition mobile au-dessus de la navigation grâce au jeton `--mobile-navigation-height`. |
| `PageNotification` | Retour d’action fermable pendant huit secondes ; le centre de notifications conserve l’historique de session. |

Le bouton Annuler de l’Agent appelait le chargement dédupliqué de la même ressource : sa garde le faisait sortir immédiatement. Il restaure désormais l’instantané enregistré et remplace le modèle/EditContext et les contrôles, sans changer d’onglet. Les erreurs, valeurs non valides et brouillons YAML sont réinitialisés.

## Thèmes et limites

**Clair** et **Sombre** sont les deux premiers thèmes ; la préférence Système choisit l’un des deux. Les composants utilisent les variables sémantiques communes. Le shell expose le thème résolu par `data-theme="light|dark"`, sans changer l’API de préférences. Les futurs thèmes de marque pourront fournir d’autres palettes.

Le rendu d’icône est prêt pour les logos commerciaux. Leur déclaration persistée, les actifs Bootstrap et l’héritage automatique depuis le type restent dans [FR #1110](https://github.com/gbaudrit/agentstration/issues/1110) : ce lot ne prétend pas les livrer. Aucun logo commercial approximatif n’est ajouté.

La recherche d’accès utilise l’inventaire autorisé existant et rend au plus cinquante options correspondantes, avec leur décompte réel. Elle ne transforme pas l’API d’inventaire en API paginée. Les API, ETags, règles de portée et formats sont inchangés. Il n’y a pas de nouvelle bibliothèque d’interface ni de migration générale de toutes les familles.

## Captures

Application réelle, données locales de démonstration et fournisseur simulé, largeurs de 1 440 et 390 pixels. La hauteur des captures est augmentée pour montrer les panneaux complets ; le débordement est contrôlé séparément à une hauteur de 844 pixels. Les captures de barre d’actions utilisent cette hauteur normale.

| Vue | Clair bureau | Sombre bureau | Clair mobile | Sombre mobile |
| --- | --- | --- | --- | --- |
| Vue d’ensemble | [Voir](overview-light-1440.png) | [Voir](overview-dark-1440.png) | [Voir](overview-light-390.png) | [Voir](overview-dark-390.png) |
| Définition | [Voir](definition-light-1440.png) | [Voir](definition-dark-1440.png) | [Voir](definition-light-390.png) | [Voir](definition-dark-390.png) |
| Accès | [Voir](access-light-1440.png) | [Voir](access-dark-1440.png) | [Voir](access-light-390.png) | [Voir](access-dark-390.png) |
| Modifications en cours | [Voir](actions-light-1440.png) | [Voir](actions-dark-1440.png) | [Voir](actions-light-390.png) | [Voir](actions-dark-390.png) |

[Ajout d’autorisation sur mobile](access-add-light-390.png) et [erreurs d’étiquettes associées aux lignes](labels-invalid-light-1440.png).

## Vérification

- Build Release complet : aucune erreur ni avertissement.
- Lane rapide : 925 tests réussis, dont les nouveaux cas d’étiquettes invalides/dupliquées, de repli d’image, de copie et d’annulation des accès.
- Vérification ponctuelle dans l’application : copie réelle, annulation des valeurs et erreurs sans changement d’onglet, blocage des doublons, enregistrement/relecture des étiquettes, fermeture de notification, recherche/ajout/enregistrement/annulation des accès, deux thèmes et deux largeurs. Aucune erreur navigateur ni débordement horizontal du document.
- TypeScript : compilation réussie. Les parcours existants d’accès ouvrent désormais l’ajout ; le parcours ToolSet consulte la preuve active dans Vue d’ensemble. Le parcours des périmètres capture et vérifie le contexte en une seule lecture avant de construire son URL.
- Catalogues modifiés valides, sans doublon et symétriques anglais/français : 71 clés partagées, 153 clés Agent et 59 clés Accès.
- Lane d’intégration : 1 011 tests réussis et 12 intégrations optionnelles ignorées, aucun échec.
- Campagne navigateur complète : 103 cas, 101 réussites et deux échecs initiaux. Le parcours ToolSet cherchait la preuve active dans l’ancien onglet ; un parcours mobile français de périmètres a envoyé `context=null` après deux lectures pendant l’initialisation. Les Page Objects sont corrigés. Les reprises ciblées passent : 4/4 (ToolSet bureau/mobile/français et Accès bureau), puis 5/5 sur les sorties finales (Accès bureau et Périmètres anglais/français sur bureau/mobile).
- Contrôles complémentaires sur les sorties finales : annulation du brouillon YAML, résumé commun sur Profil de modèle, expiration de notification, effacement du périmètre cible sans repli avec focus conservé, ajout/sauvegarde/retrait d’autorisation sur mobile. À 390 × 844, les trois actions de sauvegarde sont atteignables et la barre reste huit pixels au-dessus de la navigation.
- Build du site documentaire réussi ; différences sans erreur de whitespace. Les navigateurs utilisent le Chromium système, avec vidéo désactivée.

Les scénarios réutilisables restent suivis par les enfants natifs [#1098](https://github.com/gbaudrit/agentstration/issues/1098), [#1099](https://github.com/gbaudrit/agentstration/issues/1099), [#1100](https://github.com/gbaudrit/agentstration/issues/1100), [#1101](https://github.com/gbaudrit/agentstration/issues/1101) et [#1103](https://github.com/gbaudrit/agentstration/issues/1103). Ils restent bloqués jusqu’à validation UX explicite et fusion de la PR corrective ; les tests de composants, la maintenance des parcours existants et les contrôles ponctuels de cette PR ne démarrent pas ces nouveaux scénarios.
