#!/usr/bin/env python3
"""Invoke deterministic MCP tools on the exact Utilities OCI delivery."""
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import urllib.request
import uuid
from smoke import wait_for_health


def main():
    delivery = json.loads(Path('artifacts/extension/delivery.json').read_text())
    name = 'utilities-boundary-' + uuid.uuid4().hex
    with tempfile.TemporaryDirectory(prefix='utilities-image-') as temporary:
        key_file = Path(temporary) / 'key'
        key = secrets.token_urlsafe(48)
        key_file.write_text(key)
        key_file.chmod(0o600)
        try:
            subprocess.run(['docker', 'run', '--detach', '--name', name, '--publish', '127.0.0.1::8080',
                            '--user', f'{os.getuid()}:{os.getgid()}',
                            '--mount', f'type=bind,source={key_file},target=/run/secrets/key,readonly',
                            '--env', 'Aep__EnrollmentMode=SharedKeyFile', '--env', 'Aep__SharedKeyFile__Path=/run/secrets/key',
                            'localhost:5000/extension@' + delivery['digest']], check=True, stdout=subprocess.DEVNULL)
            inspect = json.loads(subprocess.check_output(['docker', 'inspect', name]))[0]
            base = 'http://127.0.0.1:' + inspect['NetworkSettings']['Ports']['8080/tcp'][0]['HostPort']
            wait_for_health(base)
            headers = {'Authorization': 'Bearer ' + key, 'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'}
            counter = 0
            def rpc(method, params, notification=False):
                nonlocal counter
                counter += 1
                body = {'jsonrpc': '2.0', 'method': method, 'params': params}
                if not notification:
                    body['id'] = counter
                request = urllib.request.Request(base + '/mcp', data=json.dumps(body).encode(), headers=headers)
                with urllib.request.urlopen(request, timeout=15) as response:
                    session = response.headers.get('Mcp-Session-Id')
                    if session:
                        headers['Mcp-Session-Id'] = session
                    raw = response.read().decode()
                    if notification:
                        return None
                    messages = [json.loads(line[5:].strip()) for line in raw.splitlines() if line.startswith('data:')]
                    result = messages[-1] if messages else json.loads(raw)
                    assert 'error' not in result
                    return result['result']
            initialize = rpc('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'packaging-fixture', 'version': '0.1.0'}})
            headers['MCP-Protocol-Version'] = initialize['protocolVersion']
            rpc('notifications/initialized', {}, notification=True)
            listed = rpc('tools/list', {})
            assert {'text_upper', 'hash_compute', 'json_compact'} <= {tool['name'] for tool in listed['tools']}
            for tool, arguments, expected in [('text_upper', {'text': 'packaging'}, 'PACKAGING'),
                                               ('hash_compute', {'text': 'packaging'}, hashlib.sha256(b'packaging').hexdigest()),
                                               ('json_compact', {'json': '{ "fixture": true }'}, '{"fixture":true}')]:
                result = rpc('tools/call', {'name': tool, 'arguments': arguments})
                assert not result.get('isError', False)
                texts = [content['text'] for content in result.get('content', []) if content['type'] == 'text']
                assert expected in texts or json.dumps(expected) in texts, tool
            Path('artifacts/extension/utilities-smoke.json').write_text(json.dumps({
                'sourceSha': delivery['sourceSha'], 'digest': delivery['digest'], 'mcpToolDiscovery': True,
                'deterministicUppercaseHashAndJson': True}, indent=2) + '\n')
            print('Utilities image MCP discovery and three deterministic tool invocations passed.')
        finally:
            subprocess.run(['docker', 'rm', '--force', name], check=False, stdout=subprocess.DEVNULL)


if __name__ == '__main__':
    main()
