# Champs communs dans les formulaires complémentaires

Suivi : [#1124](https://github.com/gbaudrit/agentstration/issues/1124), scénario différé [#1125](https://github.com/gbaudrit/agentstration/issues/1125). Base : `ux/unification-formulaires`, après #1114. Le chantier de listes #1121 reste indépendant.

## Périmètre livré

Cette vague adopte les composants de champs existants dans neuf éditeurs. Elle ne refond pas leurs cycles de publication, leurs modèles métier ou tous leurs onglets.

| Éditeur | Champs adoptés | Comportements conservés |
| --- | --- | --- |
| Secret | Description, référence exacte du coffre, clé, type informatif et nouvelle valeur masquée | Métadonnées et valeur ont des sauvegardes distinctes. Vider une nouvelle valeur ne supprime jamais la valeur stockée. Suppression explicite et confirmations conservées. |
| Coffre | Fournisseur ; identité commune déjà présente | Fournisseur immuable après création, initialisation de la clé maître et droits descendants conservés. |
| Fournisseur d’outils | Type, transport, référence AEP, commande, arguments, répertoire, références d’environnement et endpoint MCP | Connexion, découverte, champs conditionnels, restrictions des ressources gérées et onglet Outils conservés. |
| Catégorie d’outils | Description ; recherche avec `SearchBox` | Filtrer ne change pas les membres sélectionnés, y compris les références absentes conservées. |
| Source de données | Namespace, description, profil et cible d’acquisition | Configuration guidée par schéma, découverte, acquisition, YAML et références exactes conservés. |
| Profil de source de données | Namespace, description, versions, Flows d’acquisition et de découverte | Les éditeurs de JSON, les révisions, l’activation, Accès et YAML gardent leur fonctionnement. |
| Source de connaissances | Namespace, description, Flows de projection/retrieval/transformation, nom et référence de liaison, cible, durée maximale | Effacer une source de liaison efface sa cible dépendante. Configuration guidée, options et validation métier restent spécialisées. |
| Entrée | Description, placeholder, options de présentation, champs dynamiques, suggestions, type et référence de cible, création de tâche | Icône, exposition, aperçu, sélection exclusive de l’entrée principale, publication et YAML conservent leurs composants et contrats. |
| Schéma | Namespace, format, dialecte, description et version publiée | Format/dialecte restent immuables après création ; Designer, contenu natif, versions et YAML restent spécialisés. |

`FormField` associe le label, l’aide et les messages de validation au contrôle. `ClearableText` et `ClearableSelect` placent la croix dans le champ et rendent le focus au contrôle après effacement. Les descriptions facultatives utilisent le marqueur traduit commun. Aucun catalogue de traductions ni bibliothèque UI supplémentaire n’est introduit.

Les sélections à enum non nullable, lorsqu’elles sont hors `EditContext`, conservent une valeur métier valide selon le contrat existant de `ClearableSelect`. Les références texte peuvent devenir vides ; les champs requis gardent leurs validations existantes. Les champs de namespace conservent les conventions existantes du namespace par défaut.

Les formulaires Secret/Coffre suivent désormais `EditContext.OnFieldChanged` en plus de leurs événements existants : une croix déclenche aussi le bandeau de modifications. Le formulaire est recréé lorsqu’un modèle enregistré/restauré le remplace, ce qui retire les anciennes erreurs de validation après annulation. Les changements de droits descendants restent signalés par leur éditeur propre.

Les styles isolés d’Entrée et de Schéma ciblent les nouveaux conteneurs de composants pour conserver les champs larges et les dimensions des zones de texte. Les règles de Schéma ne forcent plus les labels internes partagés en grille, afin que l’astérisque reste auprès du label.

## Vérifications

Les tests de composants vérifient les comportements suivants :

- Effacer la clé d’un Secret signale un brouillon ; la validation empêche l’enregistrement ; annuler restaure la valeur enregistrée et enlève les anciennes erreurs, en anglais et en français.
- Effacer une nouvelle valeur secrète désactive sa sauvegarde sans appeler la suppression ni l’écriture de valeur.
- Effacer le nom d’affichage d’un Coffre déclenche le bandeau ; annuler restaure son identité sans changer le nom technique.
- Effacer une commande de fournisseur laisse le transport inchangé sans invoquer une commande de gestion, dans les deux langues.
- Effacer une liaison de source de connaissances retire sa cible d’acquisition ; sélectionner à nouveau la source ne restaure pas une ancienne cible implicitement.
- Effacer une description d’Entrée ou de Schéma n’enregistre rien implicitement ; la sauvegarde explicite garde le binding ou le contenu/format/dialecte.

Le contrôle visuel temporaire dans l’application réelle couvre les neuf formulaires en français/anglais, clair/sombre et 1440/390 px : association label/contrôle, nom accessible de la croix, valeur vide, retour du focus, aucune navigation et absence de débordement horizontal. Ce contrôle ponctuel ne constitue pas le nouveau scénario réutilisable #1125.

Résultats locaux : compilation Release sans avertissement ni erreur, 410 tests de composants Console réussis, 944 tests rapides réussis, vérification TypeScript et build complet du site documentaire réussis. Les 72 combinaisons visuelles passent ; les huit combinaisons Schéma sont revérifiées après la correction du label. Le contrôle supplémentaire d’un Secret réellement stocké dans SQLite passe également. Les résultats des suites d’intégration et des parcours navigateur complets sont consignés dans la PR et dans #1124.

## Captures de l’application

Les images proviennent des hôtes SQLite/déterministes isolés, avec des données de démonstration. Elles montrent le desktop sombre et le mobile clair en français ; les autres combinaisons sont vérifiées sans multiplier les captures.

| Formulaire | Desktop sombre | Mobile clair |
| --- | --- | --- |
| Secret | [Capture](secret-dark-1440.png) | [Capture](secret-light-390.png) |
| Coffre | [Capture](coffre-dark-1440.png) | [Capture](coffre-light-390.png) |
| Fournisseur d’outils | [Capture](fournisseur-outils-dark-1440.png) | [Capture](fournisseur-outils-light-390.png) |
| Catégorie d’outils | [Capture](categorie-outils-dark-1440.png) | [Capture](categorie-outils-light-390.png) |
| Source de données | [Capture](source-donnees-dark-1440.png) | [Capture](source-donnees-light-390.png) |
| Profil de source | [Capture](profil-source-dark-1440.png) | [Capture](profil-source-light-390.png) |
| Source de connaissances | [Capture](source-connaissances-dark-1440.png) | [Capture](source-connaissances-light-390.png) |
| Entrée | [Capture](entree-dark-1440.png) | [Capture](entree-light-390.png) |
| Schéma | [Capture](schema-dark-1440.png) | [Capture](schema-light-390.png) |

## Suite du programme

Le catalogue de listes #1121, les images et leur héritage #1110, les éditeurs de registres de Sources, l’administration, les écrans opérationnels et le Flow Designer conservent leurs chantiers propres. Les formulaires complets ne sont pas tous déclarés « migrés » parce que leurs champs ordinaires utilisent le socle. La matrice initiale reste lisible avec cette extension de périmètre.

Le scénario réutilisable #1125 attend la validation explicite de l’UX implémentée et la fusion de la PR. La PR d’implémentation reste en brouillon, sans approbation ni fusion par l’agent. La FR reste ouverte jusqu’à sa couverture par le scénario ou une dérogation motivée du mainteneur.
