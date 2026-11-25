#!/usr/bin/env python3
"""Read-only audit of shared preview Access configuration; no deployment credentials leave CI."""
import argparse
import json
import os
from pathlib import Path
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

import preview

API = 'https://api.cloudflare.com/client/v4'
MAX_RESPONSE = 2 * 1024 * 1024
MAX_PAGES = 20
PAGE_SIZE = 50


def configuration(environment):
    names = ('ACCOUNT_ID', 'ZONE_ID', 'TEAM_DOMAIN', 'PREVIEW_DOMAIN', 'ACCESS_APP_ID',
             'API_TOKEN', 'ACCESS_CLIENT_ID', 'ACCESS_CLIENT_SECRET')
    values = {name: environment.get('CLOUDFLARE_' + name, '') for name in names}
    for name, value in values.items():
        preview.require(isinstance(value, str) and 0 < len(value) <= 512
                        and not any(character.isspace() for character in value),
                        'Configure CLOUDFLARE_' + name + ' in the protected preview-deployment environment.')
    for name in ('ACCOUNT_ID', 'ZONE_ID'):
        preview.require(re.fullmatch('[0-9a-f]{32}', values[name]) is not None,
                        'Invalid CLOUDFLARE_' + name + '; use the Cloudflare identifier.')
    identifier(values['ACCESS_APP_ID'], 'Invalid CLOUDFLARE_ACCESS_APP_ID; use the application ID, not its AUD.')
    preview.require(re.fullmatch('[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.cloudflareaccess\\.com', values['TEAM_DOMAIN']) is not None,
                    'CLOUDFLARE_TEAM_DOMAIN must be the team hostname, without a scheme or path.')
    labels = values['PREVIEW_DOMAIN'].split('.')
    preview.require(2 <= len(labels) <= 10 and len(values['PREVIEW_DOMAIN']) <= 200
                    and all(re.fullmatch('[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?', label) for label in labels),
                    'Invalid CLOUDFLARE_PREVIEW_DOMAIN; use the DNS domain without a scheme or wildcard.')
    preview.require(re.fullmatch('[A-Za-z0-9_-]{16,128}\\.access', values['ACCESS_CLIENT_ID']) is not None,
                    'CLOUDFLARE_ACCESS_CLIENT_ID must be the Cloudflare service Client ID, not an Entra application ID.')
    return values


def identifier(value, message='Invalid Access identifier.'):
    preview.require(isinstance(value, str) and re.fullmatch('[0-9a-fA-F]{32}|[0-9a-fA-F-]{36}', value) is not None, message)
    try:
        return uuid.UUID(value)
    except ValueError:
        raise ValueError(message) from None


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, file, code, message, headers, new_url):
        raise ValueError('Cloudflare API redirects are refused; credentials were not forwarded.')


class ReadOnlyAPI:
    """Bounded GET-only boundary. Never expose API bodies or transport exception text."""
    def __init__(self, token):
        self.token = token
        self.opener = urllib.request.build_opener(NoRedirect())

    def get(self, path):
        preview.require(path.startswith('/') and not path.startswith('//') and '#' not in path,
                        'Invalid Cloudflare API path.')
        request = urllib.request.Request(API + path, method='GET',
                                         headers={'Authorization': 'Bearer ' + self.token, 'Accept': 'application/json'})
        try:
            with self.opener.open(request, timeout=30) as response:
                raw = response.read(MAX_RESPONSE + 1)
                preview.require(len(raw) <= MAX_RESPONSE, 'Cloudflare API response exceeds the audit limit.')
                value = json.loads(raw)
        except urllib.error.HTTPError as error:
            raise RuntimeError('Cloudflare API read failed (HTTP ' + str(error.code)
                               + '); check the token permissions and configured account/zone.') from None
        except (urllib.error.URLError, OSError, TimeoutError):
            raise RuntimeError('Cloudflare API read failed; check connectivity and retry.') from None
        except (json.JSONDecodeError, UnicodeDecodeError):
            raise ValueError('Cloudflare API returned invalid JSON; response content is withheld.') from None
        preview.require(isinstance(value, dict) and value.get('success') is True and 'result' in value,
                        'Cloudflare API did not confirm a successful read; response content is withheld.')
        return value

    def list(self, path):
        result = []
        for page in range(1, MAX_PAGES + 1):
            response = self.get(path + '?' + urllib.parse.urlencode({'page': page, 'per_page': PAGE_SIZE}))
            items = response['result']
            preview.require(isinstance(items, list) and len(items) <= PAGE_SIZE,
                            'Unexpected Cloudflare inventory page.')
            result.extend(items)
            pages = response.get('result_info', {}).get('total_pages')
            if pages is not None:
                preview.require(type(pages) is int and 0 <= pages <= MAX_PAGES,
                                'Cloudflare inventory exceeds the bounded audit; narrow its scope.')
                if page >= pages:
                    return result
            elif len(items) < PAGE_SIZE:
                return result
        raise ValueError('Cloudflare inventory pagination did not converge; audit refused.')


def policy_rules(policy, kind):
    rules = policy.get(kind, [])
    preview.require(isinstance(rules, list) and all(isinstance(rule, dict) for rule in rules),
                    'Invalid Access policy rules.')
    return rules


def audit(values, api):
    expected = 'dev-*.' + values['PREVIEW_DOMAIN']
    root = '/accounts/' + values['ACCOUNT_ID']
    organization = api.get(root + '/access/organizations')['result']
    preview.require(isinstance(organization, dict) and organization.get('auth_domain') == values['TEAM_DOMAIN'],
                    'CLOUDFLARE_TEAM_DOMAIN does not match the configured Cloudflare account.')
    app_path = root + '/access/apps/' + values['ACCESS_APP_ID']
    app = api.get(app_path)['result']
    preview.require(isinstance(app, dict) and app.get('type') == 'self_hosted' and app.get('domain') == expected,
                    'Access must protect the whole dev-* preview hostname with a self-hosted application.')
    preview.require(identifier(app.get('id')) == identifier(values['ACCESS_APP_ID']), 'Access returned another application.')
    preview.require(re.fullmatch('[0-9a-f]{64}', app.get('aud', '')) is not None, 'Access returned no valid application AUD.')
    # Additional hostnames make the service credential valid outside the intended preview scope.
    domains = app.get('self_hosted_domains') or [expected]
    destinations = app.get('destinations') or []
    preview.require(domains == [expected] and isinstance(destinations, list)
                    and all(isinstance(item, dict) and item.get('type') == 'public' and item.get('uri') == expected
                            for item in destinations), 'The shared Access application must only cover dev-* previews.')
    preview.require(not app.get('options_preflight_bypass') and not app.get('allow_authenticate_via_warp')
                    and not app.get('read_service_tokens_from_header'),
                    'Preview Access must not bypass OPTIONS or enable alternative authentication paths.')
    providers = app.get('allowed_idps')
    preview.require(isinstance(providers, list) and len(providers) == 1,
                    'Select only the dedicated Entra identity provider for preview Access.')
    provider_id = providers[0]
    identifier(provider_id)
    provider = api.get(root + '/access/identity_providers/' + provider_id)['result']
    preview.require(isinstance(provider, dict) and provider.get('type') == 'azureAD'
                    and isinstance(provider.get('config'), dict) and provider['config'].get('support_groups') is True,
                    'The selected Entra provider must enable Support Groups.')
    identifier(provider['config'].get('directory_id'), 'Invalid Entra tenant ID in the identity provider.')
    identifier(provider['config'].get('client_id'), 'Invalid Entra client ID in the identity provider.')

    policies = api.list(app_path + '/policies')
    preview.require(len(policies) == 2 and all(isinstance(policy, dict) for policy in policies),
                    'Expected exactly one human Allow policy and one automation Service Auth policy.')
    human = [p for p in policies if p.get('decision') == 'allow']
    machine = [p for p in policies if p.get('decision') == 'non_identity']
    preview.require(len(human) == len(machine) == 1,
                    'Use Allow for the Entra group and Service Auth for the dedicated CI token; no Bypass policy.')
    groups = policy_rules(human[0], 'include')
    preview.require(len(groups) == 1 and set(groups[0]) == {'azureAD'},
                    'The human policy must Include only the dedicated Azure Group.')
    group = groups[0]['azureAD']
    preview.require(isinstance(group, dict) and set(group) == {'id', 'identity_provider_id'}
                    and identifier(group['identity_provider_id']) == identifier(provider_id),
                    'The human group must use the selected Entra identity provider.')
    identifier(group['id'], 'The human Azure Group must contain its Entra object ID.')
    for rule in policy_rules(human[0], 'require'):
        preview.require(set(rule) == {'login_method'} and isinstance(rule['login_method'], dict)
                        and identifier(rule['login_method'].get('id')) == identifier(provider_id),
                        'Only the selected Entra login method is supported as an additional Require rule.')
    tokens = policy_rules(machine[0], 'include')
    preview.require(len(tokens) == 1 and set(tokens[0]) == {'service_token'}
                    and isinstance(tokens[0]['service_token'], dict) and set(tokens[0]['service_token']) == {'token_id'},
                    'Service Auth must Include only the dedicated CI service token.')
    identifier(tokens[0]['service_token']['token_id'], 'Invalid CI service token ID in the Access policy.')
    preview.require(not policy_rules(machine[0], 'require'), 'The CI Service Auth policy must not require an interactive login.')
    preview.require(not policy_rules(human[0], 'exclude') and not policy_rules(machine[0], 'exclude'),
                    'The initial shared policies must not contain Exclude rules; review them before extending the audit.')

    # More specific applications can override the wildcard policy, including individual URL paths.
    for other in api.list(root + '/access/apps'):
        preview.require(isinstance(other, dict), 'Invalid Access application inventory.')
        if other.get('id') == app['id']:
            continue
        extra_domains = other.get('self_hosted_domains') or []
        preview.require(isinstance(extra_domains, list), 'Invalid Access hostname inventory.')
        other_domains = [other.get('domain', ''), *extra_domains]
        for destination in other.get('destinations') or []:
            preview.require(isinstance(destination, dict), 'Invalid Access destination inventory.')
            other_domains.append(destination.get('uri', ''))
        for domain in other_domains:
            preview.require(isinstance(domain, str), 'Invalid Access hostname inventory.')
            # DNS hostnames are case-insensitive. Destinations may use a full URI.
            parsed = urllib.parse.urlsplit(domain if '://' in domain else 'https://' + domain)
            host = (parsed.hostname or '').lower().rstrip('.')
            preview.require(not (host.endswith('.' + values['PREVIEW_DOMAIN'])
                                 and (host.startswith('dev-') or '*' in host)),
                            'Another Access application overlaps preview hostnames; review its precedence before publishing.')

    # Reading these inventories verifies the configured scopes, not Edit rights or active connectivity.
    api.list(root + '/cfd_tunnel')
    api.list('/zones/' + values['ZONE_ID'] + '/dns_records')
    return {'schemaVersion': 1, 'state': 'configuration-validated', 'hostnamePattern': expected,
            'teamDomain': values['TEAM_DOMAIN'], 'accessAppId': app['id'],
            'checks': {'humanPolicy': 'entra-group', 'automationPolicy': 'dedicated-service-token',
                       'overlappingApplications': False, 'tunnelInventoryReadable': True, 'dnsInventoryReadable': True},
            'automationCredentials': 'configured-not-exercised',
            'deployment': 'not-created', 'url': None}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    # The workflow has the same gate. Keep direct invocation from Actions fail closed too.
    if os.environ.get('GITHUB_ACTIONS') == 'true':
        preview.require(os.environ.get('GITHUB_REF') == 'refs/heads/main'
                        and os.environ.get('GITHUB_EVENT_NAME') == 'workflow_dispatch',
                        'Live Access audits require an explicitly dispatched trusted main workflow.')
    values = configuration(os.environ)
    result = audit(values, ReadOnlyAPI(values['API_TOKEN']))
    preview.private_write(args.output, json.dumps(result, indent=2) + '\n')
    summary = os.environ.get('GITHUB_STEP_SUMMARY')
    if summary:
        with open(summary, 'a') as stream:
            stream.write('Shared preview Access configuration validated for `' + result['hostnamePattern'] + '`.\n\n'
                         'Human Entra group and CI Service Auth policies checked; no VM, tunnel or DNS record created.\n\n'
                         'Service credentials, write permissions and end-to-end protection remain unexercised; no URL is published.\n')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, RuntimeError) as error:
        print('Preview Access audit failed: ' + str(error), file=sys.stderr)
        raise SystemExit(1)
    except Exception:
        print('Preview Access audit failed; response content and credentials are withheld.', file=sys.stderr)
        raise SystemExit(1)
