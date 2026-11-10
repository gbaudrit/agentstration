# En-tête et références directement navigables

Suite de [PR #1114](https://github.com/gbaudrit/agentstration/pull/1114), toujours en brouillon vers `ux/unification-formulaires`.

## Changements

- L’en-tête Agent présente le périmètre, la génération et le namespace avant l’identifiant technique. Le composant partagé `ResourceHeader` possède un paramètre `TechnicalName` dédié : il rend cet identifiant en dernier, le tronque lorsque nécessaire et conserve sa copie complète.
- La description occupe une seule ligne avec ellipsis et infobulle native contenant tout le texte. Le contenu complet reste présent dans le DOM pour les technologies d’assistance.
- Le panneau Identité est retiré de la vue d’ensemble Agent : les informations sont regroupées dans l’en-tête. Le composant de résumé d’identité reste disponible pour les autres écrans dont l’en-tête ne porte pas ces informations.
- Le panneau Exécution de consultation est retiré. Le runtime est directement cliquable dans Déploiement actif. Sa destination utilise le nom et le namespace de la révision réellement déployée, et non ceux d’une modification en cours. Une identité active non résolue reste du texte, sans destination inventée.
- Dans les cartes de configuration déclarée/résolue et dans le profil de modèle sélectionné, les noms du profil et du fournisseur sont les liens. Les actions séparées « Ouvrir… » ou « Voir le profil… » sont supprimées.
- La configuration du profil d’exécution reste disponible dans Définition.

Les URL de ressources, règles d’autorisation et contrats API sont conservés. Les cartes gardent leurs contrôles de sélection/effacement distincts des liens de consultation.

## Captures

Application locale avec un identifiant technique de plus de 80 caractères, une description longue et un Agent réellement préparé pour le runtime de démonstration. La hauteur des captures est augmentée pour montrer les blocs ; le débordement est contrôlé avec une hauteur normale de 844 pixels.

| Vue | Clair | Sombre |
| --- | --- | --- |
| En-tête bureau | [Voir](entete-light-1440.png) | [Voir](entete-dark-1440.png) |
| En-tête mobile | [Voir](entete-light-390.png) | [Voir](entete-dark-390.png) |
| Vue d’ensemble bureau | [Voir](vue-ensemble-light-1440.png) | [Voir](vue-ensemble-dark-1440.png) |
| Vue d’ensemble mobile | [Voir](vue-ensemble-light-390.png) | [Voir](vue-ensemble-dark-390.png) |

[Carte du profil sélectionné](profil-selectionne-dark.png).

## Vérification

- Compilation Release : aucune erreur ni avertissement. Lane rapide : 927 tests réussis.
- Quatre parcours existants passent : création d’Agent, administration des modèles et nommage sur bureau/mobile.
- Le contrôle dans l’application vérifie l’identifiant en dernier, sa copie complète malgré l’ellipsis, la description tronquée avec son infobulle, les deux thèmes et les deux largeurs sans débordement, l’absence des panneaux retirés et des liens d’ouverture redondants, puis le chargement des trois éditeurs liés.
- Le composant `StateComparison` est testé avec un lien qualifié par namespace, puis sans destination connue. Les tests du sélecteur vérifient les liens sur les noms et l’absence de lien d’ouverture supplémentaire.
- Lane d’intégration : 1 011 tests réussis, 12 intégrations optionnelles ignorées, aucun échec.
- Build du site documentaire et vérification de whitespace réussis.

Les scénarios réutilisables restent suivis dans les enfants natifs existants et bloqués jusqu’à validation UX explicite puis fusion. La PR n’est ni validée ni fusionnée par cette passe.
