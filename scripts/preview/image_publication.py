#!/usr/bin/env python3
"""Admit automatic server packaging from PR metadata; publish archives using trusted main tooling."""
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


def approval(github, number, sha=None, grant=None):
    value = github.approval(github.pull(number))
    preview.require(sha is None or value['sourceSha'] == sha, 'The PR head changed; waiting for its new publication.')
    preview.require(grant is None or value['optInEvent'] == grant, 'The preview opt-in changed; this publication is stale.')
    return value


def dispatch(github, number, event=None):
    # PR events are metadata only. Re-read live state rather than trusting the webhook payload.
    pull = github.pull(number)
    if reconcile.LABEL not in [item.get('name') for item in pull.get('labels', [])]:
        return False
    if event is not None:
        preview.require(event.get('number') == number, 'Unexpected PR event.')
        if event.get('action') == 'labeled' and event.get('label', {}).get('name') != reconcile.LABEL:
            return False
        if (event.get('pull_request', {}).get('head') or {}).get('sha') != pull.get('head', {}).get('sha'):
            return False  # A newer push has its own event; never dispatch an obsolete head.
    value = github.approval(pull)
    github.call(github.root + '/actions/workflows/oci-images.yml/dispatches', 'POST', {
        'ref': 'main', 'inputs': {'extension': 'server', 'publish_preview': True,
                                'preview_pr': str(number), 'source_sha': value['sourceSha']}})
    return True


def needs_build(github, number, sha, selector=None):
    value = approval(github, number, sha)
    selector = selector or manual.PublishedImages(github.repository)
    selector.private_package('server')
    reference = 'ghcr.io/' + github.repository.split('/')[0] + '/' + github.repository.split('/')[1] + '-server:sha-' + sha
    # Only a definite missing manifest permits a build. Authentication/registry outages fail closed.
    result = subprocess.run(['skopeo', 'inspect', '--raw', 'docker://' + reference], capture_output=True, timeout=90)
    if result.returncode:
        preview.require(any(code in result.stderr.lower() for code in (b'manifest unknown', b'name unknown', b'404', b'not found')),
                        'Cannot inspect the private server image; check package read access.')
        build = True
    else:
        selector.select('server', sha)  # Reuse platform, source, version and immutable-digest verification.
        build = False
    approval(github, number, sha, value['optInEvent'])
    return {'build': str(build).lower(), 'grant': str(value['optInEvent'])}


def publish(github, number, sha, grant, delivery_path, authfile, selector=None):
    approval(github, number, sha, grant)
    selector = selector or manual.PublishedImages(github.repository)
    expected = selector.entry('server', sha)  # Source catalogue/version read as data, never imported or executed.
    preview.require(delivery_path.stat().st_size <= 65536, 'Delivery receipt exceeds 64 KiB.')
    delivery = json.loads(delivery_path.read_text(encoding='utf-8'))
    preview.require(delivery.get('schemaVersion') == 1 and delivery.get('repository') == github.repository
                    and delivery.get('sourceSha') == sha and delivery.get('archive') == 'image.oci.tar'
                    and images.delivery_entry(delivery) == expected,
                    'Server delivery does not match the approved repository, source and catalogue.')
    preview.image_record(delivery, 'server', 'ghcr.io')
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
    package = repository + '-server'
    destination = 'ghcr.io/' + owner + '/' + package + ':sha-' + sha
    images.private_package(github.repository, package)
    approval(github, number, sha, grant)  # Revoke stale consent/head before the only registry write.
    images.copy_archive(delivery_path, destination, authfile)
    images.private_package(github.repository, package)
    publication = {'image': destination, 'digest': delivery['digest'], 'sourceSha': sha,
                   'reference': destination.rsplit(':', 1)[0] + '@' + delivery['digest']}
    (delivery_path.parent / 'publication-ghcr.json').write_text(json.dumps(publication, indent=2) + '\n', encoding='utf-8')
    return publication


def report_failure(github, number, sha, grant=None):
    approval(github, number, sha, grant)
    run = reconcile.commands.positive(int(os.environ['GITHUB_RUN_ID']))
    url = 'https://github.com/' + github.repository + '/actions/runs/' + str(run)
    github.sync_status('pr-' + str(number), operation='deploy', outcome='error')
    github.publish('pr-' + str(number), '### Preview update blocked\n\n'
                   'Server image packaging or publication failed for commit `' + sha + '`. '
                   'This attempt did not start a deployment or reset the existing environment.\n\n'
                   '[Inspect packaging checks and publication](' + url + '). '
                   'After correcting the failure, rerun **Preview image admission** on `main` with PR number `'
                   + str(number) + '`, or push a new commit. Independently versioned Worker/extension images must already be published.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('dispatch', 'gate', 'publish', 'report-failure'))
    args = parser.parse_args()
    event_name = os.environ.get('GITHUB_EVENT_NAME')
    preview.require(os.environ.get('GITHUB_REF') == 'refs/heads/main'
                    and event_name in ('pull_request_target', 'workflow_dispatch'), 'Automatic publication requires trusted main.')
    github = reconcile.GitHub(os.environ['GITHUB_REPOSITORY'])
    number = os.environ.get('PREVIEW_PR', '')
    event = None
    if event_name == 'pull_request_target':
        preview.require(args.action == 'dispatch', 'PR events may only dispatch metadata.')
        event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text())
        number = str(event['number'])
    preview.require(number.isdecimal() and 1 <= int(number) <= 9999999999, 'Invalid PR number.')
    if args.action == 'dispatch':
        print('Server image publication dispatched.' if dispatch(github, int(number), event) else 'No publication needed for this event.')
        return
    sha = os.environ.get('SOURCE_SHA', '')
    preview.require(preview.SHA.fullmatch(sha), 'Expected an exact PR source SHA.')
    if args.action == 'gate':
        values = needs_build(github, int(number), sha)
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            for key, value in values.items():
                output.write(key + '=' + value + '\n')
    elif args.action == 'publish':
        publish(github, int(number), sha, int(os.environ['OPT_IN_GRANT']), Path('delivery/delivery.json'),
                Path(os.environ['DOCKER_CONFIG']) / 'config.json')
    else:
        grant = os.environ.get('OPT_IN_GRANT')
        report_failure(github, int(number), sha, int(grant) if grant else None)


if __name__ == '__main__':
    try:
        main()
    except (ValueError, RuntimeError, KeyError, TypeError, AttributeError, OSError, subprocess.SubprocessError):
        print('Automatic server publication failed or became stale. Check current PR opt-in, head and private package access; remote content is withheld.')
        raise SystemExit(1)
