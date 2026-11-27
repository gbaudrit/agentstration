#!/usr/bin/env python3
"""Reuse the native ASC/MCP consumer against the exact delivered OpenDAL image."""
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import uuid
from smoke import wait_for_health


def main():
    delivery = json.loads(Path('artifacts/extension/delivery.json').read_text())
    image = 'localhost:5000/extension@' + delivery['digest']
    name = 'opendal-boundary-' + uuid.uuid4().hex
    subprocess.run(['dotnet', 'build', 'samples/OpenDalSmoke', '--configuration', 'Release'], check=True)
    sample = Path('samples/OpenDalSmoke/bin/Release/net10.0/OpenDalSmoke.dll').resolve()
    with tempfile.TemporaryDirectory(prefix='opendal-image-') as temporary:
        root = Path(temporary)
        data = root / 'content'
        data.mkdir()
        key = root / 'key'
        key.write_text(secrets.token_urlsafe(48))
        key.chmod(0o600)
        try:
            subprocess.run(['docker', 'run', '--detach', '--name', name, '--user', f'{os.getuid()}:{os.getgid()}',
                            '--publish', '127.0.0.1::8080', '--mount', f'type=bind,source={data},target=/data',
                            '--mount', f'type=bind,source={key},target=/run/secrets/key,readonly',
                            '--env', 'OpenDal__RootDirectory=/data', '--env', 'OpenDal__AllowedClients__0=smoke',
                            '--env', 'Aep__EnrollmentMode=SharedKeyFile', '--env', 'Aep__SharedKeyFile__ClientId=smoke',
                            '--env', 'Aep__SharedKeyFile__Path=/run/secrets/key', image], check=True, stdout=subprocess.DEVNULL)
            for file in ('libopendal_dotnet.so', 'licenses/opendal/LICENSE', 'licenses/opendal/NOTICE',
                         'licenses/opendal/LICENSE-MPL-2.0.txt', 'licenses/opendal/LICENSE-CC0-1.0.txt', 'licenses/opendal/README.md'):
                subprocess.run(['docker', 'exec', name, 'test', '-f', '/app/' + file], check=True)
            for iteration in range(2):
                inspect = json.loads(subprocess.check_output(['docker', 'inspect', name]))[0]
                base = 'http://127.0.0.1:' + inspect['NetworkSettings']['Ports']['8080/tcp'][0]['HostPort']
                wait_for_health(base)
                command = ['dotnet', str(sample), base, str(key)]
                if iteration:
                    command.append('--read-only')
                subprocess.run(command, check=True, timeout=60)
                if not iteration:
                    subprocess.run(['docker', 'restart', name], check=True, stdout=subprocess.DEVNULL)
            Path('artifacts/extension/opendal-smoke.json').write_text(json.dumps({
                'sourceSha': delivery['sourceSha'], 'digest': delivery['digest'], 'nativeAssetsAndLicenses': True,
                'ascMcpWriteReadList': True, 'persistedContentAfterRestart': True}, indent=2) + '\n')
            print('OpenDAL image native assets, licenses, ASC/MCP operations and persisted restart passed.')
        finally:
            subprocess.run(['docker', 'rm', '--force', name], check=False, stdout=subprocess.DEVNULL)


if __name__ == '__main__':
    main()
