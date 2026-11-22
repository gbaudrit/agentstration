# Revalidation visuelle — transformation-functions

Commit réellement testé : `1e52fb3e06c303b4ad32e10398edef214c1f780b`. Le commit `de066f4a` ajoute uniquement les évidences et leur documentation. La maintenance CI ultérieure déplace des tests dans des processus distincts et corrige une attente de test ; les sources de rendu restent identiques, voir [l’analyse CI](../../../../contributing/ci-memory-1273.md). Les empreintes dans [observations.json](observations.json) identifient le code produit testé.

8 variantes : desktop 1440 × 1000 / mobile 390 × 844, EN/FR, clair/sombre. Les deux représentations sont contrôlées dans chaque variante. Chaque séquence capture la grille sans Preview, le menu Actions, le Preview ouvert, puis la grille après fermeture. Vues de fenêtre natives, sans montage ni réduction.

La correction précédente reste effective : Description cible la quatrième colonne et Actions possède une cinquième colonne bornée. Neuf ressources, dont trois noms et descriptions longs, sont contrôlées.

Assertions : en-têtes, colonnes, hauteurs, badges, menus, focus et défilement horizontal. Inspection visuelle : **terminée**, 70 PNG originaux lus individuellement à taille native lisible le 22 novembre 2026 (198 pour les trois familles).

Neuf ressources, dont trois descriptions longues : paragraphes lisibles, cinq en-têtes séparés, hauteurs proportionnées au contenu. Les six largeurs complémentaires sont lisibles. À 390 px, le tableau forcé nécessite un défilement horizontal ; les cartes offrent des paragraphes lisibles et un défilement vertical normal.

La conservation de position vérifiée porte sur le défilement horizontal ; certaines navigations natives à la fermeture réinitialisent la position verticale. L’inspection porte sur les régions visibles des captures, complétée par les mesures automatiques des lignes hors écran.

## Données et limites

- Le défaut historique caractère par caractère n’a pas été reproduit sur 9132710e : la correction précédente y était déjà présente. La capture avant correction est conservée.
- En mode tableau forcé sur écran étroit, le défilement horizontal reste nécessaire. Les cartes sont la représentation étroite native.

Les trois Artefacts durables sont préparés par les API natives de contenu temporaire et le Flow de stockage fichier, qui s’exécute réellement. Aucun appel de modèle ou exécution de requête n’est revendiqué. Cette maintenance ne clôture pas le scénario réutilisable #1258.

## Captures actuelles

### table

| Variante | Grille | Actions | Preview | Après fermeture |
| --- | --- | --- | --- | --- |
| en-US / light / 1440 | [closed](catalog-layout/table-en-US-light-1440-closed.png) | [actions](catalog-layout/table-en-US-light-1440-actions.png) | [preview](catalog-layout/table-en-US-light-1440-preview.png) | [after-close](catalog-layout/table-en-US-light-1440-after-close.png) |
| en-US / dark / 1440 | [closed](catalog-layout/table-en-US-dark-1440-closed.png) | [actions](catalog-layout/table-en-US-dark-1440-actions.png) | [preview](catalog-layout/table-en-US-dark-1440-preview.png) | [after-close](catalog-layout/table-en-US-dark-1440-after-close.png) |
| en-US / light / 390 | [closed](catalog-layout/table-en-US-light-390-closed.png) | [actions](catalog-layout/table-en-US-light-390-actions.png) | [preview](catalog-layout/table-en-US-light-390-preview.png) | [after-close](catalog-layout/table-en-US-light-390-after-close.png) |
| en-US / dark / 390 | [closed](catalog-layout/table-en-US-dark-390-closed.png) | [actions](catalog-layout/table-en-US-dark-390-actions.png) | [preview](catalog-layout/table-en-US-dark-390-preview.png) | [after-close](catalog-layout/table-en-US-dark-390-after-close.png) |
| fr-FR / light / 390 | [closed](catalog-layout/table-fr-FR-light-390-closed.png) | [actions](catalog-layout/table-fr-FR-light-390-actions.png) | [preview](catalog-layout/table-fr-FR-light-390-preview.png) | [after-close](catalog-layout/table-fr-FR-light-390-after-close.png) |
| fr-FR / dark / 390 | [closed](catalog-layout/table-fr-FR-dark-390-closed.png) | [actions](catalog-layout/table-fr-FR-dark-390-actions.png) | [preview](catalog-layout/table-fr-FR-dark-390-preview.png) | [after-close](catalog-layout/table-fr-FR-dark-390-after-close.png) |
| fr-FR / light / 1440 | [closed](catalog-layout/table-fr-FR-light-1440-closed.png) | [actions](catalog-layout/table-fr-FR-light-1440-actions.png) | [preview](catalog-layout/table-fr-FR-light-1440-preview.png) | [after-close](catalog-layout/table-fr-FR-light-1440-after-close.png) |
| fr-FR / dark / 1440 | [closed](catalog-layout/table-fr-FR-dark-1440-closed.png) | [actions](catalog-layout/table-fr-FR-dark-1440-actions.png) | [preview](catalog-layout/table-fr-FR-dark-1440-preview.png) | [after-close](catalog-layout/table-fr-FR-dark-1440-after-close.png) |

### cards

| Variante | Grille | Actions | Preview | Après fermeture |
| --- | --- | --- | --- | --- |
| en-US / light / 1440 | [closed](catalog-layout/cards-en-US-light-1440-closed.png) | [actions](catalog-layout/cards-en-US-light-1440-actions.png) | [preview](catalog-layout/cards-en-US-light-1440-preview.png) | [after-close](catalog-layout/cards-en-US-light-1440-after-close.png) |
| en-US / dark / 1440 | [closed](catalog-layout/cards-en-US-dark-1440-closed.png) | [actions](catalog-layout/cards-en-US-dark-1440-actions.png) | [preview](catalog-layout/cards-en-US-dark-1440-preview.png) | [after-close](catalog-layout/cards-en-US-dark-1440-after-close.png) |
| en-US / light / 390 | [closed](catalog-layout/cards-en-US-light-390-closed.png) | [actions](catalog-layout/cards-en-US-light-390-actions.png) | [preview](catalog-layout/cards-en-US-light-390-preview.png) | [after-close](catalog-layout/cards-en-US-light-390-after-close.png) |
| en-US / dark / 390 | [closed](catalog-layout/cards-en-US-dark-390-closed.png) | [actions](catalog-layout/cards-en-US-dark-390-actions.png) | [preview](catalog-layout/cards-en-US-dark-390-preview.png) | [after-close](catalog-layout/cards-en-US-dark-390-after-close.png) |
| fr-FR / light / 390 | [closed](catalog-layout/cards-fr-FR-light-390-closed.png) | [actions](catalog-layout/cards-fr-FR-light-390-actions.png) | [preview](catalog-layout/cards-fr-FR-light-390-preview.png) | [after-close](catalog-layout/cards-fr-FR-light-390-after-close.png) |
| fr-FR / dark / 390 | [closed](catalog-layout/cards-fr-FR-dark-390-closed.png) | [actions](catalog-layout/cards-fr-FR-dark-390-actions.png) | [preview](catalog-layout/cards-fr-FR-dark-390-preview.png) | [after-close](catalog-layout/cards-fr-FR-dark-390-after-close.png) |
| fr-FR / light / 1440 | [closed](catalog-layout/cards-fr-FR-light-1440-closed.png) | [actions](catalog-layout/cards-fr-FR-light-1440-actions.png) | [preview](catalog-layout/cards-fr-FR-light-1440-preview.png) | [after-close](catalog-layout/cards-fr-FR-light-1440-after-close.png) |
| fr-FR / dark / 1440 | [closed](catalog-layout/cards-fr-FR-dark-1440-closed.png) | [actions](catalog-layout/cards-fr-FR-dark-1440-actions.png) | [preview](catalog-layout/cards-fr-FR-dark-1440-preview.png) | [after-close](catalog-layout/cards-fr-FR-dark-1440-after-close.png) |

Largeurs supplémentaires avec les mêmes descriptions longues : [760 px](catalog-layout/functions-table-width-760.png), [761 px](catalog-layout/functions-table-width-761.png), [1024 px](catalog-layout/functions-table-width-1024.png), [1280 px](catalog-layout/functions-table-width-1280.png), [1659 px](catalog-layout/functions-table-width-1659.png), [1920 px](catalog-layout/functions-table-width-1920.png).

## Avant correction

[Archives historiques et notes originales](history/before-visual-revalidation/README.md). Les assertions anciennes sont conservées comme historique et ne valent pas validation visuelle du nouveau code.

Reproduction portable : depuis `automation/playwright`, exécuter `npm run test:smoke -- collection-layout.spec.ts`. La campagne consignée utilise le même test avec un fichier temporaire de configuration pour Chromium système (`/usr/bin/chromium`), les sorties JSON/PNG et la métadonnée `sourceRevision`. Les hôtes Release sont construits avant la campagne puis figés (`AGENTSTRATION_PLAYWRIGHT_NO_BUILD=true`).
