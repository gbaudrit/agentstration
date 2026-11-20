# POC — Changer de périmètre en conservant sa position

Base : branche `codex/ux-corrections`, commit `82206267`. Les captures correspondent au POC ajouté à cette base, exécuté sur le véritable hôte Console avec les fixtures déterministes existantes. Aucun modèle distant n’est appelé par les vérifications de navigation.

## Comportement livré

- Une page de liste présente dans la navigation principale reste ouverte dans le périmètre cible. Les résultats, droits et états locaux sont rechargés ; la pagination repart de la première page.
- Les fiches Agent `/agents/{Name}` et `/namespaces/{Namespace}/agents/{Name}` conservent le chemin, le namespace, le propriétaire exact et l’ancre si la même ressource est accessible dans le périmètre cible. Les liens anciens sans propriétaire sont résolus dans le contexte source avant de changer de contexte.
- Une fiche héritée devient en lecture seule selon les droits existants. Une section devenue inaccessible, comme `#access`, utilise le repli existant vers `#overview` avec son message explicite.
- Un Agent absent, inaccessible ou remplacé par une autre identité conduit à `/agents?context=…`, avec un message et un lien vers la fiche et le contexte précédents. Un Agent homonyme dans le Workspace cible n’est jamais ouvert à sa place.
- Le contexte cible est validé séparément de la ressource. Une erreur réseau, serveur, d’authentification ou un contexte refusé laisse la navigation source intacte et produit une notification d’échec ; elle ne devient pas un état « ressource absente ».
- Le changement passe par `NavigationManager.NavigateTo`. Le garde existant protège les brouillons de définition, YAML, accès et éditeurs masqués. Annuler conserve la page et les saisies ; confirmer abandonne le brouillon avant de charger le nouveau contexte.
- Le corps de page est démonté pendant le chargement d’un contexte différent. Il est recréé avec la nouvelle identité de contexte, afin de ne pas montrer les données et éditeurs du contexte précédent. Les autres onglets restent indépendants.

La résolution utilise les clients et les endpoints existants, avec `X-Agentstration-Context` explicite sur chaque lecture. Elle ne change aucun cookie de contexte et ne modifie aucune ressource. Le serveur reste responsable de l’autorisation et vérifie à nouveau l’accès au chargement effectif de la fiche.

## Limites du POC

La conservation d’une **fiche** est volontairement limitée aux Agents. Les autres fiches, les écrans de création et les sous-routes spécialisées reviennent à la destination correspondante de la navigation principale, ou à l’accueil si aucune correspondance n’existe. Cela évite de réinterpréter leurs identifiants dans un autre Workspace. Les sous-listes absentes de la navigation principale ne disposent pas encore d’une déclaration dédiée.

Le POC conserve la page et sa section ; il ne promet pas une position de défilement identique. Il ne transporte pas les brouillons entre périmètres. Le message de repli appartient au circuit courant, sans persistance après un rechargement complet du document. Si les droits ou la ressource changent entre la vérification préalable et le chargement final, les états bloquants existants de la fiche restent applicables.

## Vérifications

Les 18 cas .NET du POC couvrent la résolution de destination, les identités exactes, les homonymes/remplacements, les erreurs attendues et techniques, la protection des brouillons sans barre visible, le retrait du contenu pendant le chargement et les réponses tardives.

Le smoke ponctuel [smoke.mts](smoke.mts) réutilise les fixtures et les page objects existants. Il vérifie sur Chromium réel : conservation de la fiche/ancre et lecture seule, historique sans rechargement, section inaccessible, homonyme présent dans la liste cible, indépendance d’un autre onglet, retour explicite, annulation/confirmation d’un brouillon, français mobile et absence de débordement horizontal ou d’exception navigateur. Résultats : [verification.json](verification.json).

Ce script est une preuve ponctuelle du POC, pas un nouveau parcours réutilisable du catalogue UX. La généralisation et un éventuel parcours catalogué devront suivre le suivi et la validation UX prévus par le dépôt.

Pour reproduire depuis la racine après installation des dépendances et compilation Release de la solution :

```sh
AGENTSTRATION_PLAYWRIGHT_NO_BUILD=true \
AGENTSTRATION_CHROMIUM_EXECUTABLE=/usr/bin/chromium \
node --import ./automation/playwright/node_modules/tsx/dist/esm/index.mjs \
  docs/ux/evidence/context-switch-poc/smoke.mts
```

Le SDK .NET du dépôt doit être dans `PATH`. Le script démarre puis arrête ses propres hôtes et données locales. Il réécrit ses quatre captures et son fichier de résultats dans ce dossier.

## Validation technique exécutée

- Solution complète, compilation Release : réussite, aucun avertissement ni erreur.
- 18 cas .NET du POC, 2 cas de régression Flow et 5 cas de régression pour la vue d’ensemble et l’état de santé : réussite. Les vérifications ciblées Dashboard/Overview (13 cas) et MainLayout/navigation/état de santé (23 cas) passent intégralement. Les 5 nouveaux cas échouent avant le correctif, puis passent après celui-ci.
- Dernière suite Fast après correction de la vue d’ensemble : 2 493 cas, 2 491 réussites et 2 échecs préexistants (`ConversationListReturnsToThePersistedGenericInteraction` et `LateReviewUnderAChangedOperationalWorkspaceCannotClearNewRows`). Les deux sont déjà reproduits dans un checkout isolé de `82206267` (1 527 cas, 1 525 réussites et ces 2 échecs). L’échec Artifact intermittent de la campagne précédente n’apparaît pas dans cette exécution.
- Suite Integration réexécutée après correction de la vue d’ensemble : 1 570 cas, 1 542 réussites, aucun échec, 28 cas optionnels ignorés sans fournisseurs réels/PostgreSQL configurés.
- Tests JavaScript des composants : 17 réussites. Compilation TypeScript : réussite. Catalogues de localisation anglais/français : XML valide, aucune clé dupliquée et clés symétriques.
- Smoke ponctuel du POC : les huit vérifications de `verification.json` passent, avec les quatre captures ci-dessous.
- Régressions navigateur ciblées : 11 cas réussis lors de la validation initiale, puis 3 cas hiérarchiques réexécutés après le correctif de la vue d’ensemble (bureau, mobile et localisation), tous réussis sur Chromium système. Les traces et vidéos restent activées ; le binaire ffmpeg du système est utilisé dans cet environnement.

La campagne navigateur globale de 494 cas n’est pas exécutée intégralement pour ce POC. Les régressions ciblées couvrent les contextes hiérarchiques, les ressources héritées, l’inventaire des périmètres et les usages inter-périmètres des profils de modèle, sur bureau, mobile et localisation. Leur ancien lien `tab=usages` est remplacé par `#usages` ; la confirmation native de bascule est remplacée par le dialogue du garde partagé.

## Retour utilisateur : accès Flow et langue automatique

L’échec des accès est reproduit après Workspace → Instance → création d’un Flow brouillon. La page transmettait la chaîne littérale `PublishedVersion` à la politique, au lieu de la version publiée calculée. L’API répondait `422 resource_identity_stale`. La liaison Razor est corrigée ; aucune règle d’autorisation ni de validation d’identité serveur n’est assouplie. Les deux nouveaux cas vérifient une version publiée réelle et l’absence de version publiée pour un brouillon.

Le smoke ponctuel [flow-access-smoke.mts](flow-access-smoke.mts), lancé comme `smoke.mts`, crée un Flow par l’interface après la bascule et enregistre Discover/Use avec propagation aux descendants. Il relit la politique enregistrée par l’API. Les deux exécutions, français et anglais automatiques sans cookie de langue, passent sans erreur navigateur : [résultats](flow-access-verification.json).

Le passage involontaire du français à l’anglais n’a pas été reproduit. Le mainteneur a ensuite indiqué qu’il pouvait s’agir d’une fausse alerte ; aucun comportement de langue n’est modifié. Le signalement précise Chrome, lancement Aspire/AppHost, langue automatique, dès Workspace → Instance. Le parcours équivalent reste en français sur l’hôte Console de test ainsi que sur AppHost, en HTTP (`localhost:5100`) puis en HTTPS (`localhost:7100`, certificat local de test). Les préférences restent automatiques et aucun cookie de langue n’est créé. Ces vérifications ne reproduisent pas le signalement ; elles ne prouvent pas sa résolution. Ce point reste suivi dans la PR brouillon #1256.

## Retour utilisateur : vue d’ensemble conservant les anciens indicateurs

Le défaut est reproduit sans F5 : Workspace → Instance conserve 4 Flows dans la vue d’ensemble alors que l’API renvoie les Flows de l’Instance. `PlatformDashboardService` conservait son chargement partagé pendant toute la durée du circuit Blazor. Remonter la page ne suffisait donc pas à recharger les données.

Le chargement partagé est désormais associé au snapshot de contexte validé. Il reste partagé entre l’état de santé du menu et les tuiles de la page dans ce contexte. Chaque contexte nouvellement chargé, y compris un retour au précédent, lance une nouvelle lecture. L’état de santé attend le contexte validé, est réinitialisé pendant son rechargement et ignore les succès ou échecs tardifs d’un chargement précédent.

Le smoke ponctuel [overview-smoke.mts](overview-smoke.mts), lancé comme `smoke.mts`, compare les compteurs Agents/Flows à l’API dans chaque contexte, sur bureau et mobile français, sans rechargement du document. Dans la fixture, il observe 4 Flows en Workspace puis 14 en Instance. Il ouvre ensuite la liste via la tuile, vérifie que l’identité du Flow de l’Instance reste éditable et que ses accès sont modifiables. Une autorisation avec propagation rend un Flow supplémentaire visible en Workspace : le retour affiche bien 5, puis l’Instance revient à 14. L’historique fonctionne aussi. Aucune règle de lecture seule ou d’autorisation serveur n’est modifiée.

Les deux variantes passent sans exception navigateur ni débordement horizontal : [résultats](overview-verification.json), [capture bureau](overview-desktop-instance.png), [capture mobile](overview-mobile-instance.png). Le parcours réutilisable reste différé dans #1255.

## Captures

| Situation | Bureau anglais, 1440 × 1000 | Mobile français, 390 × 844 |
| --- | --- | --- |
| Agent accessible, ancre YAML conservée | [Capture](desktop-en-accessible.png) | [Capture](mobile-fr-accessible.png) |
| Agent inaccessible, retour à la liste et homonyme distinct | [Capture](desktop-en-unavailable.png) | [Capture](mobile-fr-unavailable.png) |
