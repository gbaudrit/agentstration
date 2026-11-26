#!/usr/bin/env python3
"""Resolve a manual selection as data; never execute the selected source revision."""
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import quote
import xml.etree.ElementTree as ET

import contract
import extensions
import github_request
import preview
from images import catalogue, DIGEST, valid_version


class Unavailable(ValueError):
    """A registry/API read failed; its potentially sensitive output is withheld."""


class SelectionError(ValueError):
    """Controlled diagnosis suitable for a public operator comment."""
    def __init__(self, code, component, message):
        super().__init__(message)
        self.public = {'code': code, 'component': component}


def command(arguments):
    # Bound memory before decoding external metadata; commands also have a deadline.
    import tempfile
    with tempfile.TemporaryFile() as output, tempfile.TemporaryFile() as errors:
        try:
            result = subprocess.run(arguments, stdout=output, stderr=errors, timeout=90)
        except subprocess.TimeoutExpired as error:
            raise Unavailable('Metadata lookup timed out; retry or check repository/package access.') from error
        if result.returncode:
            raise Unavailable('Metadata lookup failed; check the revision, publication and repository/package read access.')
        preview.require(output.tell() <= 4 * 1024 * 1024, 'Remote metadata exceeds the 4 MiB limit.')
        output.seek(0)
        return output.read()


class PublishedImages:
    def __init__(self, repository, run=command):
        preview.require(isinstance(repository, str) and re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository),
                        'Invalid source repository.')
        self.repository, self.run = repository.lower(), run
        self.owner, self.name = self.repository.split('/')
        self.package_root = None

    def api(self, path):
        return json.loads(self.run(['gh', 'api', path]))

    def revision(self, ref):
        preview.require(isinstance(ref, str) and re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._/:-]{0,199}', ref),
                        'Use a branch, tag, full commit SHA or pr:NUMBER.')
        if ref.startswith('pr:'):
            preview.require(re.fullmatch(r'pr:[1-9][0-9]{0,9}', ref), 'Invalid PR revision; use pr:NUMBER.')
            pull = self.api(f'repos/{self.repository}/pulls/{ref[3:]}')
            preview.require((pull['head'].get('repo') or {}).get('full_name', '').lower() == self.repository,
                            'Fork PR previews are not enabled; select a revision in this repository.')
            sha = pull['head']['sha']
        else:
            sha = self.api(f'repos/{self.repository}/commits/{quote(ref, safe="")}')['sha']
        preview.require(isinstance(sha, str) and preview.SHA.fullmatch(sha), 'GitHub did not return an exact source SHA.')
        return sha

    def source_file(self, path, sha):
        value = self.api(f'repos/{self.repository}/contents/{path}?ref={sha}')
        preview.require(value.get('encoding') == 'base64' and value.get('type') == 'file',
                        'The published source must contain the supported image catalogue/version file.')
        result = base64.b64decode(value['content'], validate=False)
        preview.require(len(result) <= 65536, 'Source metadata exceeds 64 KiB.')
        return result.decode('utf-8')

    def component(self, identifier):
        if identifier in ('server', 'maf-worker'):
            return catalogue(identifier)
        extensions.validate_ids([identifier])
        if extensions.catalogue_path(identifier).exists():
            return extensions.entry(identifier)
        # Only the fixed AEP recipe/namespace is available before catalogue merge.
        return {'id': identifier, 'kind': 'aep-extension', 'imageName': 'extension-' + identifier, 'platform': 'linux/amd64'}

    def entry(self, component, sha):
        trusted = self.component(component)
        family = 'applications' if component == 'server' else 'workers' if trusted.get('kind') == 'awp-worker' else 'extensions'
        entry = contract.parse(self.source_file(f'deploy/images/{family}/{component}.json', sha))
        preview.require(isinstance(entry, dict) and type(entry.get('schemaVersion')) is int and entry['schemaVersion'] == 1
                        and entry.get('kind', 'aep-extension') == trusted.get('kind', 'aep-extension')
                        and all(entry.get(key) == trusted[key] for key in ('id', 'imageName', 'platform'))
                        and ('versionFile' not in trusted or entry.get('versionFile') == trusted['versionFile'])
                        and (family != 'extensions' or all(entry.get(key) == trusted[key] for key in extensions.METADATA if key in trusted)),
                        'Published source does not match the supported component catalogue.')
        if family == 'extensions':
            preview.require(isinstance(entry.get('versionFile'), str)
                            and re.fullmatch(r'src/extensions/[A-Za-z][A-Za-z0-9_.-]*/version\.txt', entry['versionFile']),
                            'Extension versions must use a bounded source version.txt path.')
        text = self.source_file(entry['versionFile'], sha)
        if component == 'server':
            preview.require('<!DOCTYPE' not in text.upper() and '<!ENTITY' not in text.upper(), 'XML entities are not source version metadata.')
            properties = ET.fromstring(text)
            prefix = properties.findtext('./PropertyGroup/VersionPrefix') or ''
            suffix = properties.findtext('./PropertyGroup/VersionSuffix')
            version = prefix + ('-' + suffix if suffix else '')
        else:
            version = text.strip()
        preview.require(valid_version(version), 'Published source has no supported SemVer version.')
        value = {**entry, 'version': version}
        if family == 'extensions':
            preview.image_record({'schemaVersion': 1, 'component': value, 'repository': self.repository,
                                  'sourceSha': sha, 'digest': 'sha256:' + '0' * 64}, 'aep-extension', 'ghcr.io')
        return value

    def private_package(self, image_name):
        if self.package_root is None:
            owner_type = self.api(f'repos/{self.repository}')['owner']['type']
            preview.require(owner_type in ('User', 'Organization'), 'Unsupported package owner.')
            self.package_root = ('orgs' if owner_type == 'Organization' else 'users') + '/' + self.owner + '/packages/container/'
        root = self.package_root + self.name + '-' + image_name
        preview.require(self.api(root).get('visibility') == 'private', 'Preview images must be private GHCR packages.')
        return root

    def raw(self, reference):
        return self.run(['skopeo', 'inspect', '--raw', 'docker://' + reference])

    def inspect(self, reference):
        return json.loads(self.run(['skopeo', '--override-os', 'linux', '--override-arch', 'amd64',
                                   'inspect', '--config', 'docker://' + reference]))

    def select(self, component, ref):
        trusted = self.component(component)
        try:
            package = self.private_package(trusted['imageName'])
        except Unavailable as error:
            raise SelectionError('registry-access', component, 'Private GHCR package metadata is unavailable.') from error
        image = f'ghcr.io/{self.owner}/{self.name}-{trusted["imageName"]}'
        sha = None
        if ref == 'published':
            preview.require(trusted.get('kind', 'aep-extension') in ('awp-worker', 'aep-extension'), 'Only independently versioned Workers and extensions accept published.')
            try:
                digest = self.latest_tagged(package)
            except Unavailable as error:
                raise SelectionError('registry-access', component, 'Private GHCR image versions are unavailable.') from error
            except ValueError as error:
                raise SelectionError('image-unavailable', component, 'No tagged component image found. Publish the component or select its explicit published revision.') from error
        else:
            sha = self.revision(ref)
            entry = self.entry(component, sha)
            try:
                manifest = self.raw(image + ':sha-' + sha)
            except Unavailable:
                # Official component releases use SemVer tags. Never accept another source SHA.
                try:
                    manifest = self.raw(image + ':' + entry['version'])
                except Unavailable as error:
                    raise SelectionError('image-unavailable', component, 'No published image for the selected revision. Publish this component with OCI images, then retry; deployment never builds images.') from error
            digest = 'sha256:' + hashlib.sha256(manifest).hexdigest()
        preview.require(isinstance(digest, str) and DIGEST.fullmatch(digest), 'Package version is not an exact OCI digest.')
        reference = image + '@' + digest
        preview.require('sha256:' + hashlib.sha256(self.raw(reference)).hexdigest() == digest,
                        'Registry manifest differs from the frozen index digest.')
        config = self.inspect(reference)
        labels = config.get('config', {}).get('Labels') or {}
        actual_sha = labels.get('org.opencontainers.image.revision', '')
        if not preview.SHA.fullmatch(actual_sha) or sha is not None and sha != actual_sha:
            raise SelectionError('image-unavailable', component, 'Published image source SHA differs from the requested revision; publish that exact revision.')
        entry = self.entry(component, actual_sha) if sha is None else entry
        preview.require(config.get('os') == 'linux' and config.get('architecture') == 'amd64'
                        and labels.get('org.opencontainers.image.source', '').lower() == 'https://github.com/' + self.repository
                        and labels.get('org.opencontainers.image.version') == entry['version'],
                        'Published image platform, source repository or version does not match its source catalogue.')
        return contract.delivery({'schemaVersion': 1, 'component': entry, 'repository': self.repository,
                                  'sourceSha': actual_sha, 'digest': digest})

    def latest_tagged(self, package):
        # Versions are returned newest-first by GitHub. Ignore untagged attestation manifests.
        for page in range(1, 6):
            versions = self.api(package + f'/versions?per_page=100&page={page}')
            preview.require(isinstance(versions, list), 'Invalid package version inventory.')
            for version in versions:
                tags = version.get('metadata', {}).get('container', {}).get('tags', [])
                if any(isinstance(tag, str) and (re.fullmatch(r'sha-[0-9a-f]{40}', tag) or valid_version(tag)) for tag in tags):
                    return version['name']
            if len(versions) < 100:
                break
        raise ValueError('No tagged component image found. Publish the component or select its explicit published revision.')


def validate_options(options):
    extensions.validate_ids(options.get('extensions', []))
    preview.require(options['operation'] in contract.OPERATIONS and options['provider'] == 'azure-vm', 'Only the Azure VM provider is available in this form.')
    preview.require(preview.ENVIRONMENT_ID.fullmatch(options['environment_id']), 'Use a lowercase environment ID, at most 40 characters.')
    preview.require(options['database'] in ('sqlite', 'postgresql') and options['test_profile'] in ('none', 'readiness'), 'Unsupported database or test profile.')
    preview.require(options['data_mode'] in ('preserve', 'reset'), 'Unsupported data mode.')
    preview.require(options['operation'] == 'destroy' or options['data_mode'] == 'preserve', 'Reset is explicit destroy only; destroy with reset before deploying.')
    preview.require(type(options.get('reset_before_deploy', False)) is bool
                    and (not options.get('reset_before_deploy') or options['operation'] == 'deploy'),
                    'Reset-before-deploy is a boolean available only for deployment.')
    preview.require(options['vm_size_profile'] in ('1', '2', '3', '4', '5'), 'Select a configured size slot from 1 to 5.')
    ttl = str(options['ttl_hours'])
    if type(options['ttl_hours']) is float and options['ttl_hours'].is_integer():
        ttl = str(int(options['ttl_hours']))
    preview.require(re.fullmatch(r'[0-9]{1,3}', ttl) and 1 <= int(ttl) <= 168, 'TTL must be a whole number from 1 to 168 hours.')
    return int(ttl)


def resolve(options, variables, repository, selector=None):
    ttl_hours = validate_options(options)
    preview.require(options['operation'] in ('deploy', 'validate'), 'Status/destroy must load the recorded request.')
    # Validate all target/default choices before consulting GitHub or GHCR.
    target = {'subscriptionId': variables.get('AZURE_PREVIEW_SUBSCRIPTION_ID', ''),
              'resourceGroup': variables.get('AZURE_PREVIEW_RESOURCE_GROUP', ''),
              'region': variables.get('AZURE_PREVIEW_REGION') or 'westeurope'}
    target = github_request.resolve_target(target, variables, options['vm_size_profile'])
    contract.validate_azure_target(target)
    arguments = {'environment_id': options['environment_id'], 'database': options['database'], 'provider': options['provider'],
                 'target': target, 'ttl_hours': ttl_hours, 'data_mode': options['data_mode'], 'test_profile': options['test_profile']}
    selector = selector or PublishedImages(repository)
    return contract.plan(selector.select('server', options['revision']), selector.select('maf-worker', options['worker_revision']),
                         extension_deliveries={identifier: selector.select(identifier, 'published') for identifier in options.get('extensions', [])},
                         **arguments)


def main():
    try:
        options = contract.parse(os.environ['SELECTION_JSON'])
        validate_options(options)
        repository = os.environ['GITHUB_REPOSITORY']
        if options['operation'] in ('status', 'destroy'):
            import azure
            request = azure.recorded_request(options['environment_id'], os.environ['AZURE_PREVIEW_SUBSCRIPTION_ID'],
                                             os.environ['AZURE_PREVIEW_RESOURCE_GROUP'], repository)
            if options['operation'] == 'destroy':
                request['dataMode'] = options['data_mode']
        else:
            request = resolve(options, os.environ, repository)
        contract.operation(options['operation'], request)
        preview.private_write(Path(os.environ['RUNNER_TEMP']) / 'manual-request.json', json.dumps(request) + '\n')
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            output.write('request=' + json.dumps(request) + '\n')
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a') as summary:
            summary.write('Frozen request for `' + request['environmentId'] + '`; operation `' + options['operation'] + '`.\n\n')
            summary.write('Source: `' + request['sourceSha'] + '`; database: `' + request['database'] + '`; VM: `' + request['target']['vmSize'] + '`.\n\n')
            for name, value in contract.images(request).items():
                summary.write('- ' + name + ': `' + value['reference'] + '`\n')
            summary.write('\nURL is returned only after protected readiness checks. TTL is recorded; automatic expiry is #1286.\n')
    except (ValueError, KeyError, TypeError, RuntimeError, ET.ParseError) as error:
        if isinstance(error, SelectionError):
            with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
                output.write('failure=' + json.dumps(error.public) + '\n')
        # Only controlled validation messages may be shown; remote JSON/key/parser errors are not echoed.
        print(str(error) if isinstance(error, (ValueError, RuntimeError)) and not isinstance(error, (json.JSONDecodeError, ET.ParseError, UnicodeError))
              else 'Invalid published metadata or manual selection; check the supported catalogue and form inputs.', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
