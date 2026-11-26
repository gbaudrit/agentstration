#!/usr/bin/env python3
"""Explicit Docker smoke: real pinned proxy, HTTP/WebSocket stripping and connector config parsing."""
import base64
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import socket
import tempfile
import threading
import time
import urllib.request
import uuid

import access
import access_host
import lifecycle
import preview


def smoke():
    captured = []

    class Origin(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            captured.append(dict(self.headers.items()))
            if self.headers.get('Upgrade', '').lower() == 'websocket':
                digest = hashlib.sha1((self.headers['Sec-WebSocket-Key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode()).digest()
                self.send_response(101)
                self.send_header('Upgrade', 'websocket')
                self.send_header('Connection', 'Upgrade')
                self.send_header('Sec-WebSocket-Accept', base64.b64encode(digest).decode())
                self.end_headers()
                self.wfile.flush()
                header = self.rfile.read(2)
                size = header[1] & 127
                mask = self.rfile.read(4)
                data = self.rfile.read(size)
                plain = bytes(c ^ mask[i % 4] for i, c in enumerate(data))
                self.wfile.write(bytes([0x81, len(plain)]) + plain)
                self.wfile.flush()
            else:
                self.send_response(200)
                self.send_header('Content-Length', '2')
                self.end_headers()
                self.wfile.write(b'OK')

    images = preview.read_json(preview.ROOT / 'deploy/preview/access.json')
    origin = ThreadingHTTPServer(('127.0.0.1', 5080), Origin)
    thread = threading.Thread(target=origin.serve_forever, daemon=True)
    thread.start()
    name = 'agentstration-access-smoke-' + uuid.uuid4().hex[:12]
    user = str(os.getuid()) + ':' + str(os.getgid())
    try:
        with tempfile.TemporaryDirectory(prefix='access-smoke-') as temporary:
            root = Path(temporary)
            preview.private_write(root / 'Caddyfile', access_host.proxy_configuration('dev-smoke.agentstration.cloud'))
            lifecycle.docker('run', '--rm', '--user', user, '--mount', 'type=bind,src=' + str(root / 'Caddyfile') + ',dst=/etc/caddy/Caddyfile,readonly',
                             images['proxyImage'], 'caddy', 'validate', '--config', '/etc/caddy/Caddyfile', '--adapter', 'caddyfile')
            config = access.ingress({'hostname': 'dev-smoke.agentstration.cloud', 'teamDomain': 'agentstration.cloudflareaccess.com'}, 'a' * 64)
            preview.private_write(root / 'connector.json', json.dumps(config))
            lifecycle.docker('run', '--rm', '--user', user, '--mount', 'type=bind,src=' + str(root / 'connector.json') + ',dst=/etc/cloudflared/config.json,readonly',
                             images['connectorImage'], 'tunnel', '--config', '/etc/cloudflared/config.json', 'ingress', 'validate')
            lifecycle.docker('run', '--detach', '--name', name, '--network', 'host', '--user', user, '--read-only',
                             '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--log-driver', 'none',
                             '--tmpfs', '/data:rw,nosuid,nodev,noexec,size=1m', '--tmpfs', '/config:rw,nosuid,nodev,noexec,size=1m',
                             '--mount', 'type=bind,src=' + str(root / 'Caddyfile') + ',dst=/etc/caddy/Caddyfile,readonly',
                             images['proxyImage'], 'caddy', 'run', '--config', '/etc/caddy/Caddyfile', '--adapter', 'caddyfile')
            for attempt in range(40):
                try:
                    with urllib.request.urlopen('http://127.0.0.1:5081/health/ready', timeout=1):
                        break
                except OSError:
                    preview.require(attempt < 39, 'The pinned proxy did not start.')
                    time.sleep(0.25)
            # Multiple positions, duplicates, case, and multiple application cookies.
            cookies = ['CF_Authorization=access-fixture; App=one', 'App=one; CF_Authorization=access-fixture; Other=two',
                       'App=one; CF_Authorization=access-fixture', 'CF_Authorization=a; CF_Authorization=b; App=one',
                       'cf_authorization=a; App=one', ' CF_Authorization =a; App=one']
            headers = {'CF-Access-Client-Id': 'id-fixture', 'CF-Access-Client-Secret': 'secret-fixture',
                       'Cf-Access-Jwt-Assertion': 'jwt-fixture', 'Authorization': 'Bearer application-fixture'}
            for cookie in cookies:
                with urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:5081/', headers={**headers, 'Cookie': cookie}), timeout=5) as response:
                    preview.require(response.status == 200, 'HTTP proxy failed.')
                check(captured[-1])
            with socket.create_connection(('127.0.0.1', 5081), timeout=5) as client:
                client.sendall(('GET /_blazor HTTP/1.1\r\nHost: dev-smoke.agentstration.cloud\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n'
                                'Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n'
                                + ''.join(key + ': ' + value + '\r\n' for key, value in {**headers, 'Cookie': cookies[0]}.items()) + '\r\n').encode())
                stream = client.makefile('rb')
                preview.require(b' 101 ' in stream.readline(), 'WebSocket upgrade failed.')
                while stream.readline() != b'\r\n':
                    pass
                message, mask = b'preview-ws', b'abcd'
                client.sendall(bytes([0x81, 0x80 | len(message)]) + mask + bytes(c ^ mask[i % 4] for i, c in enumerate(message)))
                frame = stream.read(2)
                preview.require(frame == bytes([0x81, len(message)]) and stream.read(len(message)) == message, 'WebSocket echo failed.')
                check(captured[-1])
    finally:
        ids = lifecycle.docker('ps', '--all', '--filter', 'name=^/' + name + '$', '--format', '{{.ID}}')
        if ids:
            lifecycle.docker('rm', '--force', ids)
        origin.shutdown()
        origin.server_close()
    print('Pinned access proxy: HTTP and WebSocket passed; Access credentials stripped; application authentication preserved. Connector config parsed.')


def check(headers):
    values = {key.lower(): value for key, value in headers.items()}
    preview.require(not any(key.startswith('cf-access-') for key in values)
                    and all(cookie.partition('=')[0].strip().lower() != 'cf_authorization'
                            for cookie in values.get('cookie', '').split(';'))
                    and 'App=one' in values.get('cookie', '')
                    and values.get('authorization') == 'Bearer application-fixture'
                    and values.get('host') == 'dev-smoke.agentstration.cloud'
                    and values.get('x-forwarded-proto') == 'https', 'Proxy credential or forwarding boundary failed.')


if __name__ == '__main__':
    smoke()
