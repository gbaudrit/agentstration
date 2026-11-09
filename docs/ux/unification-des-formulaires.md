# Besoins de composants pour unifier les formulaires

L’objectif est de faire émerger un socle commun de formulaire pour l’ensemble de l’IHM. La fiche agent est un exemple de ses besoins, parmi les profils, sources, outils, paramètres, secrets, Flows, entrées et écrans d’administration.

L’IHM dispose déjà de styles et de composants réutilisables. Le principal manque est un contrat commun pour assembler les champs, afficher leur validation, présenter les actions et gérer les modifications. La proposition consiste à consolider ces fondations, puis à extraire les compositions qui reviennent dans plusieurs écrans.

L’[analyse des listes, fiches, écrans d’administration et du Workplace](analyse-des-ecrans-transverses.md) étend cet inventaire à 23 écrans supplémentaires et précise les besoins de composants transverses.

La [maquette interactive de fiche ressource](maquette-ecran-type.md) permet de comparer ces propositions sur une liste et un formulaire, dans les thèmes clair et sombre et sur mobile.

Le [plan des FR d’unification](plan-fr-unification.md) transforme ces besoins en neuf brouillons locaux : une FR parent, six FR de réalisation et deux tâches techniques de socle, avec dépendances, critères d’acceptation et scénarios de validation. Aucune issue n’est publiée à ce stade.

## Périmètre de l’état des lieux

Analyse du 9 novembre 2026, à partir de `main` au commit `8a9fafd7432e28c7aab23109c43593cc90b35c66`. Branche de travail : `ux/unification-formulaires`.

- Inventaire statique des 220 fichiers `.razor` de `src`, puis lecture comparative des éditeurs Console, composants de valeurs, formulaires Workplace et formulaires d’authentification et de compte en Razor Pages.
- Inspection du DOM et captures des pages de création Paramètre, Secret, Coffre, Profil de modèle, Profil d’exécution, Source de données, Source de connaissances, Schéma, Déclencheur, Définition d’outil, Flow et Entrée. Application locale en français, Chromium, 1440 × 1000 et 390 × 844 ; les données et dépendances disponibles sont celles du bootstrap Development. Pour Entrée, la vue Définition est examinée en complément de la vue d’ensemble.
- Les différences de validation, d’édition et de sauvegarde sont comparées dans le code. Les soumissions de ces douze formulaires, tous leurs états conditionnels et les parcours Workplace ne sont pas validés dans le navigateur. Les problèmes de conservation des saisies déjà reproduits sur Agent sont documentés dans [l’étude de cas agent](etat-des-lieux-fiche-agent.md).

Il s’agit d’un inventaire de besoins et d’une proposition de composants, sans modification des écrans ni création d’un nouveau moteur de formulaires.

## Ce qui est déjà mutualisé

Les nombres ci-dessous comptent les fichiers qui contiennent littéralement le marqueur indiqué. Ils ne représentent pas le nombre de parcours, ni une mesure de conformité ou d’adoption complète.

| Existant | Présence dans les fichiers Razor | Ce que cela apporte et ce qui reste dispersé |
| --- | --- | --- |
| Styles `form-section`, `form-grid`, `form-actions` | Respectivement 35, 33 et 29 fichiers | Apparence commune ; structure HTML, titres, aides et placement des actions recopiés. |
| `ResourceIdentityFields` | 28 fichiers consommateurs | Nom d’affichage et nom technique déjà partagés ; description, namespace, périmètre et lecture seule assemblés par écran. |
| `ResourceScopeFormField` | 18 fichiers consommateurs | Chargement et sélection du périmètre ; variantes avec `ResourceScopePicker`, badge ou champ verrouillé. |
| `EditForm` / `ValidationSummary` | 20 / 15 fichiers | Une partie des éditeurs utilise la validation Blazor ; d’autres utilisent du HTML ou des boutons avec validation propre. |
| `UnsavedChangesBar` | 4 fichiers consommateurs | Agent, Secret, Coffre, Déclencheur ; détection des changements et actions gérées séparément. |
| `JsonSchemaInputEditor` | 7 fichiers consommateurs, 8 usages | Formulaire généré à partir d’un schéma, validation, valeurs verrouillées et repli JSON déjà disponibles. |
| `ModelProfilePicker`, `ReusableSchemaBindingEditor`, `ValueBindingRequirementEditor` | Compositions spécialisées existantes | Choix d’une ressource, liaison à un schéma, valeur littérale/Paramètre/Secret ; à conserver et harmoniser. |
| `HelpTooltip`, `ConfirmationDialog`, `ResourceTabs`, composants de statut | Bibliothèque partagée | Briques existantes à intégrer au contrat des formulaires ; elles ne prennent pas en charge tout le cycle d’édition. |

La présence de styles communs est une bonne base. Elle n’empêche pas les écarts : les actions de `ToolDefinitionEditor` utilisent `page-actions`, celles de Paramètre `form-actions`, et Agent utilise une barre de modifications. Le comportement sticky de `.resource-form > .form-actions` dépend aussi de l’imbrication HTML [S1].

## Matrice des besoins par famille

| Famille examinée | Variantes observées | Besoins de composants qui en ressortent |
| --- | --- | --- |
| Agent et définition d’agent dans Pack | Sélecteur de modèle dans la fiche ; références saisies en texte dans le formulaire Pack ; instructions et options réparties différemment. | Identité commune, champ de référence, section, options avancées, état d’édition, source. Les règles d’édition d’un Pack restent propres au Pack. |
| Profils de modèle et d’exécution | Identité/périmètre, sélections dépendantes, nombres et énumérations, aides, options JSON ; mêmes compositions de labels et contrôles répétées. | Cadre de champ, groupe de champs, choix de ressource, options avancées, validation et actions. |
| Fournisseurs de modèles, d’outils et de sources | Identité, type ou contribution, champs conditionnels, endpoints, commandes, références de valeurs. | Choix de type, section conditionnelle, références, valeurs protégées ; garder les contraintes de transport dans leur domaine. |
| Sources de données et profils de sources | Édition, publication, configuration par schéma, opérations d’acquisition et YAML. | Socle de formulaire, référence versionnée, configuration structurée, actions distinctes d’enregistrement et de publication. |
| Sources de connaissances | Plusieurs liaisons ajoutables/supprimables ; choix Source/Flow/cible et configuration par liaison. | Collection de sous-formulaires, référence, champ et retour de validation local à chaque élément. |
| Paramètres, Secrets et Coffres | Identité/périmètre communs ; métadonnées et valeur de Secret enregistrées séparément ; création contextuelle dans une boîte de dialogue. | Identité, actions, formulaire en dialogue, référence avec création contextuelle ; composant de valeur secrète spécialisé. |
| Déclencheurs | Flow, fuseau horaire, variantes une fois/intervalle/cron, aperçu de planification et JSON. | Champ, section conditionnelle, actions et source ; conserver un éditeur de planification spécialisé. |
| Schémas, contrats et liaisons | Designer, source, validation de définition et d’instance, version publiée ou contenu hérité en lecture seule. | Consolider les éditeurs existants ; partager champ, validation, mode source et indication de lecture seule. |
| Flows et Entrées | Création par étapes, cartes de choix, options de présentation, listes de champs, publication et preview. | Groupe de choix, navigation d’étapes, collection, sections, actions de cycle de vie explicites. |
| Administration et accès | Création de Workspace, membres, rôles, autorisations et cibles de périmètre. | Formulaire court ou en dialogue, sélection, validation, confirmation et justification des restrictions. |
| Workplace | `FormEntry` et `EntryFieldRenderer` rendent des champs à partir d’une définition ; prompt, conversation et actions en attente ont leurs propres interactions. | Réutiliser présentation des champs, aides, erreurs et états d’attente ; conserver les moteurs et les actions de conversation. |
| Authentification, bootstrap et compte | Formulaires Razor Pages `.cshtml`, labels et validation ASP.NET, styles `auth-form` et actions propres. | Partager tokens et règles visuelles/accessibles ; fournir un rendu adapté à Razor Pages, sans imposer les composants interactifs Blazor. |

## Catalogue de composants proposé

Les noms ci-dessous désignent des responsabilités proposées, pas des API C# déjà décidées. Un composant n’est extrait qu’après vérification de ses usages réels ; les contrôles natifs et `InputText`, `InputSelect`, `InputNumber` restent utilisables.

Le gabarit de fiche retenu pour la maquette distingue **Vue d’ensemble**, **Édition**, **Accès** et **YAML**. La vue d’ensemble présente la définition enregistrée et ses usages ; le formulaire et ses actions restent dans Édition. Le nom technique accompagne le nom d’affichage : il est généré à la création, puis reste stable lorsque la ressource est renommée.

### Socle à traiter en premier

| Besoin | État actuel | Responsabilité et variantes attendues | Premiers consommateurs |
| --- | --- | --- | --- |
| **`FormField`** | Labels, aides et erreurs composés dans chaque éditeur. | Associer label, contrôle, aide et erreur ; afficher obligatoire/facultatif et motif de verrouillage. Exposer des identifiants pour `aria-describedby` et l’erreur ; permettre texte court, texte long, nombre, date et sélection sans recréer leur moteur. Prévoir l’effacement intégré au contrôle, sans bouton extérieur. Un seul propriétaire du label. | Paramètre, Profils, Agent, champs Workplace. |
| **`FormSection` et `FormGrid`** | Styles partagés, balisage répété. | Titre, description, statut ou action de section, contenu ; largeur de lecture et grille responsive, champ en pleine largeur. Variante simple ou section avancée repliable, hiérarchie de titres cohérente. | Tous les éditeurs Console ; composition réutilisable en dialogue. |
| **`FormActions`** | Plusieurs classes et positions ; logique sticky liée au parent. | Zone d’action stable avec une action principale par contexte, actions secondaires et état d’attente. Variantes page/dialogue/étape. L’écran fournit les commandes : enregistrer, publier, appliquer ou continuer conservent leurs effets propres. | Paramètre, Profils, Sources, Agent. |
| **`FormFeedback`** | `ValidationSummary`, alertes, messages JSON et exceptions présentés séparément. | Afficher un résumé navigable vers les champs, une erreur de sauvegarde ou de concurrence, un avertissement et un succès. Préserver les valeurs, gérer focus et annonce accessible, proposer une reprise lorsque l’écran la fournit. Ne pas refaire les validateurs métier. | Paramètre et Profils pour Blazor ; Sources et Schémas pour les diagnostics structurés. |
| **Composition d’identité de ressource** | `ResourceIdentityFields` est déjà largement réutilisé. | Présenter le nom technique à côté du nom d’affichage, avec génération automatique à la création et indication de son immutabilité ensuite. Composer les champs existants avec description, namespace et périmètre selon les capacités réelles de la ressource. Distinguer création, périmètre fixe et ressource gérée. Le champ de périmètre n’est pas ajouté aux ressources qui n’en ont pas besoin. | Profils, Sources, Paramètres, Agent ; comparaison avec Pack. |

Ces composants ont besoin d’un **comportement d’édition commun** : état initial, modification effective, validation, sauvegarde en cours, succès ou échec, abandon et protection de la navigation. Il doit ignorer recherche, filtres et simple changement d’onglet. Après retour aux valeurs enregistrées, le formulaire redevient inchangé. `UnsavedChangesBar` en est une vue ; elle ne doit pas porter seule ce comportement. Ce besoin traverse aussi les éditeurs source, qui doivent conserver leur texte temporairement invalide.

### Effacement intégré aux champs

Règle demandée : pouvoir vider une valeur directement dans le champ, sans bouton supplémentaire à l’extérieur. Le contrôle peut proposer une option `Clearable` ; le cadre `FormField` conserve la responsabilité du label, de l’aide et de l’erreur.

- Afficher une croix d’effacement à l’intérieur du contrôle lorsqu’il contient une valeur modifiable. Réserver sa place pour éviter de recouvrir le texte ; elle reste utilisable sur écran tactile et ne dépend pas uniquement du survol.
- Fournir un bouton interne de type `button`, accessible au clavier et nommé de manière localisée, par exemple « Vider le champ Description ». Après activation, remettre le focus sur le contrôle sans soumettre le formulaire.
- Vider produit la valeur vide appropriée : chaîne vide ou `null` selon le contrat, `null` pour un nombre ou une date facultatifs, collection vide pour une sélection multiple. Ne pas convertir une absence de valeur en zéro ou en date par défaut.
- Les champs obligatoires peuvent être vidés ; leurs contraintes continuent de s’appliquer et l’erreur est présentée selon la règle de validation commune. Un sélecteur ne remplace pas automatiquement le choix effacé par sa première option.
- L’effacement d’une valeur métier déclenche le suivi normal des modifications ; l’effacement d’une recherche ne modifie pas la définition. L’action ne revient pas à la dernière valeur enregistrée.
- Un contrôle verrouillé ou en lecture seule ne propose pas cet effacement. Un booléen à deux états conserve sa checkbox ; une valeur booléenne nullable peut exposer explicitement l’absence de choix.
- Dans un champ de saisie de secret, vider retire uniquement le texte en cours. La suppression d’une valeur secrète déjà stockée conserve son opération dédiée.

### Compositions à extraire ensuite

| Besoin | Point de départ existant | Responsabilité et limites |
| --- | --- | --- |
| **`ResourceReferenceField`** | `ModelProfilePicker`, sélections de Flow/profil/coffre et cibles de `ValueBindingRequirementEditor`. | Pour un grand catalogue, sélection intégrée à la page avec recherche par nom, fournisseur ou modèle, filtres de périmètre et résultats bornés. La recherche ne modifie pas la sélection enregistrée ; une fenêtre modale n’est pas le choix par défaut. États chargement, vide, erreur, sélection devenue indisponible, restriction et lecture seule. Actions voir/créer si autorisées. Les adaptateurs conservent l’adresse exacte, le namespace, le périmètre et la version nécessaires ; le socle visuel ne charge pas tous les catalogues métier. |
| **`ChoiceField` et `ChoiceGroup`** | Checkbox simple, `checkbox-field`, `entry-toggle`, cartes de choix Flow, toggle JSON Schema. | Présentation et focus cohérents pour booléen, choix unique et choix multiple, avec description et état désactivé. Conserver les distinctions checkbox/radio/switch : une option enregistrée à la soumission ne doit pas suggérer une application immédiate. |
| **`CollectionEditor`** | Liaisons de sources, champs d’Entrée, listes et paires clé/valeur saisies en texte. | Ajouter, retirer et, lorsque l’ordre est significatif, réordonner des éléments ; afficher titre, résumé et erreurs de chaque élément. Prévoir une composition clé/valeur avec doublons signalés. Les objets JSON complexes continuent d’utiliser l’éditeur de schéma adapté. |
| **Éditeur source intégré** | `SchemaSourceEditor`, `SchemaDocumentEditor`, `JsonSchemaInputEditor`, `FlowSourceEditor` et textareas YAML/JSON locaux. | Consolider le rendu, les diagnostics, le formatage et les bascules de mode autour des éditeurs existants. Distinguer texte saisi, document valide et version enregistrée ; aucune bascule ne remplace silencieusement le texte. Sérialisation, conservation du contenu natif et publication restent propres au type de document. |
| **`ReadOnlyField` et section avancée** | Champs `disabled`, `readonly`, badges et notices héritées, sections techniques et `<details>`. | Valeur lisible, éventuellement copiable si approprié, et motif explicite : identité immuable, héritage, gestion par Pack, droit insuffisant ou attente. Une section avancée peut regrouper des paramètres techniques ; elle doit s’ouvrir pour révéler une erreur. |
| **`FormDialog`** | Créateurs contextuels de Paramètre/Secret, création d’outil d’acquisition, dialogues locaux. | Titre, description, contenu et actions cohérents ; focus initial, maintien et retour du focus, fermeture accessible et traitement d’une saisie en cours. Réutiliser les champs et actions de la page sans imbriquer deux éléments HTML `form`. Garder `ConfirmationDialog` pour les confirmations simples. |

## Règles communes à faire porter par le socle

1. **Un champ a un contrat complet.** Nom, aide, valeur, contraintes, erreur, état et motif d’indisponibilité sont traités ensemble. Le placeholder ne remplace pas le label. Les identifiants et liens d’aide fonctionnent aussi dans une collection et un dialogue.
2. **Les états sont explicites.** Chargement, absence de choix, choix interdit, valeur verrouillée et erreur de récupération n’ont pas le même sens. Une référence indisponible reste identifiable et n’est pas remplacée automatiquement par une autre.
3. **Le formulaire conserve le travail.** Échec serveur, conflit et bascule de mode ne réinitialisent pas les valeurs. Le changement de périmètre ou de dépendance qui invalide une saisie demande un traitement explicite.
4. **L’action décrit son effet.** Enregistrer une définition, déployer, publier et exécuter restent des opérations distinctes. Les formulaires simples et les étapes d’un assistant partagent le rendu des actions, avec des libellés adaptés au résultat.
5. **La composition est responsive.** Texte long, instructions, schémas et éditeurs source peuvent prendre toute la largeur ; identité courte et petits réglages peuvent occuper deux colonnes. Vérifier les barres d’action avec le clavier virtuel et la navigation basse.
6. **Les garanties sont partagées.** Localisation `en-US`/`fr-FR`, contrastes, focus, saisie au clavier, validation accessible et état d’attente font partie des composants. Les formulaires Razor Pages appliquent le même contrat avec leur mécanisme de validation et de soumission.
7. **Les secrets gardent leur traitement propre.** Séparer métadonnées et valeur en écriture seule, ne pas réafficher une valeur existante, et éviter copie, logs ou conservation dans un brouillon générique. Un champ masqué ne suffit pas à définir un éditeur de secret.

## Bibliothèques candidates pour les grilles

Deux usages sont distincts : la grille de disposition du formulaire, aujourd’hui portée par CSS Grid, et le tableau de données, aujourd’hui rendu par le composant `DataGrid` local. Ce dernier se limite à une table avec en-tête et lignes fournis par l’écran ; il ne fournit pas de tri, filtre ou pagination intégrés. Une bibliothèque de tableaux ne remplace donc pas le contrat des sections et champs de formulaire.

| Candidat | Intérêt pour l’IHM | Compromis à évaluer |
| --- | --- | --- |
| **[MudBlazor](https://github.com/MudBlazor/MudBlazor)**, licence MIT | Bibliothèque cohérente pour champs et tableaux : `MudTextField` propose `Clearable`, `MudGrid` organise la disposition et `MudDataGrid` apporte tri, filtres, pagination, sélection et édition. | Présentation fondée sur Material Design ; vérifier l’adaptation à nos tokens, la densité, la validation Blazor, le thème et la coexistence avec les composants actuels. |
| **[QuickGrid](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/quickgrid?view=aspnetcore-10.0)**, ASP.NET Core | Bon point de comparaison pour enrichir les tableaux en gardant notre présentation : colonnes typées ou personnalisées, tri, pagination, virtualisation et fournisseur de données. | Les interactions plus riches demandent des compositions supplémentaires ; les champs et la disposition des formulaires restent couverts par notre socle. |
| **[RadzenDataGrid](https://blazor.radzen.com/datagrid)**, bibliothèque [Radzen Blazor sous licence MIT](https://github.com/radzenhq/radzen-blazor) | Candidat pour des tableaux d’administration riches : tri, filtres, pagination, regroupement et édition. | Vérifier les thèmes, styles, scripts et services nécessaires, ainsi que la cohérence des interactions avec le reste de la Console. |

Orientation proposée après l’élargissement de l’analyse : **consolider un socle interne réduit et évaluer les bibliothèques pour des besoins ciblés**, notamment les tableaux. QuickGrid est un candidat pour enrichir les tableaux existants ; MudBlazor et Radzen restent des options si leurs composants couvrent un besoin plus large démontré par le prototype. Pour la seule disposition des formulaires, CSS Grid suffit déjà aux besoins recensés. Aucun choix de bibliothèque n’est arrêté.

Le choix reste à valider par un prototype sur le formulaire de Paramètre, le catalogue d’Outils peuplé et un dialogue Pack, puis sur le Profil de modèle. Vérifier effacement, valeurs nullables, erreurs, clavier, localisation, thèmes, mobile, styles et chargement des données avant de retenir une bibliothèque. Les tableaux continuent de passer par les clients API existants ; un adaptateur de grille ne doit pas créer d’accès direct à EF Core depuis l’IHM. Une éventuelle dépendance sera versionnée dans `Directory.Packages.props`.

Cette comparaison repose sur la documentation des projets ; aucune bibliothèque n’est installée ni testée dans l’application à ce stade.

## Organisation proposée

- Placer les primitives de présentation communes dans la bibliothèque existante `Agentstration.Web.Components`, sans nouvelle dépendance aux clients métier.
- Garder dans `Agentstration.Console.Components` les compositions qui connaissent les ressources, droits, périmètres et inventaires. Consolider les sélecteurs et créateurs existants par composition.
- Faire adopter progressivement les primitives par `JsonSchemaField` et `EntryFieldRenderer`. Le premier rend un JSON Schema ; le second une définition d’Entrée. Leurs modèles restent distincts.
- Partager les styles et règles avec les pages `.cshtml` par une intégration propre à Razor Pages. La composition ne doit pas modifier leur authentification, protection des formulaires ou validation serveur.
- Extraire une section métier seulement lorsqu’elle a plusieurs usages réellement compatibles. Agent et Pack peuvent partager des champs de comportement ; leurs règles de référence et d’édition doivent être comparées avant mutualisation.

## Ordre de réalisation proposé

| Étape | Livrable concret | Validation attendue |
| --- | --- | --- |
| 1 | Petit catalogue de référence avec `FormField`, section/grille, actions et feedback ; documenter leurs variantes. | Montrer champs vide/rempli/obligatoire/invalide/verrouillé, texte long, attente, mobile, thèmes, français et anglais, clavier et zoom. |
| 2 | Pilote sur **Paramètre**, puis **Profil de modèle**. | Couvrir un formulaire simple et un formulaire avec choix dépendants et réglages ; comparer les composants obtenus avant de migrer les autres familles. |
| 3 | Comportement d’édition partagé sur **Agent**, puis **Secret/Coffre/Déclencheur**. | Une recherche ne modifie pas la définition ; les changements effectifs sont protégés ; erreurs et bascules préservent les saisies. Respecter le traitement particulier des valeurs secrètes. |
| 4 | Références, collections et source sur **Source de données**, **Source de connaissances** et **Entrée**. | Vérifier cible absente, indisponible ou interdite, création contextuelle, erreurs de ligne et changements de dépendances ; aucun écrasement du texte source. |
| 5 | Adoption par les autres éditeurs, Workplace et pages de compte. | Même contrat de champ et d’action avec des parcours adaptés ; vérifier droits, lecture seule, validation serveur et interactions propres à chaque surface. |

Les tests de comportement et les vérifications navigateur accompagnent chaque changement. Pour les scénarios UX réutilisables, appliquer le suivi prévu dans `AGENTS.md`. Le catalogue et cette matrice servent à préparer ces travaux ; aucun composant proposé ici n’est déclaré implémenté.

## Sources principales

- [S1 — Styles de formulaire existants](https://github.com/gbaudrit/agentstration/blob/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Web.Components/wwwroot/management.css)
- [Bibliothèque de composants partagés](https://github.com/gbaudrit/agentstration/tree/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Web.Components)
- [Éditeurs et formulaires Console](https://github.com/gbaudrit/agentstration/tree/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Console.Components/Components/Pages)
- [Définition d’agent dans Pack](https://github.com/gbaudrit/agentstration/blob/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Console.Components/Components/Shared/PackAgentDefinitionForm.razor)
- [Valeurs et création contextuelle](https://github.com/gbaudrit/agentstration/tree/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Console.Components/Components/Values)
- [Rendu des champs par JSON Schema](https://github.com/gbaudrit/agentstration/blob/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Web.Components/JsonSchema/JsonSchemaField.razor)
- [Champs et formulaires Workplace](https://github.com/gbaudrit/agentstration/tree/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Workplace.Components)
- [Pages de connexion et de compte](https://github.com/gbaudrit/agentstration/tree/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Web/Pages)
- [Connexion de la Console séparée](https://github.com/gbaudrit/agentstration/blob/8a9fafd7432e28c7aab23109c43593cc90b35c66/src/Agentstration.Console.Web/Pages/Login.cshtml)
