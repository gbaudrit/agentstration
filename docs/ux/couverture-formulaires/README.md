# Couverture des formulaires — #1128

Cette branche de tests s’empile sur l’implémentation #1129 / #1127. Le mainteneur a explicitement autorisé sa préparation avant la fusion et la validation UX définitive de l’implémentation. Les deux PR restent en brouillon. Cette préparation ne constitue pas une acceptation UX.

## Périmètre

L’[inventaire produit](../formulaires-tous-ecrans/README.md) contient 50 composants routés, 75 patrons de routes et les adaptateurs imbriqués. Le catalogue `form-adoption-coverage.ts` associe chaque composant à ses tests sans convertir une vérification de champ en couverture complète de ses opérations métier. Un contrôle empêche l’ajout silencieux d’un écran à l’inventaire sans déclaration de couverture.

Les nouvelles spécifications produisent **252 cas** indépendants :

| Famille | Cas | Assertions principales |
| --- | ---: | --- |
| 19 éditeurs de ressources | 152 | Libellés associés, aides existantes, effacement intégré, focus, absence de navigation et de débordement |
| Adaptateurs contextuels, Registry, Pack et préférences | 32 | Annulation par Échap, restitution du focus, aucune création implicite, secrets non affichés, sélection obligatoire vide et validation |
| Administration des comptes | 8 | Sélection de Workspace typée invalide après effacement, création désactivée, compte inchangé |
| Flow, Source, Entry et Workplace | 32 | Priorité nullable et transitions préservées, planification numérique invalide sans sauvegarde, champs texte/date/nombre/choix/booléen, prompt sans envoi implicite |
| Demandes de saisie en attente | 8 | Brouillon local, bouton Continuer désactivé après effacement, interaction toujours en attente, aucune Task créée |
| Comptes Razor Pages | 20 | Bootstrap réel, mot de passe, expiration PAT obligatoire, aucune émission de token invalide, login refusé sans réaffichage du mot de passe, connexion sans JavaScript |

Chaque famille est exercée en français/anglais, clair/sombre et ordinateur/mobile, soit huit combinaisons. Les quatre cas supplémentaires de comptes couvrent le POST Bootstrap, deux refus de connexion et la connexion sans JavaScript. Les attentes localisées sont indépendantes des RESX.

## Données et isolation

Les hôtes, SQLite et extensions locales sont ceux des parcours existants. Les formulaires ordinaires utilisent le fournisseur déterministe, sans LLM externe. Les comptes utilisent des identifiants publics jetables réservés aux fixtures Development. Une fixture Bootstrap sans initialisation vérifie le vrai POST de création.

La fixture OrganizationStructure existante peut préparer une interaction d’entrée en attente dans le dépôt réel. Elle publie une Entry valide, crée l’interaction et son PendingAction avec un jeton public de fixture haché, puis expose uniquement l’identifiant de l’interaction dans son fichier de disponibilité. Aucun endpoint produit, composant de démonstration ou réponse simulée n’est ajouté. Les tests n’envoient pas cette réponse : ils vérifient que l’effacement reste local.

Les références Source utilisent le périmètre propriétaire exact conservé dans l’URL. Les tests de droits, ETags, ressources gérées et références inter-périmètres restent dans les parcours métier existants (`resource-access`, `inherited-resource-readonly`, `resource-scopes`, `source-lifecycle`, `organization-structure`, `descendant-secrets`, etc.). Ils sont exécutés par la campagne CI complète et ne sont pas remplacés par ces tests de champs.

## Adaptateurs et limites explicites

Les inspecteurs Flow, JSON Schema, éditeurs de contrats, sélecteurs de ressources, fichiers et code restent spécialisés. Les tests existants du Designer et des schémas conservent leurs assertions métier ; le nouveau parcours d’inspecteur vérifie précisément la priorité et le délai de commit du champ commun.

Le login du BFF Console alternatif utilise le même adaptateur Razor Pages que le login canonique. Son intégration au composant statique est testée par les tests .NET ; les tests navigateur démarrent l’hôte canonique, sans prétendre avoir lancé un deuxième hôte BFF.

Les statuts `covered` / `partial` / `planned` du catalogue application conservent leur sens métier. La propriété `formContract` expose une dimension de preuve distincte ; les anciens scénarios ne deviennent pas exhaustifs parce qu’ils sont référencés ici. Les listes #1121 restent dans leur livraison indépendante.

## Exécution

Depuis `automation/playwright`, après la construction Release des hôtes :

```sh
npm exec tsc -- --noEmit
npm run test:fixtures
npm exec playwright -- test tests/application-surface-catalog.spec.ts --project desktop-chromium
AGENTSTRATION_PLAYWRIGHT_NO_BUILD=true npm run test:smoke -- tests/shared-forms.spec.ts tests/form-adapters.spec.ts tests/form-administration.spec.ts tests/form-workflows.spec.ts tests/pending-form.spec.ts tests/native-forms.spec.ts tests/native-bootstrap-forms.spec.ts
```

Le smoke CI complet exécute aussi les régressions existantes. Les cas Bootstrap et Pending nécessitent leurs fixtures isolées et sont ignorés quand des URL externes sont fournies. Les captures/checkpoints et les traces d’échec restent attachés aux résultats Playwright. Les résultats définitifs et liens CI sont consignés dans #1128 et la PR empilée.

Les trois parcours `exercise-shared-form`, `exercise-form-adapter` et `exercise-form-workflow` sont aussi enregistrés pour le CLI de captures. Exemple, avec les navigateurs Playwright installés :

```sh
npm run capture -- --plan examples/shared-form.capture-plan.json --output /tmp/shared-form-capture
```

Les pages de compte et l’interaction en attente utilisent leurs spécifications dédiées, car elles nécessitent des options de fixture particulières.
