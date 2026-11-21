# POC — Changer de périmètre en conservant sa position

La branche `codex/poc-context-switch`, initialement issue de `codex/ux-corrections` (`82206267`, puis `7ba891ad`), est rebasée sur `main` (`51969396`) après la fusion squash de la PR #1238. Ses cinq commits propres conservent les nouvelles présentations et la navigation vers les collections déjà intégrées à `main`. La PR #1256 cible désormais `main` ; la FR #1254 et le sous-ticket natif #1255 suivent le POC et son parcours réutilisable différé.

## Mécanisme commun aux ressources

Le switch conserve le chemin, le namespace, les paramètres utiles et l’ancre, puis change le `context` administratif. Il n’existe plus de liste de types pris en charge dans le résolveur, ni de lecture préalable de la ressource par celui-ci. La validation du contexte cible reste distincte, avant d’abandonner une saisie.

`ConsoleResourceDetailsTemplate` enregistre l’identité **déjà chargée** : UID, propriétaire réel, namespace, kind et nom technique. Le switch épingle ce propriétaire dans `scopeRef`, y compris pour les anciens liens sans propriétaire explicite. Il ne remplace jamais le propriétaire par le contexte cible ou par le périmètre présenté visuellement. Le sélecteur attend l’identité d’une fiche en cours de chargement ; une propriété non résolue n’est pas déduite du contexte cible.

La page cible utilise ses lectures et contrôles d’accès habituels. Le composant commun observe leur résultat :

- même identité autorisée : la fiche et son ancre restent ouvertes ; les droits de lecture seule, actions et sections sont recalculés par les règles existantes ;
- section devenue indisponible, comme Accès sur une ressource héritée : le composant de navigation choisit la section autorisée et affiche son retour explicite ;
- ressource absente ou interdite, ou identité remplacée : le cadre ne rend pas son contenu et revient à la liste, avec un message et un lien vers la fiche et le contexte précédents ; un homonyme du Workspace peut apparaître dans la liste mais n’est pas ouvert à la place de l’original ;
- erreur technique de chargement : l’état d’erreur et la commande de nouvel essai de la page restent applicables ; l’erreur ne devient pas une absence de ressource ;
- navigation plus récente : une réponse tardive de l’ancienne fiche ne déclenche pas de repli sur la nouvelle page.

Le mécanisme est partagé par les écrans utilisant le template Console. Chaque famille conserve la responsabilité de ses API, de son chargement et de son autorisation. Le support d’une nouvelle famille ne demande aucun cas supplémentaire dans le switch.

## Navigation, brouillons et contexte

La navigation passe par Blazor et par le garde existant pour les brouillons de définition, YAML, accès et éditeurs masqués. Annuler conserve la page, le contexte et les saisies. Confirmer abandonne le brouillon avant de charger le contexte cible ; aucun brouillon n’est transféré entre périmètres.

Le corps de page est retiré pendant le remplacement du contexte et recréé pour le contexte validé. Les API reçoivent le `X-Agentstration-Context` de l’URL. Aucun cookie de contexte n’est modifié ; les autres onglets restent indépendants. Les pages sans identité de ressource conservent également leur emplacement et rechargent leur état. Le POC ne promet pas la position exacte de défilement ni la persistance des notifications après un rechargement complet.

## Correctifs issus des essais

### Vue d’ensemble

Le défaut conservait les mêmes compteurs après Workspace → Instance alors que les API renvoyaient des valeurs différentes. Le chargement partagé du tableau de bord était conservé pendant toute la durée du circuit Blazor. Il est désormais associé au snapshot de contexte validé. Un retour à un contexte lance une nouvelle lecture ; les tuiles et l’état de santé du menu partagent cette lecture. L’état de santé est réinitialisé pendant son chargement et ignore les succès ou erreurs tardifs d’un contexte précédent.

Le smoke observe 4 Flows en Workspace puis 14 en Instance. Après une autorisation de découverte avec propagation, le retour en Workspace affiche 5, puis l’Instance revient à 14. Les compteurs Agents/Flows sont comparés à l’API à chaque étape, sur bureau et mobile, avec historique et ouverture d’un Flow éditable depuis la tuile, sans F5.

### Accès aux Flows

L’écran envoyait la chaîne littérale `PublishedVersion` au lieu de la valeur calculée, ce qui provoquait `422 resource_identity_stale`. La liaison Razor est corrigée, sans assouplir l’autorisation ou la validation d’identité serveur. Les cas brouillon et publié sont vérifiés.

La synchronisation avec la branche mère conserve ses nouveaux en-têtes et exports complets de ressources. Le Designer de version publiée conserve séparément son source YAML de graphe, vérifié par un test de lecture et de désérialisation.

### Langue

Le changement involontaire vers l’anglais n’est pas reproduit en langue automatique sur Chromium, ni sous AppHost en HTTP/HTTPS sur les ports 5100/7100. Le mainteneur indique qu’il peut s’agir d’une fausse alerte. Aucun comportement de langue n’est modifié.

## Preuves exécutables

Ces scripts sont des smokes ponctuels, pas les nouveaux parcours réutilisables du catalogue UX :

- [Ressources de plusieurs familles](resource-switch-smoke.mts) : Agent, Flow et ModelProfile restent ouverts dans un Workspace avec le propriétaire Instance épinglé ; lecture seule, accès Discover seul, repli de section, retour au propriétaire, ressource révoquée, homonyme distinct, retour explicite et Flow mobile. [Résultats](resource-switch-verification.json).
- [Vue d’ensemble](overview-smoke.mts) : compteurs, API, aller-retour, visibilité modifiée, historique et Flow éditable sur bureau/mobile. [Résultats](overview-verification.json).
- [Brouillon Flow et sauvegarde des accès](flow-access-smoke.mts) : parcours de création puis Discover/Use avec descendants, relecture de la politique, français et anglais automatiques. [Résultats](flow-access-verification.json).
- [Navigation Agent et brouillons](smoke.mts) : identité exacte/ancre, lecture seule, historique, section interdite, homonyme, autre onglet, retour, annulation/confirmation, français mobile et absence d’exception/débordement. [Résultats](verification.json).

Depuis la racine, avec les dépendances installées et la solution compilée en Release :

```sh
AGENTSTRATION_PLAYWRIGHT_NO_BUILD=true \
AGENTSTRATION_CHROMIUM_EXECUTABLE=/usr/bin/chromium \
node --import ./automation/playwright/node_modules/tsx/dist/esm/index.mjs \
  docs/ux/evidence/context-switch-poc/resource-switch-smoke.mts
```

Le SDK .NET du dépôt doit être dans `PATH`. Remplacer le nom du script pour exécuter un autre smoke. Chaque script démarre et arrête ses propres hôtes et données locales ; aucun modèle distant n’est appelé.

## Validation technique

- Compilation Release complète : aucun avertissement ni erreur.
- Après rebase sur `main` (`51969396`) : compilation Release complète sans avertissement ni erreur ; les 2 641 tests Fast réussissent. Les anciens échecs disparaissent avec les corrections de la branche mère, sans correction supplémentaire hors du POC. [Historique et nouvelle référence](baseline-failures.json).
- Vérifications ciblées de la résolution des conflits : 78 tests de détail Flow et le cas du Designer publié avec désérialisation de son YAML réussissent. Les derniers groupes ciblés avant rebase comptaient 276 réussites ; la suite Fast complète est au vert après rebase ; le résultat actualisé de la suite Integration est enregistré dans la PR #1256.
- Integration après correction des accès Tenant, avant rebase : 1 575 cas, 1 547 réussites, aucun échec, 28 cas optionnels ignorés.
- Après rebase, le smoke Déclencheur/Tenant produit 4 enregistrements de résultats réussis ; les 8 vérifications Agent/brouillons, les 2 variantes de la vue d’ensemble et les 7 régressions existantes `hierarchical-context`/`operational-triggers`/`model-profile-cross-scope` réussissent, sur bureau/mobile et français/anglais. Les liens de fiche supplémentaires de `main` demandent seulement de préciser le lien de nom de ressource dans le smoke ponctuel de la vue d’ensemble. Les preuves multi-familles antérieures restent disponibles. La campagne globale de 494 cas n’est pas exécutée intégralement.
- JavaScript : 17 réussites. TypeScript et XML/symétrie des clés de traduction réussissent. La suite PostgreSQL réelle n’est pas activée.

## Captures

- Flow hérité conservé : [bureau](flow-workspace-retained.png), [mobile](flow-mobile-retained.png).
- Vue d’ensemble Instance : [bureau](overview-desktop-instance.png), [mobile](overview-mobile-instance.png).
- Agent accessible : [bureau anglais](desktop-en-accessible.png), [mobile français](mobile-fr-accessible.png).
- Agent indisponible : [bureau anglais](desktop-en-unavailable.png), [mobile français](mobile-fr-unavailable.png).

## Suivi — Déclencheurs, accès Tenant et bandeau

Les liens de la collection Déclencheurs utilisaient le périmètre du Flow cible comme propriétaire du déclencheur. Pour un déclencheur d’Instance conservé dans son Workspace opérationnel, le résolveur commun détectait donc une identité contradictoire et refusait la bascule. Les nouveaux liens portent le propriétaire physique enregistré ; les anciens liens utilisant le périmètre logique du Flow sont normalisés après une lecture autorisée du déclencheur. Un propriétaire physique explicite différent est refusé, même en présence d’un homonyme. Le résolveur commun ne comporte aucun cas Déclencheur. L’accès partagé au Flow ne partage pas le déclencheur ; dans ce cas, le passage au Tenant affiche normalement la collection.

Le filtre de lecture de l’administration Tenant écartait les ressources Instance avant l’évaluation de leurs autorisations. Les lectures communes considèrent maintenant cet ancêtre puis appliquent la politique existante. Fournisseurs et profils de modèle sont accessibles depuis le Tenant lorsqu’une autorisation de découverte le permet ; la propagation reste nécessaire pour ses Workspaces. Les inventaires techniques et les écritures conservent leur contrôle de propriétaire. Cette précision du modèle est soumise à revue dans [ADR-0239](../../../decisions/0239-tenant-administration-discovers-explicitly-shared-ancestors.md) ; l’ADR-0184 accepté n’est pas réécrit.

Le bandeau distingue désormais un titre, une explication de la bascule réussie et un bouton de retour avec icône. Il s’adapte au mobile et aux couleurs du thème. Les textes et leurs tests sont disponibles en français et en anglais.

[Smoke ponctuel Déclencheur/Tenant](trigger-tenant-smoke.mts) : création d’un déclencheur Instance, accès du Flow propagé depuis le Tenant, lien depuis la collection et ancien lien, bascules répétées, fournisseurs/profils avec et sans propagation, révocation et bandeau mobile.

Le sélecteur lit également l’ancre réelle du navigateur, qui peut avoir changé sans notification de `NavigationManager`. Le retour précédent conserve ainsi une section choisie dans le menu ; un changement de section pendant une résolution en cours annule cette ancienne résolution. Un fournisseur hérité n’exige plus ses catalogues d’édition et de secrets pour afficher sa définition en lecture seule.

[Résultats Déclencheur/Tenant](trigger-tenant-verification.json) · [Bandeau bureau](context-unavailable-desktop-fr.png) · [Bandeau mobile](context-unavailable-mobile-fr.png).
