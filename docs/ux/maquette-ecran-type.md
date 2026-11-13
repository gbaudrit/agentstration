# Maquette interactive de fiche ressource

Cette proposition illustre le socle commun décrit dans le [catalogue des formulaires](unification-des-formulaires.md) et l’[analyse des écrans transverses](analyse-des-ecrans-transverses.md). L’Agent sert de ressource de démonstration ; le gabarit de fiche, les champs, les actions et les états sont destinés à être comparés aux autres éditeurs.

## Ouvrir la maquette

Le fichier [maquette-ecran-type.html](https://github.com/gbaudrit/agentstration/blob/ux/unification-formulaires/docs/ux/maquette-ecran-type.html) est autonome : styles, icônes, données et interactions sont inclus. Télécharger ce fichier, puis l’ouvrir directement dans un navigateur. Il n’installe aucune bibliothèque et ne modifie pas les composants de l’application.

Pour un aperçu HTTP local, depuis la racine du dépôt :

```bash
python -m http.server 5274 --bind 127.0.0.1 --directory docs/ux
```

Ouvrir ensuite [la maquette locale](http://localhost:5274/maquette-ecran-type.html).

Les données sont fictives. L’enregistrement, le déploiement et le test sont des simulations en mémoire ; recharger la page réinitialise les ressources de démonstration. Le choix du thème est conservé dans le stockage local du navigateur. Le catalogue contient 72 profils et la section « Utilisé par » 32 ressources pour éprouver les filtres et la pagination.

## Parcours à essayer

1. Ouvrir la Vue d’ensemble : elle présente la définition enregistrée, son état et les utilisations, en dehors du formulaire.
2. Parcourir « Utilisé par », rechercher une ressource et filtrer les Flows ou Entrées. Six résultats sont affichés par page.
3. Ouvrir Édition : le nom technique se trouve à côté du nom d’affichage. Lors d’une création, il est généré automatiquement ; il peut être ajusté avant l’enregistrement, puis devient immuable.
4. Vider Description ou Instructions avec la croix intégrée ; vérifier le focus, la validation et l’indication de modification.
5. Déplier le catalogue de modèles directement dans le formulaire. Filtrer par nom, modèle, fournisseur ou périmètre ; changer de page ou sélectionner au clavier avec les flèches et Entrée. La sélection reste visible même si les filtres ne la retournent plus.
6. Changer de panneau entre Vue d’ensemble, Édition, Accès et YAML : les saisies sont conservées ; Vue d’ensemble et YAML continuent de montrer la définition enregistrée.
7. Revenir à Agents avec une saisie en cours : le dialogue permet de rester ou d’abandonner explicitement les modifications.
8. Enregistrer une modification, puis déployer la nouvelle version avec l’action distincte du panneau d’état.
9. Dans la liste, rechercher un agent, filtrer par état et vider les filtres depuis les contrôles. Un état sans résultat est prévu.
10. Basculer entre les thèmes clair et sombre avec le bouton soleil/lune de la barre supérieure ; redimensionner sur mobile.

Le bouton Tester ouvre un échange simulé, identifié comme tel. La création d’un agent et l’ajout d’étiquettes sous la forme `clé=valeur` permettent aussi d’examiner les variantes du formulaire. L’onglet Accès illustre la propriété et les règles applicables à une ressource de Workspace. Le YAML enregistré est consultable et copiable en lecture seule dans cette maquette.

## Compositions représentées

| Composition | Proposition visible |
| --- | --- |
| En-tête de fiche | Identité courte, retour à la liste et action de test distincte de la sauvegarde. |
| Champ | Label, valeur, croix interne, aide, erreur et état de lecture seule ; contraintes conservées après effacement. |
| Identité | Noms d’affichage et technique côte à côte ; génération automatique pendant la création, ajustement possible avant enregistrement et stabilité après création. |
| Section et grille | Mission, références et organisation dans Édition ; résumé et usages dans Vue d’ensemble ; une colonne sur mobile. |
| Sélecteur de ressource | Valeur effaçable, recherche et filtres intégrés à la page ; résultats paginés, statut de disponibilité et choix au clavier sans fenêtre modale. |
| Actions et état d’édition | Barre persistante, modification effective, annulation et sauvegarde ; versions enregistrée et active affichées dans un résumé compact. |
| Liste | Recherche, filtre d’état, compte de résultats, état vide et ouverture de fiche ; présentation adaptée au mobile. |
| Vue d’ensemble et utilisations | Définition enregistrée et 32 utilisations, recherche, filtre de type et pagination ; l’édition ne présente qu’un résumé des usages. |
| Navigation et dialogue | Vue d’ensemble, Édition, Accès et YAML ; onglets utilisables au clavier, champs conservés entre panneaux, dialogues de confirmation et de test avec gestion du focus et fermeture avec Échap. |
| Thèmes | Couleurs de surface, textes, champs, tableaux, états et dialogues adaptés aux thèmes clair et sombre. |

## Aperçus

- Fiche : [thème clair](maquette-fiche-desktop.png) et [thème sombre](maquette-fiche-dark.png).
- Édition avec catalogue filtrable : [thème clair](maquette-edition-desktop.png) et [thème sombre](maquette-edition-dark.png).
- Liste : [thème clair](maquette-liste-desktop.png) et [thème sombre](maquette-liste-dark.png).
- Mobile : [thème clair](maquette-fiche-mobile.png) et [thème sombre](maquette-mobile-dark.png).

## Vérifications réalisées

Inspection dans Chromium et vérifications automatisées ponctuelles de l’identité automatique puis immuable, de l’effacement, du focus, de la validation, de la protection des saisies, des filtres sur 72 profils, du choix au clavier sans dialogue, des utilisations paginées, de la sauvegarde et du déploiement simulés, des quatre onglets et de la création. La vue enregistrée reste distincte du brouillon. La bascule de thème est vérifiée avec conservation de la préférence au rechargement et sans déclencher de modification métier.

Captures de vue d’ensemble à 1536 × 1120, d’édition avec catalogue à 1536 × 1500, de liste à 1536 × 1000 et de mobile à 390 × 844. Aucun débordement horizontal du document sur les vues mobiles contrôlées. Les tests avec lecteur d’écran, les autres navigateurs et une intégration aux modèles et API de l’application restent à réaliser avant toute reprise en composants de production.
