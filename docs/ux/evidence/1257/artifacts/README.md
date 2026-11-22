# Revalidation visuelle — artifacts

Commit réellement testé : `1e52fb3e06c303b4ad32e10398edef214c1f780b`. Le commit `de066f4a` ajoute uniquement les évidences et leur documentation. La maintenance CI ultérieure déplace des tests dans des processus distincts et corrige une attente de test ; les sources de rendu restent identiques, voir [l’analyse CI](../../../../contributing/ci-memory-1273.md). Les empreintes dans [observations.json](observations.json) identifient le code produit testé.

8 variantes : desktop 1440 × 1000 / mobile 390 × 844, EN/FR, clair/sombre. Les deux représentations sont contrôlées dans chaque variante. Chaque séquence capture la grille sans Preview, le menu Actions, le Preview ouvert, puis la grille après fermeture. Vues de fenêtre natives, sans montage ni réduction.

Les Actions hors de la vue déplacent horizontalement le tableau ; le Preview conserve ensuite ce décalage. La première colonne est désormais fixe pendant le défilement. Les colonnes durables ont des largeurs explicites pour conserver des en-têtes et dates lisibles.

Assertions : en-têtes, colonnes, hauteurs, badges, menus, focus et défilement horizontal. Inspection visuelle : **terminée**, 64 PNG originaux lus individuellement à taille native lisible le 22 novembre 2026 (198 pour les trois familles).

Les huit en-têtes tiennent à 1440 px. Icône et début du nom restent visibles, ellipse en fin de texte. À 390 px, la première colonne reste fixe pendant le défilement vers Actions. Les menus et le Preview sont lisibles ; la fermeture conserve le décalage horizontal.

La conservation de position vérifiée porte sur le défilement horizontal ; certaines navigations natives à la fermeture réinitialisent la position verticale. L’inspection porte sur les régions visibles des captures, complétée par les mesures automatiques des lignes hors écran.

## Données et limites

- La coupure historique exacte à 1440 px n’a pas été reproduite avec les premières nouvelles données avant correction : scrollLeft restait à 0. Le même mécanisme a été reproduit dans le tableau à 390 px : 0 → 648 px.
- Le contrat des artefacts ne possède pas de Description enregistrée ; les noms de fichiers longs et métadonnées natives sont testés.
- Les captures staged-artifact sont conservées dans les archives historiques ; cette revalidation porte sur les artefacts durables.

Les trois Artefacts durables sont préparés par les API natives de contenu temporaire et le Flow de stockage fichier, qui s’exécute réellement. Aucun appel de modèle ou exécution de requête n’est revendiqué. Cette maintenance ne clôture pas le scénario réutilisable #1258.

## Captures actuelles

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
