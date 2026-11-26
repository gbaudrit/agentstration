# Data Source — chargement périmé et circuit interrompu

Avant correction : `4ac33e7f`, campagne mobile locale. [Capture native avant correction](before-mobile-loading.png) : la Console reste sur Loading. Le thème clair visible précède la disponibilité des préférences ; cette capture n’est pas une validation de la variante sombre demandée. [Exception serveur](before-server-exception.txt) : `A pending resource requires a blocking load state`. [Régression sur l’ancien code](before-component-regression.txt).

Cause : un premier `LoadAsync` attend ses opérations tandis qu’un second chargement remplace la ressource. À sa reprise, le premier écrivait Ready alors que la nouvelle ressource était encore absente. `1dff056b` vérifie maintenant `disposed` et `loadVersion` immédiatement après cette attente. L’invariant strict du template est conservé.

La régression déterministe `APreviousLoadCannotMarkAStillPendingReplacementAsReady` échoue avec la même exception avant correction, puis passe après. **54 cas DataSourceEditor et FunctionPreview passent** après correction. La campagne finale du commit `171b792a74902bf2ad78bba01e62c06000aafc5e` passe **14 essais navigateur**, dont le scénario Data Source mobile auparavant interrompu.

Après correction : [éditeur Data Source et ses trois Queries](after-mobile-data-source-queries.png), [fin du parcours mobile](after-mobile-journey.png). Cette dernière montre une Knowledge Source avec projection acceptée/en cours ; elle démontre que le parcours a dépassé le chargement défaillant, sans revendiquer la fin de la projection. Les deux originaux 390 × 844 ont été inspectés visuellement à taille native lisible le 25 novembre 2026. La matrice des trois grilles et ses limites est documentée dans les dossiers voisins.
