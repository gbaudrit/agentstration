"""Root-only guest connector and credential-stripping loopback proxy, outside the application."""
import json
from pathlib import Path
import re
import time
import urllib.request
import uuid

import lifecycle
import preview

LABEL = 'io.agentstration.preview.access-owner'
ROLES = ('connector', 'proxy')


def proxy_configuration(hostname):
    preview.require(re.fullmatch(r'dev-[a-z][a-z0-9-]{0,39}(\.[a-z0-9-]+){2,10}', hostname), 'Invalid proxy hostname.')
    return '''{
    admin off
    auto_https off
    persist_config off
}
http://:5081 {
    bind 127.0.0.1
    reverse_proxy 127.0.0.1:5080 {
        header_up -Cf-Access-*
        header_up Cookie "(?i)(^|;)[ \\t]*CF_Authorization[ \\t]*=[^;]*" ""
        header_up Host HOSTNAME
        header_up X-Forwarded-Host HOSTNAME
        header_up X-Forwarded-Proto https
    }
}
'''.replace('HOSTNAME', hostname)


def containers(owner):
    uuid.UUID(owner)
    result = {}
    for role in ROLES:
        name = 'agentstration-preview-access-' + role
        ids = lifecycle.docker('ps', '--all', '--filter', 'name=^/' + name + '$', '--format', '{{.ID}}')
        if ids:
            preview.require('\n' not in ids, 'Ambiguous access container identity.')
            item = json.loads(lifecycle.docker('inspect', ids))[0]
            preview.require(item['Config'].get('Labels', {}).get(LABEL) == owner,
                            'Foreign access container; refusing replacement or deletion.')
            result[role] = item
    return result


def stop(root, owner):
    # Inspect all roles first: never partly remove a foreign installation.
    current = containers(owner)
    for role in ROLES:
        if role in current:
            lifecycle.docker('rm', '--force', current[role]['Id'])
    for name in ('tunnel-token', 'Caddyfile'):
        (Path(root) / name).unlink(missing_ok=True)


def start(root, config):
    preview.require(isinstance(config, dict) and set(config) == {'ownerId', 'hostname', 'token'}, 'Invalid protected connector input.')
    uuid.UUID(config['ownerId'])
    preview.require(isinstance(config['token'], str) and 16 <= len(config['token']) <= 4096
                    and not any(c.isspace() for c in config['token']), 'Invalid connector credential.')
    content = proxy_configuration(config['hostname'])
    root = Path(root)
    stop(root, config['ownerId'])
    images = preview.read_json(preview.ROOT / 'deploy/preview/access.json')
    for role, image in images.items():
        preview.require(role in ('connectorImage', 'proxyImage') and isinstance(image, str)
                        and re.fullmatch(r'docker.io/(cloudflare/cloudflared|library/caddy):[a-zA-Z0-9_.-]+@sha256:[0-9a-f]{64}', image),
                        'Pin both public access infrastructure images.')
    preview.require(set(images) == {'connectorImage', 'proxyImage'}, 'Missing pinned access image.')
    preview.private_write(root / 'tunnel-token', config['token'])
    preview.private_write(root / 'Caddyfile', content)
    common = ['--detach', '--restart', 'unless-stopped', '--network', 'host', '--user', '0:0',
              '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--log-driver', 'none',
              '--label', LABEL + '=' + config['ownerId']]
    try:
        lifecycle.docker('run', *common, '--name', 'agentstration-preview-access-proxy',
                         '--tmpfs', '/data:rw,nosuid,nodev,noexec,size=1m', '--tmpfs', '/config:rw,nosuid,nodev,noexec,size=1m',
                         '--mount', 'type=bind,src=' + str(root / 'Caddyfile') + ',dst=/etc/caddy/Caddyfile,readonly',
                         images['proxyImage'], 'caddy', 'run', '--config', '/etc/caddy/Caddyfile', '--adapter', 'caddyfile')
        lifecycle.docker('run', *common, '--name', 'agentstration-preview-access-connector',
                         '--mount', 'type=bind,src=' + str(root / 'tunnel-token') + ',dst=/run/secrets/tunnel-token,readonly',
                         images['connectorImage'], 'tunnel', '--no-autoupdate', '--metrics', '127.0.0.1:20241',
                         'run', '--token-file', '/run/secrets/tunnel-token')
        deadline = time.monotonic() + 120
        while not ready(config['ownerId']):
            preview.require(time.monotonic() < deadline, 'The protected connector did not become ready.')
            time.sleep(3)
    except Exception:
        stop(root, config['ownerId'])
        raise


def ready(owner):
    current = containers(owner)
    if set(current) != set(ROLES) or any(not item['State']['Running'] for item in current.values()):
        return False
    images = preview.read_json(preview.ROOT / 'deploy/preview/access.json')
    if any(item['Config']['Image'] != images[role + 'Image']
           or item['HostConfig'].get('NetworkMode') != 'host'
           or not item['HostConfig'].get('ReadonlyRootfs')
           or len(item.get('Mounts', [])) != 1
           or set(item['HostConfig'].get('Tmpfs') or {}) != ({'/data', '/config'} if role == 'proxy' else set())
           or any(mount.get('Type') == 'bind' and (mount.get('RW') or mount.get('Destination') !=
                  ('/etc/caddy/Caddyfile' if role == 'proxy' else '/run/secrets/tunnel-token')) for mount in item.get('Mounts', []))
           for role, item in current.items()):
        return False
    try:
        with urllib.request.urlopen('http://127.0.0.1:20241/ready', timeout=3) as result:
            return result.status == 200
    except OSError:
        return False
