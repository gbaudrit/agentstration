"""Bounded PR profile metadata and exact-source content pins. No candidate code is executed."""
import hashlib
import json
import re
import uuid

import preview

ROOT = 'deploy/profiles/previews/'
MAXIMUM = 16
GUID = re.compile(r'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}')


def identities(values):
    preview.require(isinstance(values, list) and len(values) <= MAXIMUM, 'Select at most sixteen deployment profile GUIDs.')
    preview.require(all(isinstance(value, str) and GUID.fullmatch(value) and str(uuid.UUID(value)) == value for value in values)
                    and len(values) == len(set(values)), 'Deployment profile GUIDs must be unique canonical lowercase GUIDs.')
    return values


def from_body(body):
    body = body or ''
    preview.require(isinstance(body, str) and len(body.encode()) <= 65536, 'PR description exceeds the preview metadata bound.')
    # Ignore headings hidden in HTML comments or displayed as literal fenced code.
    visible = re.sub(r'<!--[\s\S]*?-->', lambda m: re.sub(r'[^\n]', ' ', m[0]), body)
    headings, fence, offset = [], None, 0
    for line in visible.splitlines(keepends=True):
        marker = re.match(r'^\s*(`{3,}|~{3,})', line)
        if marker:
            if fence is None:
                fence = marker[1][0]
            elif marker[1][0] == fence:
                fence = None
        elif fence is None:
            match = re.match(r'^#{2,3}\s+Preview configuration\s*$', line, re.I)
            if match:
                headings.append((offset, offset + len(line.rstrip('\r\n'))))
        offset += len(line)
    if not headings:
        return []
    preview.require(len(headings) == 1, 'Use one visible Preview configuration section.')
    start = headings[0][1]
    following = re.search(r'^#{1,3}\s+', body[start:], re.M)
    section = body[start:start + following.start()] if following else body[start:]
    match = re.fullmatch(r'\s*```json\s*\n(.*?)\n```\s*', section, re.S)
    preview.require(match is not None and len(section) <= 4096, 'Preview configuration requires one JSON object with a profiles array.')
    try:
        def unique(items):
            preview.require(len(items) == len(dict(items)), 'Duplicate profile selection fields.')
            return dict(items)
        value = json.loads(match[1], object_pairs_hook=unique)
    except ValueError:
        raise ValueError('Invalid Preview configuration JSON.') from None
    preview.require(isinstance(value, dict) and set(value) == {'profiles'}, 'Preview configuration accepts only profiles.')
    return identities(value['profiles'])


def settings_path(identity):
    return ROOT + identity + '/' + identity + '.appsettings.json'


def validate_pins(pins):
    preview.require(isinstance(pins, list), 'Invalid deployment profile pins.')
    identities([pin.get('guid') for pin in pins if isinstance(pin, dict)])
    preview.require(len(pins) <= MAXIMUM and all(isinstance(pin, dict)
        and set(pin) == {'guid', 'settingsSha256', 'contentSha256'}
        and all(isinstance(pin[key], str) and re.fullmatch(r'[0-9a-f]{64}', pin[key])
                for key in ('settingsSha256', 'contentSha256')) for pin in pins), 'Invalid deployment profile digests.')
    return pins


def json_settings(text):
    # Match native JSON comments/trailing commas without touching quoted strings.
    preview.require(len(text.encode()) <= 1024 * 1024, 'Deployment settings exceed one MiB.')
    token = r'"(?:\\.|[^"\\])*"|//[^\r\n]*|/\*[\s\S]*?\*/'
    text = text.lstrip('\ufeff')
    cleaned = re.sub(token, lambda m: m[0] if m[0].startswith('"') else ' ', text)
    cleaned = re.sub(r'("(?:\\.|[^"\\])*"\s*)|,\s*(?=[}\]])', lambda m: m[1] or '', cleaned)
    pairs = []
    def object_pairs(items):
        return ('object', items)
    try:
        value = json.loads(cleaned, object_pairs_hook=object_pairs,
                           parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
    except ValueError:
        raise ValueError('Invalid deployment settings JSON.') from None
    preview.require(isinstance(value, tuple), 'Deployment settings must be a JSON object.')
    def flatten(node, path='', depth=0):
        preview.require(depth <= 64, 'Deployment settings exceed the native JSON depth limit.')
        if isinstance(node, tuple):
            if not node[1] and path:
                pairs.append((path, None))
            for key, child in node[1]:
                flatten(child, path + ':' + key if path else key, depth + 1)
        elif isinstance(node, list):
            if not node:
                pairs.append((path, None))
            for index, child in enumerate(node):
                flatten(child, path + ':' + str(index), depth + 1)
        else:
            pairs.append((path, node))
    flatten(value)
    keys = [key.casefold() for key, _ in pairs]
    preview.require(len(keys) == len(set(keys)), 'Duplicate deployment settings keys.')
    for key in keys:
        preview.require(not any(key == reserved or key.startswith(reserved + ':')
            for reserved in ('agentstration:bootstrap', 'agentstration:additionalsettings')), 'Deployment settings contain a reserved section.')
    return dict(pairs)


def source_path(path, identity):
    preview.require(isinstance(path, str) and '\\' not in path and not any(part in ('', '.', '..') for part in path.split('/')),
                    'Invalid Bootstrap deployment path.')
    local = ROOT + identity + '/bootstrap/'
    preview.require(path.startswith(local) or path.startswith('bootstrap/profiles/'),
                    'Bootstrap references must stay inside the selected profile or the shared catalogue.')
    return 'deploy/' + path if path.startswith('bootstrap/') else path


def pin(selector, guids, sha):
    identities(guids)
    if not guids:
        return []
    commit = selector.api(f'repos/{selector.repository}/git/commits/{sha}')
    preview.require(commit.get('sha') == sha and preview.SHA.fullmatch(commit.get('tree', {}).get('sha', '')), 'Selected profile source commit differs.')
    tree_sha = commit['tree']['sha']
    tree = selector.api(f'repos/{selector.repository}/git/trees/{tree_sha}?recursive=1')
    preview.require(tree.get('sha') == tree_sha and tree.get('truncated') is False and isinstance(tree.get('tree'), list)
                    and len(tree['tree']) <= 50000, 'Selected source tree is incomplete or differs from its SHA.')
    entries = tree['tree']
    result = []
    for identity in guids:
        prefix = ROOT + identity + '/'
        files = [item for item in entries if item['path'].startswith(prefix) and item['type'] != 'tree']
        preview.require(0 < len(files) <= 512 and all(item.get('type') == 'blob' and item.get('mode') == '100644'
            and type(item.get('size')) is int and 0 <= item['size'] <= 32 * 1024 * 1024 for item in files)
            and sum(item['size'] for item in files) <= 32 * 1024 * 1024, 'Invalid, linked or oversized deployment profile sources.')
        selected = next((item for item in files if item['path'] == settings_path(identity)), None)
        preview.require(selected is not None, 'Selected deployment settings file is missing at the exact source SHA.')
        text = selector.source_file(settings_path(identity), sha)
        raw = text.encode('utf-8')
        blob = hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest()
        preview.require(selected['sha'] == blob, 'Deployment settings differ from their selected source blob.')
        flat = json_settings(text)
        # Validate all configured profile references even if this block is disabled.
        normalized = {key.casefold(): value for key, value in flat.items()}
        base = identity + ':bootstrap:'
        directories = []
        for key, value in normalized.items():
            if key.startswith(base + 'profilereferences:') and key.endswith(':path'):
                directories.append(source_path(value, identity))
            elif (key.startswith(base) and ':profiles:' in key) or key.startswith(base + 'initialprofiles:'):
                root = normalized.get(base + 'path')
                preview.require(isinstance(value, str) and re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,127}', value)
                                and value not in ('.', '..') and isinstance(root, str), 'Invalid named Bootstrap deployment profile.')
                directories.append(source_path(root + '/' + value, identity))
        for directory in directories:
            preview.require(any(item['type'] == 'tree' and item['path'] == directory for item in entries), 'Bootstrap deployment directory is missing.')
            children = [item for item in entries if item['path'].startswith(directory + '/') and item['type'] != 'tree']
            preview.require(len(children) <= 256 and all(item.get('mode') == '100644' for item in children), 'Invalid Bootstrap deployment directory boundary.')
        # Pins bind the whole selected subtree plus independently referenced shared content.
        bound = {item['path']: item['sha'] for item in files}
        for directory in directories:
            bound.update({item['path']: item['sha'] for item in entries if item['path'].startswith(directory + '/') and item['type'] == 'blob'})
        digest = hashlib.sha256(json.dumps(bound, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        result.append({'guid': identity, 'settingsSha256': hashlib.sha256(raw).hexdigest(), 'contentSha256': digest})
    return validate_pins(result)


def recheck(request, selector=None):
    """Called under the provider lease before any deployment mutation."""
    if 'deploymentProfiles' not in request:
        return True
    if selector is None:
        import manual
        selector = manual.PublishedImages(request['deliveries']['server']['repository'])
    expected = [item['guid'] for item in request['deploymentProfiles']]
    number = request.get('selectionPullRequest')
    if number is not None:
        pull = selector.api(f'repos/{selector.repository}/pulls/{number}')
        preview.require(pull.get('state') == 'open' and pull.get('head', {}).get('sha') == request['sourceSha']
            and (pull['head'].get('repo') or {}).get('full_name', '').lower() == selector.repository
            and from_body(pull.get('body')) == expected, 'PR source or deployment profiles changed; request a new deployment.')
    preview.require(pin(selector, expected, request['sourceSha']) == request['deploymentProfiles'], 'Deployment profile source pins changed.')
    return True
