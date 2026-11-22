# Revalidation visuelle — data-source-queries

Commit réellement testé : `1e52fb3e06c303b4ad32e10398edef214c1f780b`. Les modifications ultérieures concernent uniquement les évidences et leur documentation. Les empreintes dans [observations.json](observations.json) identifient le code produit testé.

8 variantes : desktop 1440 × 1000 / mobile 390 × 844, EN/FR, clair/sombre. Les deux représentations sont contrôlées dans chaque variante. Chaque séquence capture la grille sans Preview, le menu Actions, le Preview ouvert, puis la grille après fermeture. Vues de fenêtre natives, sans montage ni réduction.

Le badge de périmètre textuel occupait la couche absolue IconBadges. Il utilise désormais Indicators, dans le flux normal, entre l’icône et le texte. Le conteneur imbriqué nécessite aussi min-width:0. Le tableau à deux colonnes réserve la place des Actions et borne l’identité longue ; les boutons ne peuvent plus être comprimés verticalement. La fermeture du Preview restaure le fragment natif #queries.

Assertions : en-têtes, colonnes, hauteurs, badges, menus, focus et défilement horizontal. Inspection visuelle : **terminée**, 64 PNG originaux lus individuellement à taille native lisible le 22 novembre 2026 (198 pour les trois familles).

Icône, badge Instance et texte occupent des places distinctes en tableau et cartes. Les Actions restent horizontales. À 1440 px, les deux colonnes tiennent dans la section sans déplacer l’identité. À 390 px, le tableau forcé défile normalement : l’identité peut sortir de la fenêtre en allant vers Actions ; les cartes gardent toute la composition visible. Le retour du Preview conserve la section Queries.

La conservation de position vérifiée porte sur le défilement horizontal ; certaines navigations natives à la fermeture réinitialisent la position verticale. L’inspection porte sur les régions visibles des captures, complétée par les mesures automatiques des lignes hors écran.

## Données et limites

- Les trois requêtes désactivées et le dataset désactivé sont des métadonnées synthétiques persistées dans le hôte jetable, avec des pins inertes. Aucune acquisition, création de collection documentaire, exécution ou publication de requête n’est validée.
- Le contrat Query ne possède pas de Description ; les noms d’affichage et techniques longs sont testés.

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
