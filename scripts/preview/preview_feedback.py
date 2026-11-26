"""Operator summaries use public validated metadata; provider output is never echoed."""
from datetime import datetime, timezone
import re

import contract
import container_health
import preview

OPERATIONS = {'deploy': 'Deployment', 'status': 'Preview status', 'destroy': 'Removal'}


def revision(repository, sha):
    return '[' + sha[:12] + '](https://github.com/' + repository + '/commit/' + sha + ')' if preview.SHA.fullmatch(sha or '') else 'not resolved'


def extension_names(values):
    return ', '.join('`' + name.removeprefix('extension-') + '`' for name in values) or 'none — server and MAF worker only'


def actions():
    return ('**Commands:** `/preview status` to check health · `/preview` to update while preserving data · '
            '`/preview redeploy` to erase and recreate · '
            '`/preview destroy` to remove the preview **and its data**.')


def expiry(value):
    try:
        date = datetime.fromisoformat(value)
        if date.tzinfo is None:
            return 'not available'
        return date.astimezone(timezone.utc).strftime('%Y-%m-%d %H:%M UTC')
    except (TypeError, ValueError):
        return 'not available'


def details(request):
    lines = ['<details><summary>Exact images used</summary>', '', '| Component | Version | Image by digest |', '| --- | --- | --- |']
    for name, image in contract.images(request).items():
        lines.append('| ' + name + ' | ' + image.get('version', '—')[:120] + ' | `' + image['reference'] + '` |')
    return '\n'.join([*lines, '', '</details>'])


def accepted(options, repository, run_url):
    operation = options['operation']
    reset = options.get('reset_before_deploy', False)
    lines = ['**' + ('Redeployment' if reset else OPERATIONS[operation]) + ' — request received**',
             'Environment: `' + options['environment_id'] + '`.']
    if operation == 'deploy':
        lines += ['Server revision: ' + revision(repository, options['revision']) + '.',
                  'Database: **' + options['database'] + '**. Extensions: ' + extension_names(options.get('extensions', [])) + '.',
                  'Next: check published images and deployment settings. This request has not created a VM yet.']
        if reset:
            lines.append('**This command will delete the previous preview, including disks/IP and application data, then create a fresh environment.**')
    elif operation == 'destroy':
        lines.append('Full removal of this preview’s VM, disks/IP and application data has been requested.')
    else:
        lines.append('Reading the recorded environment; current labels and revisions do not change this check.')
    return '\n\n'.join([*lines, '[Follow progress](' + run_url + ').'])


def planned(request, operation, repository, run_url, reset_before_deploy=False):
    lines = ['**' + ('Redeployment' if reset_before_deploy else OPERATIONS[operation]) + ' — checks completed**',
             'Environment: `' + request['environmentId'] + '`.',
             'Server revision: ' + revision(repository, request['sourceSha']) + '.',
             'Database: **' + request['database'] + '**. Extensions: ' + extension_names(request.get('extensions', {})) + '.']
    if operation == 'deploy':
        target = request['target']
        lines += ['VM: **' + target['vmSize'] + '**, region: **' + target['region'] + '**.',
                  'The images are available. Deployment will start when its execution slot is available.',
                  '**Recorded lifetime: ' + str(request['ttlHours']) + ' h. Automatic expiry cleanup is not implemented yet; use `/preview destroy` after testing.**']
        if reset_before_deploy:
            lines.append('**The checked images will be deployed after full removal of the previous preview and its application data.**')
    elif operation == 'destroy':
        lines.append('Removal will delete this preview’s owned resources and **erase its application data**.')
    return '\n\n'.join([*lines, '[Workflow execution](' + run_url + ').', details(request)])


def health_table(result, request):
    services = list(contract.images(request)) if request else list(result.get('images', {}))
    values = result.get('health', {})
    preview.require(isinstance(values, dict) and set(values) <= set(services), 'Unexpected container health components.')
    lines = ['| Component | Container | Health | Restarts |', '| --- | --- | --- | --- |']
    for name in services:
        preview.require(isinstance(name, str) and re.fullmatch(r'server|worker|postgres|extension-[a-z][a-z0-9-]{0,31}', name),
                        'Invalid health component.')
        row = values.get(name)
        if row is None:
            lines.append('| `' + name + '` | not observed | not observed | — |')
            continue
        preview.require(isinstance(row, dict) and set(row) == {'state', 'health', 'restarts'}
                        and isinstance(row['state'], str) and row['state'] in container_health.STATES
                        and isinstance(row['health'], str) and row['health'] in container_health.HEALTH
                        and (row['restarts'] is None or type(row['restarts']) is int and 0 <= row['restarts'] <= 2147483647),
                        'Invalid container health snapshot.')
        lines.append('| `' + name + '` | ' + row['state'] + ' | ' + row['health'] + ' | ' +
                     (str(row['restarts']) if row['restarts'] is not None else '—') + ' |')
    return '\n'.join(lines) if services else 'Container health was not observed during this check.'


def completed(result, operation, repository, run_url, request=None, reset_before_deploy=False):
    state = result['state']
    titles = {'running': 'Preview ready', 'destroyed': 'Preview removed', 'absent': 'No active environment',
              'stopped': 'Preview stopped', 'unhealthy': 'Preview unavailable',
              'provisioning': 'Preview starting', 'drifted': 'Preview configuration needs attention'}
    if state == 'running' and not result.get('url'):
        titles[state] = 'Environment running — access not confirmed'
    lines = ['**' + titles[state] + '**', 'Environment: `' + result['environmentId'] + '`.']
    if result.get('url'):
        lines += ['[**Open Agentstration**](' + result['url'] + ')']
    if state == 'destroyed':
        lines.append('This preview’s VM, disks/IP and application data have been removed. Shared services are retained.')
    elif state == 'absent':
        lines.append('Use `/preview deploy` to create a preview.')
    else:
        sha = result.get('sourceSha') or (request or {}).get('sourceSha')
        if sha:
            lines.append('Server revision: ' + revision(repository, sha) + '.')
        if result.get('database'):
            lines.append('Database: **' + result['database'] + '**.')
        selected = (request or {}).get('extensions')
        if selected is None:
            selected = [name for name in result.get('images', {}) if name.startswith('extension-')]
        lines.append('Extensions: ' + extension_names(selected) + '.')
        if request:
            lines.append('VM: **' + request['target']['vmSize'] + '**, region: **' + request['target']['region'] + '**.')
        lines.append(health_table(result, request))
        if reset_before_deploy and state == 'running':
            lines.append('The previous preview and its application data were removed before this fresh deployment.')
        if state == 'running':
            if not result.get('url'):
                lines.append('The URL is withheld: this check could not confirm protected access.')
            lines += ['Recorded expiry: **' + expiry(result.get('expiresAt')) + '**.',
                      '**Automatic expiry cleanup is not implemented yet. Remove the preview after testing to release its resources.**']
        elif state == 'stopped':
            lines.append('Data is retained; disks/IP may still incur charges. Use `/preview destroy` to remove them.')
        else:
            lines.append('The URL is withheld until availability and protection are confirmed.')
        lines.append(actions())
    lines.append('[Execution details](' + run_url + ').')
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
        'boundary': ('The command failed authorization or consistency checks.',
                     'Check your repository access, the issue/PR open state and its labels, then post a new, unedited command.'),
        'select': ('Image or deployment setting checks failed.',
                   'Check private GHCR publications, their Actions access and the Azure environment settings.'),
        'recorded': ('The recorded environment could not be read.',
                     'Check that a deployment exists for this environment and inspect the saved-state reading step.'),
        'announce': ('The final check before execution failed.',
                     'Labels, issue/PR state or permissions may have changed. Check them and post a new command.'),
        'execute': ('The Azure operation or availability/protection checks failed.',
                    'Use `/preview status` to inspect remaining resources, then `/preview deploy` to retry or `/preview destroy` to clean up.'),
        'cancelled': ('Execution was cancelled.', 'Check the environment with `/preview status` before retrying.')}
    reason, next_step = reasons.get(stage, reasons['execute'])
    if code == 'image-unavailable':
        reason = 'The ' + ('server' if component == 'server' else 'MAF worker' if component == 'maf-worker' else '`' + component + '` extension') + ' image is unavailable for the requested selection.'
        next_step = ('Run **OCI images** with `extension=' + component + '` and `publish_preview=true` on the revision to publish, then retry `/preview deploy`. '
                     'Publishing an extension does not publish the PR’s server image.')
    elif code == 'registry-access':
        reason = 'The private GHCR package for `' + component + '` is missing or inaccessible to the workflow.'
        next_step = 'Check its **Private** visibility and **Manage Actions access → gbaudrit/agentstration → Write**, then retry the command.'
    reset = options.get('reset_before_deploy', False)
    lines = ['**' + ('Redeployment' if reset else OPERATIONS[operation]) + ' — failed**', reason]
    if options.get('environment_id'):
        lines.append('Environment: `' + options['environment_id'] + '`.')
    if preview.SHA.fullmatch(options.get('revision') or ''):
        lines.append('Requested revision: ' + revision(repository, options['revision']) + '.')
    if operation == 'deploy':
        lines.append('Requested database: **' + options.get('database', 'sqlite') + '**. Extensions: ' + extension_names(options.get('extensions', [])) + '.')
    lines.append('**Next action:** ' + next_step)
    if stage in ('boundary', 'select', 'recorded', 'announce'):
        lines.append('Azure deployment execution was not started by this attempt.')
    else:
        lines.append('Resources may remain after this failure; no unverified access URL is published.')
        if reset:
            lines.append('The previous preview and its data may already have been removed. Check `/preview status`; use `/preview` to retry deployment without another reset.')
    return '\n\n'.join([*lines, '[Inspect the failed step](' + run_url + ').'])
