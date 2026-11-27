#!/usr/bin/env python3
"""Trusted-main preview reconciliation; GitHub supplies metadata, never executable deployment instructions."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import tempfile

import azure
import access
import cloudflare
import capacity
import contract
import extensions
import issue_commands as commands
import manual
import preview
import preview_feedback as feedback

LABEL = 'preview:auto'
REDEPLOY = 'preview:auto:redeploy'
KEEP = 'preview:keep'


class GitHub:
    def __init__(self, repository, call=commands.api):
        self.repository = commands.repository_name(repository)
        self.root = 'repos/' + self.repository
        self.call = call

    def pages(self, path):
        result = []
        for page in range(1, 11):
            items = self.call(self.root + path + ('&' if '?' in path else '?') + 'per_page=100&page=' + str(page))
            preview.require(isinstance(items, list) and len(items) <= 100, 'Invalid bounded GitHub inventory.')
            result += items
            if len(items) < 100:
                return result
        raise ValueError('GitHub inventory exceeds its bound; no automatic action is authorized.')

    def pull(self, number):
        value = self.call(self.root + '/pulls/' + str(number))
        preview.require(value.get('number') == number and (value.get('base', {}).get('repo') or {}).get('full_name', '').lower() == self.repository,
                        'Unexpected automatic PR context.')
        return value

    def label_grant(self, pull, label):
        if label not in [item.get('name') for item in pull.get('labels', [])]:
            return None
        events = self.pages('/issues/' + str(pull['number']) + '/events')
        grants = [item for item in events if item.get('event') == 'labeled' and item.get('label', {}).get('name') == label]
        if not grants:
            return None
        grant = grants[-1]
        if not commands.authorized({'user': grant.get('actor', {})}, self.repository, self.call):
            return None
        preview.require(isinstance(grant.get('created_at'), str) and datetime.fromisoformat(grant['created_at']).tzinfo is not None,
                        'Invalid preview label timestamp.')
        commands.positive(grant['id'])
        return grant

    def approval(self, pull):
        preview.require(pull.get('state') == 'open' and not pull.get('draft')
                        and (pull.get('head', {}).get('repo') or {}).get('full_name', '').lower() == self.repository,
                        'Automatic previews require open, non-draft, same-repository PRs.')
        preview.require(LABEL in [item.get('name') for item in pull.get('labels', [])], 'Automatic preview is not enabled.')
        grant = self.label_grant(pull, LABEL)
        preview.require(grant is not None,
                        'Automatic preview opt-in requires current maintainer write access.')
        sha = pull.get('head', {}).get('sha')
        preview.require(isinstance(sha, str) and preview.SHA.fullmatch(sha), 'Invalid automatic PR source SHA.')
        result = {'pullRequest': pull['number'], 'optInEvent': grant['id'], 'optInAt': grant['created_at'], 'sourceSha': sha}
        if REDEPLOY in [item.get('name') for item in pull.get('labels', [])]:
            reset = self.label_grant(pull, REDEPLOY)
            preview.require(reset is not None, 'Automatic reset requires current maintainer write access.')
            result['redeployEvent'] = reset['id']
        return result

    def kept(self, environment):
        match = re.fullmatch(r'(?:pr|issue)-([1-9][0-9]{0,9})', environment)
        if not match:
            return False
        number = int(match[1])
        value = self.pull(number) if environment.startswith('pr-') else self.call(self.root + '/issues/' + str(number))
        preview.require(value.get('number') == number, 'Unexpected preview retention context.')
        return self.label_grant(value, KEEP) is not None

    def sync_status(self, environment, result=None, **kwargs):
        commands.sync_status(self.repository, environment, result, self.call, **kwargs)

    def publish(self, environment, text):
        match = re.fullmatch(r'(?:pr|issue)-([1-9][0-9]{0,9})', environment)
        if not match:
            return  # Manual IDs have workflow feedback, not an invented GitHub conversation.
        number = int(match[1])
        marker = '<!-- agentstration-preview-automation:' + self.repository + ':' + environment + ' -->'
        body = marker + '\n' + text
        preview.require(len(body) <= 12000, 'Automatic preview feedback exceeds its bound.')
        comments = self.pages('/issues/' + str(number) + '/comments')
        cards = [c for c in comments if c.get('body', '').startswith(marker + '\n')
                 and c.get('user', {}).get('type') == 'Bot' and c['user'].get('login') == 'github-actions[bot]']
        preview.require(len(cards) <= 1, 'Multiple automatic preview cards require inspection.')
        if not cards:
            self.call(self.root + '/issues/' + str(number) + '/comments', 'POST', {'body': body})
        else:
            identifier = commands.positive(cards[0]['id'])
            current = self.call(self.root + '/issues/comments/' + str(identifier))
            preview.require(current.get('id') == identifier and current.get('issue_url', '').lower()
                            == 'https://api.github.com/' + self.root + '/issues/' + str(number)
                            and current.get('user', {}).get('login') == 'github-actions[bot]'
                            and current['user'].get('type') == 'Bot' and current.get('body', '').startswith(marker + '\n'),
                            'Automatic feedback ownership changed; refusing overwrite.')
            if current['body'] != body:
                self.call(self.root + '/issues/comments/' + str(identifier), 'PATCH', {'body': body})


def removal_reason(record, github, now):
    pending = record.get('deletion')
    if pending and pending['completedAt'] is None:
        return pending['reason']
    if record['destroyed']:
        return None
    if github.kept(record['request']['environmentId']):
        return None
    if datetime.fromisoformat(record['expiresAt']) <= now:
        return 'ttl'
    match = re.fullmatch(r'pr-([1-9][0-9]{0,9})', record['request']['environmentId'])
    if match:
        pull = github.pull(int(match[1]))
        if pull['state'] == 'closed':
            return 'pr-closed'
        if record.get('automation') and LABEL not in [item.get('name') for item in pull.get('labels', [])]:
            return 'label-removed'
    return None


def eligible_change(record, approval, selected):
    if record is None:
        return True
    previous = record.get('automation')
    changed_intent = not previous or any(previous.get(key) != approval.get(key) for key in ('sourceSha', 'optInEvent', 'redeployEvent'))
    if record['destroyed']:
        if previous:
            return changed_intent  # TTL/manual removal stays removed until a new head or explicit re-opt-in.
        deleted = (record.get('deletion') or {}).get('completedAt')
        return bool(deleted and datetime.fromisoformat(approval['optInAt']) > datetime.fromisoformat(deleted))
    return (changed_intent or not record['initialized'] or record.get('access') and not record['access']['verified']
            or list(record['request'].get('extensions', {})) != selected)


def inventory(variables):
    lookup = {'environmentId': 'inventory', 'target': {'subscriptionId': variables['AZURE_PREVIEW_SUBSCRIPTION_ID'],
                                                     'resourceGroup': variables['AZURE_PREVIEW_RESOURCE_GROUP']}}
    with tempfile.TemporaryDirectory(prefix='preview-inventory-') as directory:
        client = azure.Azure(lookup, directory)
        records = client.list_records()
        resources = client.az('resource', 'list', '--resource-group', lookup['target']['resourceGroup'])
        values = cloudflare.configuration(variables)
        api = access.API(values['API_TOKEN'])
        occupied, unknown = route_inventory(records, api.list('/accounts/' + values['ACCOUNT_ID'] + '/cfd_tunnel'),
                                            api.list('/zones/' + values['ZONE_ID'] + '/dns_records'), values['PREVIEW_DOMAIN'])
        return records, resources, occupied, unknown


def route_inventory(records, tunnels, dns, domain):
    names, hosts, occupied = {}, {}, set()
    for item in records:
        route = item['record'].get('access')
        if route:
            names[route['tunnelName']] = item['prefix']
            hosts[route['hostname']] = item['prefix']
    unknown = 0
    for tunnel in tunnels:
        if tunnel.get('deleted_at'):
            continue
        name = tunnel.get('name', '')
        if name in names:
            occupied.add(names[name])
        elif re.fullmatch(r'agentstration-[0-9a-f]{64}', name):
            unknown += 1
    for record in dns:
        name = record.get('name', '').lower().rstrip('.')
        if name in hosts:
            occupied.add(hosts[name])
        elif name.endswith('.' + domain) and (record.get('comment') or '').startswith('agentstration.preview.owner='):
            unknown += 1
    return occupied, unknown


def reconcile(github, records, resources, variables, apply=False, execute=azure.execute, selector=None, now=None, run_url='',
              route_prefixes=(), unknown_routes=0):
    now = now or datetime.now(timezone.utc)
    variables = dict(variables)  # Provider execution consumes process secrets; retain only this private in-memory copy.
    results, failures, pending = [], [], []
    known = {item['prefix'] for item in records}
    occupied = {r['name'][:23] for r in resources if re.match(r'^pv-[0-9a-f]{20}(?:-|/)', r.get('name', ''))} | set(route_prefixes)
    unknown_resources = bool(occupied - known)
    if unknown_resources:
        failures.append({'code': 'unrecorded-resources', 'action': 'restore-ownership-record'})
    if unknown_routes:
        failures.append({'code': 'unrecorded-routes', 'count': unknown_routes, 'action': 'restore-ownership-record'})
    current, observed_states = {}, {}
    def sync(environment, value=None, *, operation='status', outcome=None):
        if not apply:
            return
        try:
            github.sync_status(environment, value, operation=operation, outcome=outcome)
        except (ValueError, RuntimeError, KeyError, TypeError, OSError):
            failures.append({'environmentId': environment, 'code': 'status-labels-failed'})
    def report(environment, text):
        if apply:
            try:
                github.publish(environment, text)
            except (ValueError, RuntimeError, KeyError, TypeError, OSError):
                failures.append({'environmentId': environment, 'code': 'feedback-failed'})
    for item in records:
        record = item['record']
        request = record['request']
        if any(value['repository'].lower() != github.repository for value in request['deliveries'].values()):
            continue
        environment = request['environmentId']
        current[environment] = record
        try:
            reason = removal_reason(record, github, now)
            if record['destroyed'] and reason is None and item['prefix'] in occupied:
                reason = (record.get('deletion') or {}).get('reason', 'manual')
            if not reason:
                if apply:
                    try:
                        value = execute('status', request) if not record['destroyed'] else {'state': 'destroyed'}
                        observed_states[environment] = value
                        sync(environment, value)
                    except (ValueError, RuntimeError, KeyError, TypeError, OSError):
                        observed_states[environment] = {'state': 'unhealthy'}
                        failures.append({'environmentId': environment, 'code': 'status-unavailable'})
                        sync(environment, outcome='error')
                continue
            results.append({'environmentId': environment, 'operation': 'destroy', 'reason': reason})
            if apply:
                def guard(latest, old=record, expected=reason):
                    return latest['ownerId'] == old['ownerId'] and (latest['destroyed'] or removal_reason(latest, github, now) == expected)
                # Rechecking the owner and reason under the lease prevents deleting a newly recreated preview.
                sync(environment, operation='destroy', outcome='pending')
                value = execute('destroy', {**request, 'dataMode': 'reset'}, deletion_reason=reason, guard=guard)
                current[environment] = {**record, 'destroyed': True}
                if value.get('deletion'):
                    current[environment]['deletion'] = value['deletion']
                sync(environment, value, operation='destroy', outcome='success')
                report(environment, feedback.completed(value, 'destroy', github.repository, run_url, request))
        except (ValueError, RuntimeError, KeyError, TypeError, OSError):
            failures.append({'environmentId': environment, 'code': 'cleanup-failed'})
            sync(environment, operation='destroy', outcome='error')
            if apply:
                try:
                    sync(environment, execute('status', request))
                except (ValueError, RuntimeError, KeyError, TypeError, OSError):
                    pass  # Unknown status cannot justify a label change; the next reconciliation retries.
            report(environment, '**Preview cleanup incomplete**\n\nFull removal is not confirmed; owned resources may remain. '
                   'Check `/preview status` or retry `/preview destroy`.\n\n' + run_url)
    if unknown_resources:
        return {'schemaVersion': 1, 'dryRun': not apply, 'actions': results, 'failures': failures, 'pending': pending}
    try:
        pulls = github.pages('/pulls?state=open')
    except (ValueError, RuntimeError, KeyError, TypeError, OSError):
        failures.append({'code': 'pr-inventory-unavailable'})
        return {'schemaVersion': 1, 'dryRun': not apply, 'actions': results, 'failures': failures, 'pending': pending}
    for summary in pulls:
        if LABEL not in [item.get('name') for item in summary.get('labels', [])]:
            continue
        number = commands.positive(summary['number'])
        environment = 'pr-' + str(number)
        stage = 'authorization'
        try:
            pull = github.pull(number)
            approval = github.approval(pull)
            selected = extensions.from_labels(pull.get('labels', []))
            previous = current.get(environment)
            if previous and (removal_reason(previous, github, now) or previous.get('deletion', {}).get('completedAt', True) is None):
                continue
            if not eligible_change(previous, approval, selected):
                if not apply or previous['destroyed']:
                    continue
                try:
                    observed = observed_states.get(environment) or execute('status', previous['request'])
                except (ValueError, RuntimeError, KeyError, TypeError, OSError):
                    observed = {'state': 'unhealthy'}
                if observed['state'] == 'running' and (not previous.get('access') or observed.get('url')):
                    diagnostics = {'preview:status:pending', 'preview:status:error'}
                    if not diagnostics.intersection(item.get('name') for item in pull.get('labels', [])):
                        report(environment, feedback.completed(observed, 'status', github.repository, run_url, previous['request']))
                    continue
                # Restore an owned opted-in preview through ordinary deploy, never through implicit reset.
            stage = 'selection'
            sync(environment, operation='deploy', outcome='pending')
            # Reset a newly requested update once. Health/startup retries of the same intent preserve data.
            old_intent = (previous or {}).get('automation') or {}
            reset = bool(approval.get('redeployEvent') and previous and not previous['destroyed'] and (
                any(old_intent.get(key) != approval.get(key) for key in ('sourceSha', 'optInEvent', 'redeployEvent'))
                or list(previous['request'].get('extensions', {})) != selected))
            options = {**commands.parse('/preview deploy'), 'environment_id': environment,
                       'revision': approval['sourceSha'], 'extensions': selected}
            if previous and not previous['destroyed']:
                options['database'] = previous['request']['database']
                options['ttl_hours'] = previous['request']['ttlHours']
            selection_variables = dict(variables)
            if previous and not previous['destroyed']:
                target = contract.azure_target(previous['request'])
                selection_variables.update(AZURE_PREVIEW_REGION=target['region'], AZURE_PREVIEW_IMAGE_VERSION=target['imageVersion'],
                    AZURE_PREVIEW_OS_PROFILE=target['osProfile'], AZURE_PREVIEW_VM_SIZE1=target['vmSize'])
            request = manual.resolve(options, selection_variables, github.repository, selector)
            if previous and not previous['destroyed']:
                request['target'] = previous['request']['target']
                contract.validate(request)
            results.append({'environmentId': environment, 'operation': 'redeploy' if reset else 'deploy',
                            'sourceSha': approval['sourceSha'], 'extensions': selected})
            if apply:
                stage = 'deployment'
                def guard(latest, expected=approval, ids=selected, old=previous, desired=request):
                    if old and (latest is None or latest['ownerId'] != old['ownerId']):
                        return False
                    if old is None and latest and (latest['initialized'] or latest['request'] != desired):
                        return False
                    fresh = github.pull(expected['pullRequest'])
                    return github.approval(fresh) == expected and extensions.from_labels(fresh.get('labels', [])) == ids
                report(environment, feedback.planned(request, 'deploy', github.repository, run_url, reset))
                value = execute('deploy', request, automation_context=approval, guard=guard,
                                reset_before_deploy=reset,
                                extension_configuration=variables.get('PREVIEW_EXTENSION_CONFIGURATION') or '{}')
                sync(environment, value, operation='deploy', outcome='success')
                report(environment, feedback.completed(value, 'deploy', github.repository, run_url, request, reset))
        except manual.SelectionError as error:
            target = pending if error.public['code'] == 'image-unavailable' else failures
            target.append({'environmentId': environment, 'code': error.public['code'], 'component': error.public['component']})
            text = feedback.failed('deploy', github.repository, run_url, 'select', options, error.public)
            sync(environment, operation='deploy', outcome='pending' if target is pending else 'error')
            if target is pending:
                text = text.replace('**Deployment — failed**', '**Automatic preview — waiting for images**')
            report(environment, text)
        except capacity.Unavailable:
            pending.append({'environmentId': environment, 'code': 'capacity'})
            sync(environment, operation='deploy', outcome='pending')
            report(environment, '**Automatic preview — waiting for capacity**\n\nRemove an unused preview or wait for scheduled cleanup.\n\n' + run_url)
        except (ValueError, RuntimeError, KeyError, TypeError, OSError):
            failures.append({'environmentId': environment, 'code': 'deployment-not-authorized-or-failed', 'stage': stage})
            sync(environment, operation='deploy', outcome='error')
            messages = {
                'authorization': 'Check that the PR is open, non-draft and from this repository, and that the latest `preview:auto` and reset-mode grants came from a human with current write access. Deployment was not started.',
                'selection': 'Image or environment setting checks failed. Check exact private image publication and the protected preview defaults. Azure deployment was not started.',
                'deployment': 'Azure deployment or protected-access verification failed. Resources may remain; check `/preview status`, then retry `/preview` or clean up with `/preview destroy`.'}
            report(environment, '**Automatic preview not deployed**\n\n' + messages[stage] + '\n\n' + run_url)
    return {'schemaVersion': 1, 'dryRun': not apply, 'actions': results, 'failures': failures, 'pending': pending}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    preview.require(os.environ.get('GITHUB_REF') == 'refs/heads/main' and os.environ.get('GITHUB_EVENT_NAME')
                    in ('schedule', 'workflow_dispatch', 'workflow_run'), 'Reconciliation requires trusted main tooling.')
    github = GitHub(os.environ['GITHUB_REPOSITORY'])
    records, resources, route_prefixes, unknown_routes = inventory(os.environ)
    run_url = 'https://github.com/' + github.repository + '/actions/runs/' + str(commands.positive(int(os.environ['GITHUB_RUN_ID'])))
    result = reconcile(github, records, resources, os.environ, apply=args.apply, run_url=run_url,
                       route_prefixes=route_prefixes, unknown_routes=unknown_routes)
    preview.private_write(args.output, json.dumps(result, indent=2) + '\n')
    with open(os.environ['GITHUB_STEP_SUMMARY'], 'a') as summary:
        summary.write('Preview reconciliation (' + ('apply' if args.apply else 'dry run') + '): ' + str(len(result['actions']))
                      + ' actions; ' + str(len(result.get('pending', []))) + ' pending; ' + str(len(result['failures'])) + ' failed checks.\n')
    return 1 if result['failures'] else 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (ValueError, RuntimeError, KeyError, TypeError, OSError):
        print('Preview reconciliation failed. Inspect bounded results and trusted scope; remote content is withheld.')
        raise SystemExit(1)
