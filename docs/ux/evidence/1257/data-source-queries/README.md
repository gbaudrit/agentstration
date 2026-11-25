# Revalidation après synchronisation de main — data-source-queries

Commit réellement testé : `171b792a74902bf2ad78bba01e62c06000aafc5e` ; `main` intégré : `e052dadfe511b97985a040fefb76a4b37d544797`. Les fichiers sous `main-e052dadf/` proviennent de cette campagne. Les anciennes captures et observations sont conservées comme historique, sans les réétiqueter au nouveau commit.

**14 essais navigateur réussis, zéro échec/reprise/ignoré.** Huit variantes desktop 1440 × 1000 / mobile 390 × 844, FR/EN et clair/sombre, en tableau et cartes. Chaque séquence couvre grille sans Preview, menu Actions, Preview ouvert, puis fermeture. **Inspection visuelle terminée le 25 novembre 2026 : 64 PNG originaux ouverts individuellement à taille lisible**, 198 pour les trois familles. Les empreintes des PNG, les mesures des lignes, en-têtes, badges et décalages sont dans [observations.json](observations.json).

Le badge Instance occupe un emplacement distinct de l’icône, de son indicateur d’état et du texte, en tableau et en cartes. Les menus et le Preview tiennent dans la fenêtre. Le tableau forcé mobile défile vers Actions et conserve ce décalage après fermeture.

Les hôtes Release ont été construits puis figés pendant toute la campagne. Aucun collecteur de couverture ne modifiait leurs DLL. Les changements de `main` concernant les contrats versionnés des fonctions sont conservés dans le résumé partagé entre éditeur et Preview ; deux régressions FR/EN vérifient les contrats acceptés et leur lecture seule. La nouvelle course de chargement Data Source reproduite pendant la validation est corrigée et documentée [ici](../data-source-loading/README.md).

## Captures du code synchronisé

### table

| Variante | Sans Preview | Actions | Preview | Après fermeture |
| --- | --- | --- | --- | --- |
| desktop-en-US-dark | [closed](main-e052dadf/desktop-en-US-dark/table-closed.png) | [actions](main-e052dadf/desktop-en-US-dark/table-actions.png) | [preview](main-e052dadf/desktop-en-US-dark/table-preview.png) | [after-close](main-e052dadf/desktop-en-US-dark/table-after-close.png) |
| desktop-en-US-light | [closed](main-e052dadf/desktop-en-US-light/table-closed.png) | [actions](main-e052dadf/desktop-en-US-light/table-actions.png) | [preview](main-e052dadf/desktop-en-US-light/table-preview.png) | [after-close](main-e052dadf/desktop-en-US-light/table-after-close.png) |
| desktop-fr-FR-dark | [closed](main-e052dadf/desktop-fr-FR-dark/table-closed.png) | [actions](main-e052dadf/desktop-fr-FR-dark/table-actions.png) | [preview](main-e052dadf/desktop-fr-FR-dark/table-preview.png) | [after-close](main-e052dadf/desktop-fr-FR-dark/table-after-close.png) |
| desktop-fr-FR-light | [closed](main-e052dadf/desktop-fr-FR-light/table-closed.png) | [actions](main-e052dadf/desktop-fr-FR-light/table-actions.png) | [preview](main-e052dadf/desktop-fr-FR-light/table-preview.png) | [after-close](main-e052dadf/desktop-fr-FR-light/table-after-close.png) |
| mobile-en-US-dark | [closed](main-e052dadf/mobile-en-US-dark/table-closed.png) | [actions](main-e052dadf/mobile-en-US-dark/table-actions.png) | [preview](main-e052dadf/mobile-en-US-dark/table-preview.png) | [after-close](main-e052dadf/mobile-en-US-dark/table-after-close.png) |
| mobile-en-US-light | [closed](main-e052dadf/mobile-en-US-light/table-closed.png) | [actions](main-e052dadf/mobile-en-US-light/table-actions.png) | [preview](main-e052dadf/mobile-en-US-light/table-preview.png) | [after-close](main-e052dadf/mobile-en-US-light/table-after-close.png) |
| mobile-fr-FR-dark | [closed](main-e052dadf/mobile-fr-FR-dark/table-closed.png) | [actions](main-e052dadf/mobile-fr-FR-dark/table-actions.png) | [preview](main-e052dadf/mobile-fr-FR-dark/table-preview.png) | [after-close](main-e052dadf/mobile-fr-FR-dark/table-after-close.png) |
| mobile-fr-FR-light | [closed](main-e052dadf/mobile-fr-FR-light/table-closed.png) | [actions](main-e052dadf/mobile-fr-FR-light/table-actions.png) | [preview](main-e052dadf/mobile-fr-FR-light/table-preview.png) | [after-close](main-e052dadf/mobile-fr-FR-light/table-after-close.png) |

### cards

| Variante | Sans Preview | Actions | Preview | Après fermeture |
| --- | --- | --- | --- | --- |
| desktop-en-US-dark | [closed](main-e052dadf/desktop-en-US-dark/cards-closed.png) | [actions](main-e052dadf/desktop-en-US-dark/cards-actions.png) | [preview](main-e052dadf/desktop-en-US-dark/cards-preview.png) | [after-close](main-e052dadf/desktop-en-US-dark/cards-after-close.png) |
| desktop-en-US-light | [closed](main-e052dadf/desktop-en-US-light/cards-closed.png) | [actions](main-e052dadf/desktop-en-US-light/cards-actions.png) | [preview](main-e052dadf/desktop-en-US-light/cards-preview.png) | [after-close](main-e052dadf/desktop-en-US-light/cards-after-close.png) |
| desktop-fr-FR-dark | [closed](main-e052dadf/desktop-fr-FR-dark/cards-closed.png) | [actions](main-e052dadf/desktop-fr-FR-dark/cards-actions.png) | [preview](main-e052dadf/desktop-fr-FR-dark/cards-preview.png) | [after-close](main-e052dadf/desktop-fr-FR-dark/cards-after-close.png) |
| desktop-fr-FR-light | [closed](main-e052dadf/desktop-fr-FR-light/cards-closed.png) | [actions](main-e052dadf/desktop-fr-FR-light/cards-actions.png) | [preview](main-e052dadf/desktop-fr-FR-light/cards-preview.png) | [after-close](main-e052dadf/desktop-fr-FR-light/cards-after-close.png) |
| mobile-en-US-dark | [closed](main-e052dadf/mobile-en-US-dark/cards-closed.png) | [actions](main-e052dadf/mobile-en-US-dark/cards-actions.png) | [preview](main-e052dadf/mobile-en-US-dark/cards-preview.png) | [after-close](main-e052dadf/mobile-en-US-dark/cards-after-close.png) |
| mobile-en-US-light | [closed](main-e052dadf/mobile-en-US-light/cards-closed.png) | [actions](main-e052dadf/mobile-en-US-light/cards-actions.png) | [preview](main-e052dadf/mobile-en-US-light/cards-preview.png) | [after-close](main-e052dadf/mobile-en-US-light/cards-after-close.png) |
| mobile-fr-FR-dark | [closed](main-e052dadf/mobile-fr-FR-dark/cards-closed.png) | [actions](main-e052dadf/mobile-fr-FR-dark/cards-actions.png) | [preview](main-e052dadf/mobile-fr-FR-dark/cards-preview.png) | [after-close](main-e052dadf/mobile-fr-FR-dark/cards-after-close.png) |
| mobile-fr-FR-light | [closed](main-e052dadf/mobile-fr-FR-light/cards-closed.png) | [actions](main-e052dadf/mobile-fr-FR-light/cards-actions.png) | [preview](main-e052dadf/mobile-fr-FR-light/cards-preview.png) | [after-close](main-e052dadf/mobile-fr-FR-light/cards-after-close.png) |

## Limites et historique

- Les trois requêtes désactivées et le dataset désactivé sont des métadonnées synthétiques persistées dans le hôte jetable, avec des pins inertes. Aucune acquisition, création de collection documentaire, exécution ou publication de requête n’est validée.
- Le contrat Query ne possède pas de Description ; les noms d’affichage et techniques longs sont testés.
- Les captures sont des vues de fenêtre natives, pas des pages entières ni toutes les sections du Preview. Les mesures couvrent aussi les lignes hors écran.
- Seule la conservation du défilement horizontal est assertée ; certaines fermetures réinitialisent la position verticale.
- La campagne locale utilise Chromium système sans vidéo ; les jobs hébergés restent nécessaires pour leur environnement propre.
- En tableau forcé sur mobile, les captures Actions et après fermeture sont prises à droite : l’identité est volontairement hors champ dans ce tableau non épinglé. Le badge est inspecté dans closed et dans toutes les cartes. Les Queries ne stockent pas de description ; leurs noms techniques et affichés longs sont testés.

Les résultats ci-dessous appartiennent à la campagne antérieure `1e52fb3e`. Ils ne remplacent pas les captures synchronisées ci-dessus. [Observations originales conservées](history/observations-original-1e52fb3e.json).

---

## Campagne historique 1e52fb3e — data-source-queries

Commit réellement testé : `1e52fb3e06c303b4ad32e10398edef214c1f780b`. Le commit `de066f4a` ajoute uniquement les évidences et leur documentation. La maintenance CI ultérieure déplace des tests dans des processus distincts et corrige une attente de test ; les sources de rendu restaient identiques jusqu’à 5dd14a30 (avant la synchronisation e052dadf), voir [l’analyse CI](../../../../contributing/ci-memory-1273.md). Les empreintes dans [observations.json](observations.json) identifient le code produit testé.

8 variantes : desktop 1440 × 1000 / mobile 390 × 844, EN/FR, clair/sombre. Les deux représentations sont contrôlées dans chaque variante. Chaque séquence capture la grille sans Preview, le menu Actions, le Preview ouvert, puis la grille après fermeture. Vues de fenêtre natives, sans montage ni réduction.

Le badge de périmètre textuel occupait la couche absolue IconBadges. Il utilise désormais Indicators, dans le flux normal, entre l’icône et le texte. Le conteneur imbriqué nécessite aussi min-width:0. Le tableau à deux colonnes réserve la place des Actions et borne l’identité longue ; les boutons ne peuvent plus être comprimés verticalement. La fermeture du Preview restaure le fragment natif #queries.

Assertions : en-têtes, colonnes, hauteurs, badges, menus, focus et défilement horizontal. Inspection visuelle : **terminée**, 64 PNG originaux lus individuellement à taille native lisible le 22 novembre 2026 (198 pour les trois familles).

Icône, badge Instance et texte occupent des places distinctes en tableau et cartes. Les Actions restent horizontales. À 1440 px, les deux colonnes tiennent dans la section sans déplacer l’identité. À 390 px, le tableau forcé défile normalement : l’identité peut sortir de la fenêtre en allant vers Actions ; les cartes gardent toute la composition visible. Le retour du Preview conserve la section Queries.

La conservation de position vérifiée porte sur le défilement horizontal ; certaines navigations natives à la fermeture réinitialisent la position verticale. L’inspection porte sur les régions visibles des captures, complétée par les mesures automatiques des lignes hors écran.

## Données et limites

- Les trois requêtes désactivées et le dataset désactivé sont des métadonnées synthétiques persistées dans le hôte jetable, avec des pins inertes. Aucune acquisition, création de collection documentaire, exécution ou publication de requête n’est validée.
- Le contrat Query ne possède pas de Description ; les noms d’affichage et techniques longs sont testés.

Les trois Artefacts durables sont préparés par les API natives de contenu temporaire et le Flow de stockage fichier, qui s’exécute réellement. Aucun appel de modèle ou exécution de requête n’est revendiqué. Cette maintenance ne clôture pas le scénario réutilisable #1258.

## Captures historiques du commit 1e52fb3e

### table

| Variante | Grille | Actions | Preview | Après fermeture |
| --- | --- | --- | --- | --- |
| en-US / light / 1440 | [closed](grids/table-en-US-light-1440-closed.png) | [actions](grids/table-en-US-light-1440-actions.png) | [preview](grids/table-en-US-light-1440-preview.png) | [after-close](grids/table-en-US-light-1440-after-close.png) |
| en-US / dark / 1440 | [closed](grids/table-en-US-dark-1440-closed.png) | [actions](grids/table-en-US-dark-1440-actions.png) | [preview](grids/table-en-US-dark-1440-preview.png) | [after-close](grids/table-en-US-dark-1440-after-close.png) |
| en-US / light / 390 | [closed](grids/table-en-US-light-390-closed.png) | [actions](grids/table-en-US-light-390-actions.png) | [preview](grids/table-en-US-light-390-preview.png) | [after-close](grids/table-en-US-light-390-after-close.png) |
| en-US / dark / 390 | [closed](grids/table-en-US-dark-390-closed.png) | [actions](grids/table-en-US-dark-390-actions.png) | [preview](grids/table-en-US-dark-390-preview.png) | [after-close](grids/table-en-US-dark-390-after-close.png) |
| fr-FR / light / 390 | [closed](grids/table-fr-FR-light-390-closed.png) | [actions](grids/table-fr-FR-light-390-actions.png) | [preview](grids/table-fr-FR-light-390-preview.png) | [after-close](grids/table-fr-FR-light-390-after-close.png) |
| fr-FR / dark / 390 | [closed](grids/table-fr-FR-dark-390-closed.png) | [actions](grids/table-fr-FR-dark-390-actions.png) | [preview](grids/table-fr-FR-dark-390-preview.png) | [after-close](grids/table-fr-FR-dark-390-after-close.png) |
| fr-FR / light / 1440 | [closed](grids/table-fr-FR-light-1440-closed.png) | [actions](grids/table-fr-FR-light-1440-actions.png) | [preview](grids/table-fr-FR-light-1440-preview.png) | [after-close](grids/table-fr-FR-light-1440-after-close.png) |
| fr-FR / dark / 1440 | [closed](grids/table-fr-FR-dark-1440-closed.png) | [actions](grids/table-fr-FR-dark-1440-actions.png) | [preview](grids/table-fr-FR-dark-1440-preview.png) | [after-close](grids/table-fr-FR-dark-1440-after-close.png) |

### cards

| Variante | Grille | Actions | Preview | Après fermeture |
| --- | --- | --- | --- | --- |
| en-US / light / 1440 | [closed](grids/cards-en-US-light-1440-closed.png) | [actions](grids/cards-en-US-light-1440-actions.png) | [preview](grids/cards-en-US-light-1440-preview.png) | [after-close](grids/cards-en-US-light-1440-after-close.png) |
| en-US / dark / 1440 | [closed](grids/cards-en-US-dark-1440-closed.png) | [actions](grids/cards-en-US-dark-1440-actions.png) | [preview](grids/cards-en-US-dark-1440-preview.png) | [after-close](grids/cards-en-US-dark-1440-after-close.png) |
| en-US / light / 390 | [closed](grids/cards-en-US-light-390-closed.png) | [actions](grids/cards-en-US-light-390-actions.png) | [preview](grids/cards-en-US-light-390-preview.png) | [after-close](grids/cards-en-US-light-390-after-close.png) |
| en-US / dark / 390 | [closed](grids/cards-en-US-dark-390-closed.png) | [actions](grids/cards-en-US-dark-390-actions.png) | [preview](grids/cards-en-US-dark-390-preview.png) | [after-close](grids/cards-en-US-dark-390-after-close.png) |
| fr-FR / light / 390 | [closed](grids/cards-fr-FR-light-390-closed.png) | [actions](grids/cards-fr-FR-light-390-actions.png) | [preview](grids/cards-fr-FR-light-390-preview.png) | [after-close](grids/cards-fr-FR-light-390-after-close.png) |
| fr-FR / dark / 390 | [closed](grids/cards-fr-FR-dark-390-closed.png) | [actions](grids/cards-fr-FR-dark-390-actions.png) | [preview](grids/cards-fr-FR-dark-390-preview.png) | [after-close](grids/cards-fr-FR-dark-390-after-close.png) |
| fr-FR / light / 1440 | [closed](grids/cards-fr-FR-light-1440-closed.png) | [actions](grids/cards-fr-FR-light-1440-actions.png) | [preview](grids/cards-fr-FR-light-1440-preview.png) | [after-close](grids/cards-fr-FR-light-1440-after-close.png) |
| fr-FR / dark / 1440 | [closed](grids/cards-fr-FR-dark-1440-closed.png) | [actions](grids/cards-fr-FR-dark-1440-actions.png) | [preview](grids/cards-fr-FR-dark-1440-preview.png) | [after-close](grids/cards-fr-FR-dark-1440-after-close.png) |

## Avant correction

[Archives historiques et notes originales](history/before-visual-revalidation/README.md). Les assertions anciennes sont conservées comme historique et ne valent pas validation visuelle du nouveau code.

[Essai intermédiaire rejeté après inspection visuelle](history/intermediate-rejected-7461e99a/README.md) : les assertions seules ne suffisaient pas ; les budgets de colonnes ont ensuite été corrigés et les régressions renforcées.

Reproduction portable : depuis `automation/playwright`, exécuter `npm run test:smoke -- collection-layout.spec.ts`. La campagne consignée utilise le même test avec un fichier temporaire de configuration pour Chromium système (`/usr/bin/chromium`), les sorties JSON/PNG et la métadonnée `sourceRevision`. Les hôtes Release sont construits avant la campagne puis figés (`AGENTSTRATION_PLAYWRIGHT_NO_BUILD=true`).
