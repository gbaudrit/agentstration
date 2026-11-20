# POC — Changer de périmètre en conservant sa position

La branche `codex/poc-context-switch` part de `82206267` sur `codex/ux-corrections`, puis intègre `7ba891ad`. La PR #1256 et la FR #1254 suivent ce POC ; le parcours réutilisable reste différé dans le sous-ticket natif #1255.

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
- Tests ciblés : 17 cas du résolveur, 39 cas d’état/navigation/état de santé et 98 cas du cadre partagé/Flows/tableau de bord, tous réussis. Les cas du résolveur imposent une seule lecture du contexte et aucune lecture préalable par type de ressource.
- Fast après généralisation : 2 550 cas, 2 533 réussites, 17 échecs déjà présents hors du correctif. Une copie isolée de `7ba891ad` reproduit 16 de ces échecs (1 552 cas de Console.Components, 1 536 réussites). Le dix-septième, `LateReviewUnderAChangedOperationalWorkspaceCannotClearNewRows`, est déjà reproduit sur `82206267`. [Liste et comparaison](baseline-failures.json). Le mainteneur confie ces autres corrections à un autre agent ; elles ne sont pas modifiées ici.
- Integration après généralisation : 1 570 cas, 1 542 réussites, aucun échec, 28 cas optionnels ignorés.
- Après généralisation, les 5 vérifications multi-familles, les 8 vérifications Agent/brouillons, les 2 variantes bureau/mobile de la vue d’ensemble et les 3 régressions navigateur `hierarchical-context` réussissent. La campagne globale de 494 cas n’est pas exécutée intégralement. Les résultats historiques JavaScript (17 cas) et TypeScript précèdent la généralisation, qui ne modifie pas ces sources.

## Captures

- Flow hérité conservé : [bureau](flow-workspace-retained.png), [mobile](flow-mobile-retained.png).
- Vue d’ensemble Instance : [bureau](overview-desktop-instance.png), [mobile](overview-mobile-instance.png).
- Agent accessible : [bureau anglais](desktop-en-accessible.png), [mobile français](mobile-fr-accessible.png).
- Agent indisponible : [bureau anglais](desktop-en-unavailable.png), [mobile français](mobile-fr-unavailable.png).
