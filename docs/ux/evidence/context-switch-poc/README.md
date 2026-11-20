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

Les 18 nouveaux cas .NET couvrent la résolution de destination, les identités exactes, les homonymes/remplacements, les erreurs attendues et techniques, la protection des brouillons sans barre visible, le retrait du contenu pendant le chargement et les réponses tardives.

Le smoke ponctuel [smoke.mts](smoke.mts) réutilise les fixtures et les page objects existants. Il vérifie sur Chromium réel : conservation de la fiche/ancre et lecture seule, historique sans rechargement, section inaccessible, homonyme présent dans la liste cible, indépendance d’un autre onglet, retour explicite, annulation/confirmation d’un brouillon, français mobile et absence de débordement horizontal ou d’exception navigateur. Résultats : [verification.json](verification.json).

Ce script est une preuve ponctuelle du POC, pas un nouveau parcours réutilisable du catalogue UX. La généralisation et un éventuel parcours catalogué devront suivre le suivi et la validation UX prévus par le dépôt.

Pour reproduire depuis la racine après installation des dépendances et compilation Release de la solution :

```sh
AGENTSTRATION_PLAYWRIGHT_NO_BUILD=true \
AGENTSTRATION_CHROMIUM_EXECUTABLE=/usr/bin/chromium \
node --import ./automation/playwright/node_modules/tsx/dist/loader.mjs \
  docs/ux/evidence/context-switch-poc/smoke.mts
```

Le SDK .NET du dépôt doit être dans `PATH`. Le script démarre puis arrête ses propres hôtes et données locales. Il réécrit ses quatre captures et son fichier de résultats dans ce dossier.

## Validation technique exécutée

- Solution complète, compilation Release : réussite, aucun avertissement ni erreur.
- 18 nouveaux cas .NET du POC : réussite. Les tests de navigation de `MainLayout` passent également (16 cas au total), dont le retour au contexte précédent après un chargement refusé et le blocage du sélecteur pendant un chargement.
- Dernière suite Fast : 2 486 cas, 2 485 réussites et 1 échec préexistant (`ConversationListReturnsToThePersistedGenericInteraction`). Une exécution précédente relève aussi `LateReviewUnderAChangedOperationalWorkspaceCannotClearNewRows`, qui est intermittent. Les deux échecs sont reproduits dans un checkout isolé de `82206267` : le contrôle de base exécute les 1 527 cas de `Console.Components.Tests`, avec 1 525 réussites et ces 2 échecs.
- Suite Integration : 1 570 cas, 1 542 réussites, aucun échec, 28 cas optionnels ignorés sans fournisseurs réels/PostgreSQL configurés. Cette campagne précède les derniers gardes de présentation pendant un chargement et au retour après échec ; ceux-ci sont vérifiés par la dernière suite Fast et le smoke du POC.
- Tests JavaScript des composants : 17 réussites. Compilation TypeScript : réussite. Catalogues de localisation anglais/français : XML valide, aucune clé dupliquée et clés symétriques.
- Smoke ponctuel du POC : les huit vérifications de `verification.json` passent, avec les quatre captures ci-dessous.
- Régressions navigateur ciblées : 11 cas réussis sur Chromium système, en bureau, mobile et localisation. Les traces et vidéos restent activées ; le binaire ffmpeg du système est utilisé dans cet environnement.

La campagne navigateur globale de 494 cas n’est pas exécutée intégralement pour ce POC. Les régressions ciblées couvrent les contextes hiérarchiques, les ressources héritées, l’inventaire des périmètres et les usages inter-périmètres des profils de modèle, sur bureau, mobile et localisation. Leur ancien lien `tab=usages` est remplacé par `#usages` ; la confirmation native de bascule est remplacée par le dialogue du garde partagé.

## Captures

| Situation | Bureau anglais, 1440 × 1000 | Mobile français, 390 × 844 |
| --- | --- | --- |
| Agent accessible, ancre YAML conservée | [Capture](desktop-en-accessible.png) | [Capture](mobile-fr-accessible.png) |
| Agent inaccessible, retour à la liste et homonyme distinct | [Capture](desktop-en-unavailable.png) | [Capture](mobile-fr-unavailable.png) |
