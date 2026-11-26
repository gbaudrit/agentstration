"""Operator summaries use public validated metadata; provider output is never echoed."""
from datetime import datetime, timezone
import re

import contract
import preview

OPERATIONS = {'deploy': 'Déploiement', 'status': 'État de la prévisualisation', 'destroy': 'Suppression'}


def revision(repository, sha):
    return '[' + sha[:12] + '](https://github.com/' + repository + '/commit/' + sha + ')' if preview.SHA.fullmatch(sha or '') else 'non résolue'


def extension_names(values):
    return ', '.join('`' + name.removeprefix('extension-') + '`' for name in values) or 'aucune — serveur et worker MAF uniquement'


def actions():
    return ('**Commandes :** `/preview status` pour vérifier l’état · `/preview deploy` pour mettre à jour en conservant les données · '
            '`/preview destroy` pour supprimer la preview **et ses données**.')


def expiry(value):
    try:
        date = datetime.fromisoformat(value)
        if date.tzinfo is None:
            return 'non disponible'
        return date.astimezone(timezone.utc).strftime('%d/%m/%Y à %H:%M UTC')
    except (TypeError, ValueError):
        return 'non disponible'


def details(request):
    lines = ['<details><summary>Images exactes utilisées</summary>', '', '| Composant | Version | Image par digest |', '| --- | --- | --- |']
    for name, image in contract.images(request).items():
        lines.append('| ' + name + ' | ' + image.get('version', '—')[:120] + ' | `' + image['reference'] + '` |')
    return '\n'.join([*lines, '', '</details>'])


def accepted(options, repository, run_url):
    operation = options['operation']
    lines = ['**' + OPERATIONS[operation] + ' — demande reçue**',
             'Environnement : `' + options['environment_id'] + '`.']
    if operation == 'deploy':
        lines += ['Révision serveur : ' + revision(repository, options['revision']) + '.',
                  'Base : **' + options['database'] + '**. Extensions : ' + extension_names(options.get('extensions', [])) + '.',
                  'Étape suivante : vérifier les images publiées et les paramètres du déploiement. Aucune VM n’a encore été créée par cette demande.']
    elif operation == 'destroy':
        lines.append('La suppression complète de la VM, de ses disques/IP et des données de cette preview est demandée.')
    else:
        lines.append('Lecture de l’environnement enregistré ; les labels et révisions actuels ne modifient pas ce contrôle.')
    return '\n\n'.join([*lines, '[Suivre le traitement](' + run_url + ').'])


def planned(request, operation, repository, run_url):
    lines = ['**' + OPERATIONS[operation] + ' — vérifications terminées**',
             'Environnement : `' + request['environmentId'] + '`.',
             'Révision serveur : ' + revision(repository, request['sourceSha']) + '.',
             'Base : **' + request['database'] + '**. Extensions : ' + extension_names(request.get('extensions', {})) + '.']
    if operation == 'deploy':
        target = request['target']
        lines += ['Machine : **' + target['vmSize'] + '**, région **' + target['region'] + '**.',
                  'Les images sont disponibles. Le déploiement va démarrer dès que son créneau d’exécution sera disponible.',
                  '**Durée prévue : ' + str(request['ttlHours']) + ' h. L’arrêt automatique n’est pas encore actif ; utilisez `/preview destroy` après le test.**']
    elif operation == 'destroy':
        lines.append('La suppression va retirer les ressources propres à cette preview et **effacer ses données**.')
    return '\n\n'.join([*lines, '[Exécution du workflow](' + run_url + ').', details(request)])


def completed(result, operation, repository, run_url, request=None):
    state = result['state']
    titles = {'running': 'Prévisualisation prête', 'destroyed': 'Prévisualisation supprimée', 'absent': 'Aucun environnement actif',
              'stopped': 'Prévisualisation arrêtée', 'unhealthy': 'Prévisualisation indisponible',
              'provisioning': 'Prévisualisation en cours de démarrage', 'drifted': 'Configuration de la prévisualisation à vérifier'}
    if state == 'running' and not result.get('url'):
        titles[state] = 'Environnement démarré — accès non confirmé'
    lines = ['**' + titles[state] + '**', 'Environnement : `' + result['environmentId'] + '`.']
    if result.get('url'):
        lines += ['[**Ouvrir Agentstration**](' + result['url'] + ')', 'Accès protégé par Cloudflare Access / Entra.']
    if state == 'destroyed':
        lines.append('La VM, les disques/IP et les données propres à cette preview ont été supprimés. Les services partagés sont conservés.')
    elif state == 'absent':
        lines.append('Utilisez `/preview deploy` pour créer une prévisualisation.')
    else:
        sha = result.get('sourceSha') or (request or {}).get('sourceSha')
        if sha:
            lines.append('Révision serveur : ' + revision(repository, sha) + '.')
        if result.get('database'):
            lines.append('Base : **' + result['database'] + '**.')
        selected = (request or {}).get('extensions')
        if selected is None:
            selected = [name for name in result.get('images', {}) if name.startswith('extension-')]
        lines.append('Extensions : ' + extension_names(selected) + '.')
        if request:
            lines.append('Machine : **' + request['target']['vmSize'] + '**, région **' + request['target']['region'] + '**.')
        if state == 'running':
            if not result.get('url'):
                lines.append('L’URL est masquée : l’accès protégé n’a pas pu être confirmé par ce contrôle.')
            lines += ['Échéance prévue : **' + expiry(result.get('expiresAt')) + '**.',
                      '**L’arrêt automatique n’est pas encore actif. Supprimez la preview après le test pour libérer ses ressources.**']
        elif state == 'stopped':
            lines.append('Les données sont conservées ; des disques/IP peuvent rester facturés. `/preview destroy` les supprime.')
        else:
            lines.append('L’URL est masquée tant que la disponibilité et la protection ne sont pas confirmées.')
        lines.append(actions())
    lines.append('[Détails de l’exécution](' + run_url + ').')
    if request:
        lines.append(details(request))
    return '\n\n'.join(lines)


def failed(operation, repository, run_url, stage, options, failure=None):
    failure = failure or {}
    component = failure.get('component')
    preview.require(component is None or isinstance(component, str) and (component in ('server', 'maf-worker') or
                    re.fullmatch(r'[a-z][a-z0-9-]{0,31}', component)), 'Invalid public failure component.')
    code = failure.get('code')
    preview.require(code not in ('image-unavailable', 'registry-access') or component is not None, 'Missing failure component.')
    reasons = {
        'boundary': ('La commande n’a pas passé les contrôles d’autorisation ou de cohérence.',
                     'Vérifiez vos droits, l’ouverture de la PR/issue et ses labels, puis publiez une nouvelle commande non éditée.'),
        'select': ('La vérification des images ou des paramètres a échoué.',
                   'Vérifiez les publications GHCR privées, leurs accès Actions et les paramètres Azure de l’environnement.'),
        'recorded': ('L’environnement enregistré n’a pas pu être lu.',
                     'Vérifiez qu’un déploiement existe pour cet environnement et consultez l’étape de lecture de l’état.'),
        'announce': ('Le contrôle final avant exécution a échoué.',
                     'Les labels, l’état de la PR/issue ou les droits ont pu changer. Publiez une nouvelle commande après vérification.'),
        'execute': ('L’opération Azure ou les contrôles de disponibilité/protection ont échoué.',
                    'Utilisez `/preview status` pour vérifier les ressources restantes, puis `/preview deploy` pour réessayer ou `/preview destroy` pour nettoyer.'),
        'cancelled': ('L’exécution a été annulée.', 'Vérifiez l’état avec `/preview status` avant de relancer une commande.')}
    reason, next_step = reasons.get(stage, reasons['execute'])
    if code == 'image-unavailable':
        reason = 'L’image ' + ('serveur' if component == 'server' else 'du worker MAF' if component == 'maf-worker' else 'de l’extension `' + component + '`') + ' n’est pas disponible pour la révision demandée.'
        next_step = ('Lancez **OCI images** avec `extension=' + component + '` et `publish_preview=true` sur la révision à publier, puis relancez `/preview deploy`. '
                     'Publier une extension ne publie pas l’image serveur de la PR.')
    elif code == 'registry-access':
        reason = 'Le package GHCR privé de `' + component + '` est absent ou inaccessible au workflow.'
        next_step = 'Vérifiez sa visibilité **Private** et **Manage Actions access → gbaudrit/agentstration → Write**, puis relancez la commande.'
    lines = ['**' + OPERATIONS[operation] + ' — échec**', reason]
    if options.get('environment_id'):
        lines.append('Environnement : `' + options['environment_id'] + '`.')
    if preview.SHA.fullmatch(options.get('revision') or ''):
        lines.append('Révision demandée : ' + revision(repository, options['revision']) + '.')
    if operation == 'deploy':
        lines.append('Base demandée : **' + options.get('database', 'sqlite') + '**. Extensions : ' + extension_names(options.get('extensions', [])) + '.')
    lines.append('**À faire :** ' + next_step)
    if stage in ('boundary', 'select', 'recorded', 'announce'):
        lines.append('L’exécution du déploiement Azure n’a pas été lancée par cette tentative.')
    else:
        lines.append('Des ressources peuvent subsister après cet échec ; aucun lien d’accès non vérifié n’est publié.')
    return '\n\n'.join([*lines, '[Voir l’étape en échec](' + run_url + ').'])
