#!/usr/bin/env python3
"""Promote required CI OCI archives from trusted main, without building images."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

import preview
import images
import manual
import reconcile
import extensions
import ci_images

CONTEXT = 'preview/publication/'


def required_components(pull):
    return ['server', 'maf-worker', *extensions.from_labels(pull.get('labels', []))]


def approval(github, number, sha=None, grant=None, component=None):
    pull = github.pull(number)
    value = github.approval(pull)
    preview.require(sha is None or value['sourceSha'] == sha, 'The PR head changed; this publication is stale.')
    preview.require(grant is None or value['optInEvent'] == grant, 'The preview opt-in changed; this publication is stale.')
    preview.require(component is None or component in required_components(pull), 'Component is not requested by this preview.')
    return value


def status(github, sha, component, state, description, run=None):
    preview.require(preview.SHA.fullmatch(sha) and state in ('pending', 'success', 'failure', 'error'), 'Invalid publication status.')
    preview.require(component in ('server', 'maf-worker') or extensions.ID.fullmatch(component), 'Invalid component status.')
    target = 'https://github.com/' + github.repository + ('/actions/runs/' + str(ci_images.positive(run)) if run else '/pulls')
    payload = {'state': state, 'context': CONTEXT + component, 'description': description[:140], 'target_url': target}
    cache = getattr(github, '_publication_statuses', {})
    if sha not in cache:
        latest = {}
        for record in github.pages('/commits/' + sha + '/statuses'):
            if record.get('creator', {}).get('login') == 'github-actions[bot]':
                latest.setdefault(record['context'], record)
        cache[sha] = latest
        github._publication_statuses = cache
    previous = cache[sha].get(payload['context'], {})
    if all(previous.get(key) == value for key, value in payload.items()):
        return
    github.call(github.root + '/statuses/' + sha, 'POST', payload)
    cache[sha][payload['context']] = payload


def dispatch(github, number, event=None):
    pull = github.pull(number)
    if event is not None:
        preview.require(event.get('number') == number, 'Unexpected PR event.')
        label = event.get('label', {}).get('name', '')
        if event.get('action') in ('labeled', 'unlabeled') and label != reconcile.LABEL and not label.startswith(extensions.PREFIX):
            return False
        if (event.get('pull_request', {}).get('head') or {}).get('sha') != pull.get('head', {}).get('sha'):
            return False
    enabled = (reconcile.LABEL in [item.get('name') for item in pull.get('labels', [])]
               and pull.get('state') == 'open' and not pull.get('draft'))
    if not enabled:
        if event and event.get('action') in ('unlabeled', 'closed', 'converted_to_draft'):
            sha = pull['head']['sha']
            preview.require(preview.SHA.fullmatch(sha) and (pull['head'].get('repo') or {}).get('full_name', '').lower() == github.repository,
                            'Unexpected disabled preview source.')
            latest = {}
            for record in github.pages('/commits/' + sha + '/statuses'):
                if record.get('context', '').startswith(CONTEXT) and record.get('creator', {}).get('login') == 'github-actions[bot]':
                    latest.setdefault(record['context'][len(CONTEXT):], record)
            for component, record in latest.items():
                if record['state'] in ('pending', 'failure', 'error'):
                    status(github, sha, component, 'success', 'Automatic preview publication is disabled.')
        return False
    value = github.approval(pull)
    if event and event.get('action') == 'unlabeled' and event.get('label', {}).get('name', '').startswith(extensions.PREFIX):
        status(github, value['sourceSha'], event['label']['name'][len(extensions.PREFIX):], 'success', 'Component deselected; no publication requested.')
    required = required_components(pull)
    try:
        context = ci_images.context(github.repository, github.call, number, value['sourceSha'])
    except ci_images.Waiting as wait:
        for component in required:
            status(github, value['sourceSha'], component, 'failure' if wait.failed else 'pending', str(wait))
        return False
    approval(github, number, value['sourceSha'], value['optInEvent'])
    changed = [component for component in required if component in context['components']]
    for component in required:
        if component not in changed:
            status(github, value['sourceSha'], component, 'success', 'Unchanged; reuse a published image by digest.', context['run'])
            continue
        status(github, value['sourceSha'], component, 'pending', 'CI validated; private publication queued.', context['run'])
        github.call(github.root + '/actions/workflows/oci-images.yml/dispatches', 'POST', {
            'ref': 'main', 'inputs': {'extension': component, 'publish_preview': True,
                                    'preview_pr': str(number), 'source_sha': value['sourceSha'],
                                    'ci_run_id': str(context['run']), 'ci_run_attempt': str(context['attempt'])}})
    if not changed:
        github.call(github.root + '/actions/workflows/preview-reconcile.yml/dispatches', 'POST',
                    {'ref': 'main', 'inputs': {'dry_run': False}})
    return True


def needs_publication(github, number, sha, component, run_id, attempt, selector=None):
    value = approval(github, number, sha, component=component)
    context = ci_images.context(github.repository, github.call, number, sha, run_id, attempt)
    preview.require(component in context['components'], 'Unchanged components reuse published images; no publication is required.')
    selector = selector or manual.PublishedImages(github.repository)
    entry = selector.component(component)
    selector.private_package(entry['imageName'])
    owner, repository = github.repository.split('/')
    reference = 'ghcr.io/' + owner + '/' + repository + '-' + entry['imageName'] + ':sha-' + sha
    result = subprocess.run(['skopeo', 'inspect', '--raw', 'docker://' + reference], capture_output=True, timeout=90)
    if result.returncode:
        preview.require(any(code in result.stderr.lower() for code in (b'manifest unknown', b'name unknown', b'404', b'not found')),
                        'Cannot inspect the private component image; check package read access.')
        context = ci_images.context(github.repository, github.call, number, sha, run_id, attempt, component)
        publish_image = True
    else:
        selector.select(component, sha)
        publish_image = False
    approval(github, number, sha, value['optInEvent'], component)
    return {'build': 'false', 'publish': str(publish_image).lower(), 'grant': str(value['optInEvent']),
            'artifact-id': str(context.get('artifact', '')), 'ci-run': str(context['run'])}


def publish(github, number, sha, grant, delivery_path, authfile, selector=None, *, component, run_id, attempt):
    approval(github, number, sha, grant, component)
    context = ci_images.context(github.repository, github.call, number, sha, run_id, attempt, component)
    selector = selector or manual.PublishedImages(github.repository)
    expected = selector.entry(component, sha)
    preview.require(delivery_path.stat().st_size <= 65536, 'Delivery receipt exceeds 64 KiB.')
    delivery = json.loads(delivery_path.read_text(encoding='utf-8'))
    preview.require(type(delivery.get('schemaVersion')) is int and delivery['schemaVersion'] == 1 and delivery.get('repository') == github.repository
                    and delivery.get('sourceSha') == sha and delivery.get('archive') == 'image.oci.tar'
                    and images.delivery_entry(delivery) == expected,
                    'Delivery does not match the approved repository, source and component catalogue.')
    preview.image_record(delivery, expected.get('kind', 'aep-extension'), 'ghcr.io')
    archive = delivery_path.parent / delivery['archive']
    preview.require(archive.resolve().parent == delivery_path.parent.resolve() and archive.is_file(), 'Invalid OCI archive path.')
    with archive.open('rb') as stream:
        preview.require(hashlib.file_digest(stream, 'sha256').hexdigest() == delivery['archiveSha256'], 'OCI archive checksum changed.')
    raw = manual.command(['skopeo', 'inspect', '--raw', 'oci-archive:' + str(archive)])
    preview.require('sha256:' + hashlib.sha256(raw).hexdigest() == delivery['digest'], 'OCI archive index differs from the validated digest.')
    config = json.loads(manual.command(['skopeo', '--override-os', 'linux', '--override-arch', 'amd64',
                                       'inspect', '--config', 'oci-archive:' + str(archive)]))
    labels = config.get('config', {}).get('Labels') or {}
    preview.require(config.get('os') == 'linux' and config.get('architecture') == 'amd64'
                    and labels.get('org.opencontainers.image.revision') == sha
                    and labels.get('org.opencontainers.image.source') == 'https://github.com/' + github.repository
                    and labels.get('org.opencontainers.image.version') == expected['version'], 'OCI archive identity does not match approved source.')
    owner, repository = github.repository.split('/')
    package = repository + '-' + selector.component(component)['imageName']
    destination = 'ghcr.io/' + owner + '/' + package + ':sha-' + sha
    images.private_package(github.repository, package)
    approval(github, number, sha, grant, component)
    ci_images.context(github.repository, github.call, number, sha, run_id, attempt, component)
    images.copy_archive(delivery_path, destination, authfile)
    images.private_package(github.repository, package)
    publication = {'image': destination, 'digest': delivery['digest'], 'sourceSha': sha,
                   'component': component, 'ciRun': context['run'], 'ciAttempt': context['attempt'], 'artifactId': context['artifact'],
                   'reference': destination.rsplit(':', 1)[0] + '@' + delivery['digest']}
    (delivery_path.parent / 'publication-ghcr.json').write_text(json.dumps(publication, indent=2) + '\n', encoding='utf-8')
    return publication


def report_failure(github, number, sha, grant=None, component='server'):
    approval(github, number, sha, grant, component)
    run = ci_images.positive(int(os.environ['GITHUB_RUN_ID']))
    url = 'https://github.com/' + github.repository + '/actions/runs/' + str(run)
    github.sync_status('pr-' + str(number), operation='deploy', outcome='error')
    github.publish('pr-' + str(number), '### Preview update blocked\n\n'
                   'Private publication of `' + component + '` failed for commit `' + sha + '`. '
                   'This attempt did not start a deployment or reset the existing environment.\n\n'
                   '[Inspect publication](' + url + '). Check private package access and the successful CI archive. '
                   'If the archive is missing or expired, rerun CI. Publication never rebuilds an image. '
                   'Then rerun **Preview image admission** on `main` with PR number `' + str(number) + '`.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('dispatch', 'gate', 'publish', 'report-result'))
    args = parser.parse_args()
    event_name = os.environ.get('GITHUB_EVENT_NAME')
    preview.require(os.environ.get('GITHUB_REF') == 'refs/heads/main'
                    and event_name in ('pull_request_target', 'workflow_dispatch', 'workflow_run'), 'Publication requires trusted main.')
    github = reconcile.GitHub(os.environ['GITHUB_REPOSITORY'])
    number = os.environ.get('PREVIEW_PR', '')
    event = None
    if event_name == 'pull_request_target':
        preview.require(args.action == 'dispatch', 'PR events may only admit metadata.')
        event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text())
        number = str(event['number'])
    elif event_name == 'workflow_run':
        preview.require(args.action == 'dispatch', 'Workflow completion may only admit metadata.')
        completed = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text())['workflow_run']
        preview.require(completed.get('event') == 'pull_request' and preview.SHA.fullmatch(completed.get('head_sha', ''))
                        and (completed.get('head_repository') or {}).get('full_name', '').lower() == github.repository,
                        'Unexpected completed PR workflow.')
        pulls = [p for p in github.pages('/commits/' + completed['head_sha'] + '/pulls')
                 if p.get('state') == 'open' and p.get('head', {}).get('sha') == completed['head_sha']
                 and reconcile.LABEL in [label.get('name') for label in p.get('labels', [])]]
        if not pulls:
            return
        preview.require(len(pulls) == 1, 'Multiple opted-in PRs share this source; use explicit PR admission.')
        number = str(pulls[0]['number'])
        event = {'number': int(number), 'action': 'workflow-completed', 'pull_request': pulls[0]}
    preview.require(number.isdecimal() and 1 <= int(number) <= 9999999999, 'Invalid PR number.')
    if args.action == 'dispatch':
        print('CI archive publication admitted.' if dispatch(github, int(number), event) else 'Waiting or no publication requested.')
        return
    sha = os.environ.get('SOURCE_SHA', '')
    preview.require(preview.SHA.fullmatch(sha), 'Expected an exact PR source SHA.')
    component = os.environ.get('EXTENSION_ID', '')
    if args.action == 'gate':
        values = needs_publication(github, int(number), sha, component, int(os.environ['CI_RUN_ID']), int(os.environ['CI_RUN_ATTEMPT']))
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            for key, value in values.items():
                output.write(key + '=' + value + '\n')
    elif args.action == 'publish':
        publish(github, int(number), sha, int(os.environ['OPT_IN_GRANT']), Path('delivery/delivery.json'),
                Path(os.environ['DOCKER_CONFIG']) / 'config.json', component=component,
                run_id=int(os.environ['CI_RUN_ID']), attempt=int(os.environ['CI_RUN_ATTEMPT']))
    else:
        grant = os.environ.get('OPT_IN_GRANT')
        approval(github, int(number), sha, int(grant) if grant else None, component)
        succeeded = os.environ.get('PUBLICATION_SUCCEEDED') == 'true'
        status(github, sha, component, 'success' if succeeded else 'failure',
               'Private source image verified and available.' if succeeded else 'Private publication failed; inspect the linked run.',
               int(os.environ['GITHUB_RUN_ID']))
        if not succeeded:
            report_failure(github, int(number), sha, int(grant) if grant else None, component)
        else:
            github.call(github.root + '/actions/workflows/preview-reconcile.yml/dispatches', 'POST',
                        {'ref': 'main', 'inputs': {'dry_run': False}})


if __name__ == '__main__':
    try:
        main()
    except ci_images.Waiting as wait:
        print(str(wait))
        raise SystemExit(1)
    except (ValueError, RuntimeError, KeyError, TypeError, AttributeError, OSError, subprocess.SubprocessError):
        print('Preview publication failed, is waiting for CI, or became stale. Remote content is withheld; check PR workflows and private package access.')
        raise SystemExit(1)
