#!/usr/bin/env python3
"""Exercise the delivered Git executable through the real AEP provider, offline."""
import base64
import io
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import urllib.request
import uuid
import zipfile
from smoke import wait_for_health


def main():
    delivery = json.loads(Path('artifacts/extension/delivery.json').read_text())
    image = 'localhost:5000/extension@' + delivery['digest']
    name = 'git-boundary-' + uuid.uuid4().hex
    with tempfile.TemporaryDirectory(prefix='git-image-') as temporary:
        root = Path(temporary)
        repository = root / 'repository'
        repository.mkdir()
        subprocess.run(['git', 'init', '--initial-branch=main', str(repository)], check=True, stdout=subprocess.DEVNULL)
        (repository / 'fixture.txt').write_text('public disposable packaging fixture\n')
        for args in (['add', 'fixture.txt'], ['-c', 'user.name=Packaging fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-m', 'fixture']):
            subprocess.run(['git', '-C', str(repository), *args], check=True, stdout=subprocess.DEVNULL)
        revision = subprocess.check_output(['git', '-C', str(repository), 'rev-parse', 'HEAD'], text=True).strip()
        key = secrets.token_urlsafe(48)
        key_file = root / 'key'
        key_file.write_text(key)
        key_file.chmod(0o600)
        try:
            subprocess.run(['docker', 'run', '--detach', '--name', name, '--user', f'{os.getuid()}:{os.getgid()}',
                            '--publish', '127.0.0.1::8080', '--mount', f'type=bind,source={repository},target=/fixture,readonly',
                            '--mount', f'type=bind,source={key_file},target=/run/secrets/key,readonly',
                            '--tmpfs', '/scratch:rw,mode=1777,size=33554432',
                            '--env', 'Aep__EnrollmentMode=SharedKeyFile', '--env', 'Aep__SharedKeyFile__Path=/run/secrets/key',
                            '--env', 'GitSourceProvider__AllowLocalRepositories=true', '--env', 'GitSourceProvider__TemporaryDirectory=/scratch',
                            '--env', 'GitSourceProvider__MaximumRepositoryBytes=1048576', image], check=True, stdout=subprocess.DEVNULL)
            subprocess.run(['docker', 'exec', name, 'git', '--version'], check=True)
            for iteration in range(2):
                inspect = json.loads(subprocess.check_output(['docker', 'inspect', name]))[0]
                base = 'http://127.0.0.1:' + inspect['NetworkSettings']['Ports']['8080/tcp'][0]['HostPort']
                wait_for_health(base)
                def request(path, payload=None):
                    data = None if payload is None else json.dumps(payload).encode()
                    req = urllib.request.Request(base + path, data=data, headers={'Authorization': 'Bearer ' + key, 'Content-Type': 'application/json'})
                    with urllib.request.urlopen(req, timeout=30) as response:
                        return json.load(response)
                options = request('/aep/configuration')['optionSets'][0]
                contract = options['versions'][0]
                configuration = {'optionSet': options['id'], 'version': contract['version'], 'schemaDigest': contract['schemaDigest'],
                                 'values': {'repository': 'file:///fixture/', 'ref': 'refs/heads/main'}}
                resolved = request('/aep/source-providers/git/resolve', {'configuration': configuration})
                assert resolved['revision'] == revision
                result = request('/aep/source-providers/git/materialize', {'configuration': configuration, 'revision': revision,
                                 'limits': {'maxArchiveBytes': 1048576, 'maxEntries': 10, 'maxExpandedBytes': 1048576, 'timeoutSeconds': 10}})
                assert result['revision'] == revision
                with zipfile.ZipFile(io.BytesIO(base64.b64decode(result['archive']['content']))) as archive:
                    assert archive.namelist() == ['fixture.txt']
                    assert archive.read('fixture.txt') == b'public disposable packaging fixture\n'
                if iteration == 0:
                    subprocess.run(['docker', 'restart', name], check=True, stdout=subprocess.DEVNULL)
            evidence = {'sourceSha': delivery['sourceSha'], 'digest': delivery['digest'], 'gitExecutable': True,
                        'offlineResolveAndMaterialize': True, 'boundedScratch': True, 'restart': True}
            Path('artifacts/extension/git-smoke.json').write_text(json.dumps(evidence, indent=2) + '\n')
            print('Git image executable, bounded AEP materialization and restart passed.')
        finally:
            subprocess.run(['docker', 'rm', '--force', name], stdout=subprocess.DEVNULL, check=False)


if __name__ == '__main__':
    main()
