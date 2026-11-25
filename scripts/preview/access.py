"""Owned Cloudflare routes under the Azure provider lease; never modify Access policies."""
import hashlib
import re
import time
import urllib.error
import urllib.request

import cloudflare
import preview


class API(cloudflare.ReadOnlyAPI):
    def change(self, method, path, body=None):
        preview.require(method in ('POST', 'PUT', 'DELETE')
                        and re.fullmatch(r'/(accounts/[0-9a-f]{32}/cfd_tunnel|zones/[0-9a-f]{32}/dns_records)(/[0-9a-f-]+)?(/configurations|/connections)?', path),
                        'Cloudflare writes are limited to tunnels and DNS records.')
        return self._request(method, path, body)['result']


def metadata(request, record, values):
    # The saved creation intent binds names to a random owner before any provider write.
    identity = '/'.join((request['target']['subscriptionId'].lower(), request['target']['resourceGroup'].lower(),
                         request['environmentId'], record['ownerId'], request['deliveries']['server']['repository'].lower()))
    return {'schemaVersion': 1, 'accountId': values['ACCOUNT_ID'], 'zoneId': values['ZONE_ID'],
            'appId': values['ACCESS_APP_ID'], 'teamDomain': values['TEAM_DOMAIN'],
            'hostname': 'dev-' + request['environmentId'] + '.' + values['PREVIEW_DOMAIN'],
            'tunnelName': 'agentstration-' + hashlib.sha256(identity.encode()).hexdigest(),
            'tunnelId': None, 'dnsId': None, 'verified': False}


def validate(request, record):
    value = record['access']
    preview.require(isinstance(value, dict), 'Invalid route ownership metadata.')
    # Validate persisted scope independently of current environment configuration.
    scope = {'ACCOUNT_ID': value.get('accountId'), 'ZONE_ID': value.get('zoneId'), 'ACCESS_APP_ID': value.get('appId'),
             'TEAM_DOMAIN': value.get('teamDomain'), 'PREVIEW_DOMAIN': value.get('hostname', '').partition('.')[2]}
    expected = metadata(request, record, scope)
    preview.require(set(value) == set(expected) and all(value[key] == expected[key] for key in expected
                    if key not in ('tunnelId', 'dnsId', 'verified')) and type(value['verified']) is bool,
                    'Route scope or ownership differs from the recorded preview.')
    for key in ('tunnelId', 'dnsId'):
        if value[key] is not None:
            cloudflare.identifier(value[key], 'Invalid recorded route resource ID.')
    preview.require(value['dnsId'] is None or value['tunnelId'] is not None, 'A recorded DNS route has no owned tunnel.')


def ingress(value, audience):
    return {'ingress': [{'hostname': value['hostname'], 'service': 'http://127.0.0.1:5081',
                        'originRequest': {'access': {'required': True,
                                           'teamName': value['teamDomain'].removesuffix('.cloudflareaccess.com'),
                                           'audTag': [audience]}}}, {'service': 'http_status:404'}]}


class Routes:
    def __init__(self, request, record, save, environment, api=None):
        self.request, self.record, self.save = request, record, save
        self.values = cloudflare.configuration(environment)
        self.api = api or API(self.values['API_TOKEN'])
        expected = metadata(request, record, self.values)
        if 'access' not in record:
            record['access'] = expected
            save(record)
        validate(request, record)
        self.value = record['access']
        preview.require(all(self.value[key] == expected[key] for key in expected
                            if key not in ('tunnelId', 'dnsId', 'verified')),
                        'Cloudflare configuration changed the recorded scope; restore it before teardown or redeploy.')
        self.tunnels = '/accounts/' + self.value['accountId'] + '/cfd_tunnel'
        self.dns = '/zones/' + self.value['zoneId'] + '/dns_records'
        self.comment = 'agentstration.preview.owner=' + record['ownerId']

    def audit(self):
        return cloudflare.audit(self.values, self.api)['accessAudience']

    def tunnel(self):
        matches = [item for item in self.api.list(self.tunnels) if not item.get('deleted_at')
                   and (item.get('name') == self.value['tunnelName'] or item.get('id') == self.value['tunnelId'])]
        preview.require(len(matches) <= 1, 'Multiple tunnels occupy the owned namespace; refusing mutation.')
        if not matches:
            return None
        item = matches[0]
        cloudflare.identifier(item.get('id'))
        preview.require(item.get('name') == self.value['tunnelName'] and item.get('config_src') == 'cloudflare'
                        and self.value['tunnelId'] in (None, item['id']), 'Foreign tunnel occupies the preview namespace.')
        return item

    def dns_record(self):
        matches = [item for item in self.api.list(self.dns)
                   if item.get('name', '').lower().rstrip('.') == self.value['hostname'] or item.get('id') == self.value['dnsId']]
        preview.require(len(matches) <= 1, 'Multiple DNS records occupy the preview hostname; refusing mutation.')
        if not matches:
            return None
        item = matches[0]
        cloudflare.identifier(item.get('id'))
        preview.require(self.value['tunnelId'] is not None and item.get('type') == 'CNAME'
                        and item.get('name', '').lower().rstrip('.') == self.value['hostname']
                        and item.get('content', '').lower().rstrip('.') == self.value['tunnelId'] + '.cfargotunnel.com'
                        and item.get('proxied') is True and item.get('comment') == self.comment
                        and self.value['dnsId'] in (None, item['id']), 'Foreign or changed DNS record; refusing adoption or deletion.')
        return item

    def withdraw(self):
        item = self.dns_record()
        if item:
            self.api.change('DELETE', self.dns + '/' + item['id'])
        self.value.update(dnsId=None, verified=False)
        self.save(self.record)

    def prepare(self):
        audience = self.audit()
        # A previous publication is withheld while its application is replaced.
        self.withdraw()
        item = self.tunnel()
        if not item:
            self.value.update(tunnelId=None, verified=False)
            self.save(self.record)
            item = self.api.change('POST', self.tunnels, {'name': self.value['tunnelName'], 'config_src': 'cloudflare'})
            cloudflare.identifier(item.get('id'))
            # The next read verifies ownership even after an interrupted POST/save.
            self.value['tunnelId'] = item['id']
            self.save(self.record)
        else:
            self.value['tunnelId'] = item['id']
            self.save(self.record)
        preview.require(self.tunnel() is not None, 'Created tunnel is not visible in the bounded inventory.')
        configuration = ingress(self.value, audience)
        self.api.change('PUT', self.tunnels + '/' + self.value['tunnelId'] + '/configurations', {'config': configuration})
        self.check_configuration(audience)
        token = self.api.get(self.tunnels + '/' + self.value['tunnelId'] + '/token')['result']
        preview.require(isinstance(token, str) and 16 <= len(token) <= 4096 and not any(c.isspace() for c in token),
                        'Cloudflare returned no bounded connector credential.')
        return {'ownerId': self.record['ownerId'], 'hostname': self.value['hostname'], 'token': token}

    def check_configuration(self, audience):
        preview.require(self.tunnel() is not None, 'The recorded tunnel is missing.')
        observed = self.api.get(self.tunnels + '/' + self.value['tunnelId'] + '/configurations')['result']['config']
        # Cloudflare can add default config fields; the ingress must remain exact.
        preview.require(observed.get('ingress') == ingress(self.value, audience)['ingress']
                        and not observed.get('originRequest') and not (observed.get('warp-routing') or {}).get('enabled'),
                        'Tunnel ingress or Access JWT guard differs from the protected route.')

    def publish(self):
        self.check_configuration(self.audit())
        item = self.dns_record()
        if not item:
            item = self.api.change('POST', self.dns, {'type': 'CNAME', 'name': self.value['hostname'],
                                  'content': self.value['tunnelId'] + '.cfargotunnel.com',
                                  'proxied': True, 'ttl': 1, 'comment': self.comment})
            cloudflare.identifier(item.get('id'))
            self.value['dnsId'] = item['id']
            self.save(self.record)
        preview.require(self.dns_record() is not None, 'Created DNS record is not visible in the bounded inventory.')
        url = self.verify()
        self.value['verified'] = True
        self.save(self.record)
        return url

    def verify(self):
        self.check_configuration(self.audit())
        preview.require(self.dns_record() is not None, 'The recorded preview DNS route is missing.')
        url = 'https://' + self.value['hostname']
        probe(url, self.values)
        return url

    def destroy(self):
        self.withdraw()
        item = self.tunnel()
        if item:
            # Called only after the connector VM is stopped/deleted. Clear stale sessions.
            self.api.change('DELETE', self.tunnels + '/' + item['id'] + '/connections')
            self.api.change('DELETE', self.tunnels + '/' + item['id'])
        self.value.update(tunnelId=None, verified=False)
        self.save(self.record)


class NoProbeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def response(url, headers):
    opener = urllib.request.build_opener(NoProbeRedirect())
    try:
        result = opener.open(urllib.request.Request(url + '/health/ready', headers=headers), timeout=15)
    except urllib.error.HTTPError as error:
        result = error
    except (urllib.error.URLError, OSError, TimeoutError):
        raise RuntimeError('Protected preview probe could not connect; no URL is published.') from None
    with result:
        # No body, Set-Cookie or Location is logged or returned.
        result.read(4096)
        return result.code, result.headers.get('Location', ''), result.headers.get('CF-Mitigated') == 'challenge'


def probe(url, values, attempts=12, pause=time.sleep):
    preview.require(re.fullmatch(r'https://dev-[a-z][a-z0-9-]{0,39}\.' + re.escape(values['PREVIEW_DOMAIN']), url),
                    'Invalid protected preview URL.')
    for attempt in range(attempts):
        try:
            for phase, headers in (('Anonymous', {}), ('Invalid-token', {
                    'CF-Access-Client-Id': 'invalid.access', 'CF-Access-Client-Secret': 'invalid'})):
                code, location, challenge = response(url, headers)
                preview.require(code in (401, 403) or (code in (302, 303, 307)
                                and location.startswith('https://' + values['TEAM_DOMAIN'] + '/cdn-cgi/access/login')),
                                phase + ' probe did not confirm Access denial (HTTP ' + str(code)
                                + ('; Cloudflare challenge header present' if challenge else '') + ').')
            code, _, challenge = response(url, {'CF-Access-Client-Id': values['ACCESS_CLIENT_ID'],
                                     'CF-Access-Client-Secret': values['ACCESS_CLIENT_SECRET']})
            preview.require(code == 200, 'The dedicated CI token did not reach a ready protected preview (HTTP ' + str(code)
                            + ('; Cloudflare challenge header present' if challenge else '') + ').')
            return
        except (ValueError, RuntimeError):
            if attempt + 1 == attempts:
                raise
            pause(10)
