"""Black-box paxx12/Moonraker Spoolman compatibility regression tests.

Run ONLY against disposable databases (never the user's inventory on port 5090):
SPOOLHERO_TEST_SOURCE_URL, SPOOLHERO_TEST_DESTINATION_URL,
SPOOLHERO_TEST_EMAIL, SPOOLHERO_TEST_PASSWORD python3 tests/spoolman_compat_smoke.py
Optional SOURCE/DESTINATION_EMAIL/PASSWORD override the shared credentials.

Uses Python's standard library, including a small RFC 6455 client. The wire
contracts are Moonraker's components/spoolman.py and paxx12's
docs/design/spoolman.md: a WebSocket connection is required before Moonraker
reports use_length in millimetres, and extra.card_uids is JSON encoded twice.
Creates isolated projects/users and retains fixtures for inspection.
"""

import base64
import concurrent.futures
import copy
import hashlib
import json
import math
import os
import socket
import ssl
import struct
import time
import unittest
import uuid
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen


MANAGEMENT = '/api/integrations/spoolman'
INVALID = (400, 422)
DENIED = (401, 403, 404)


class Api:
    def __init__(self, case, url, token=None, project=None):
        self.case, self.url = case, url.rstrip('/')
        self.token, self.project = token, project

    def request(self, method, path, data=None, expected=200, authenticated=True,
                headers=None, raw=None):
        request_headers = {'Content-Type': 'application/json'}
        if authenticated and self.token:
            request_headers['Authorization'] = 'Bearer ' + self.token
        if self.project:
            request_headers['X-Project-Id'] = self.project
        request_headers.update(headers or {})
        body = raw if raw is not None else json.dumps(data).encode() if data is not None else None
        request = Request(self.url + path, data=body, headers=request_headers, method=method)
        try:
            response = urlopen(request, timeout=40)
        except HTTPError as error:
            response = error
        content = response.read()
        statuses = (expected,) if isinstance(expected, int) else expected
        self.case.assertIn(response.status, statuses,
                           (method, path, response.status, content[:2000].decode(errors='replace')))
        if not content:
            return None
        content_type = response.headers.get('Content-Type', '').split(';', 1)[0].strip()
        self.case.assertTrue(content_type == 'application/json' or content_type.endswith('+json'),
                             'API must return JSON, not the SPA HTML: ' + content_type)
        return json.loads(content)

    def login(self, email, password):
        self.token = self.request('POST', '/api/auth/login', {
            'email': email, 'password': password,
        }, authenticated=False)['accessToken']

    def printer(self, connection_path):
        # Firmware only knows the connection URL: no browser login or project
        # header may be necessary, or allowed to change the token's project.
        return Compat(self.case, self.url, connection_path)


class Compat(Api):
    def __init__(self, case, url, connection_path):
        super().__init__(case, url)
        self.path = connection_path.rstrip('/') + '/api/v1'

    def call(self, method, path='', data=None, expected=200, **kwargs):
        return self.request(method, self.path + path, data, expected,
                            authenticated=False, **kwargs)


class WebSocket:
    """Minimal masked client, sufficient for Moonraker's subscription contract."""

    def __init__(self, url):
        parsed = urlsplit(url)
        port = parsed.port or (443 if parsed.scheme in ('https', 'wss') else 80)
        self.socket = socket.create_connection((parsed.hostname, port), timeout=10)
        if parsed.scheme in ('https', 'wss'):
            self.socket = ssl.create_default_context().wrap_socket(self.socket, server_hostname=parsed.hostname)
        self.buffer = bytearray()
        key = base64.b64encode(os.urandom(16)).decode()
        path = parsed.path + ('?' + parsed.query if parsed.query else '')
        self.socket.sendall((
            f'GET {path} HTTP/1.1\r\nHost: {parsed.netloc}\r\n'
            'Upgrade: websocket\r\nConnection: Upgrade\r\n'
            f'Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n').encode())
        while b'\r\n\r\n' not in self.buffer:
            chunk = self.socket.recv(4096)
            if not chunk:
                raise AssertionError('Server closed the WebSocket handshake')
            self.buffer.extend(chunk)
        raw_headers, remaining = bytes(self.buffer).split(b'\r\n\r\n', 1)
        self.buffer = bytearray(remaining)
        if not raw_headers.startswith(b'HTTP/1.1 101 '):
            raise AssertionError('WebSocket handshake rejected: ' + raw_headers[:500].decode())
        expected = base64.b64encode(hashlib.sha1(
            (key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode()).digest())
        headers = {line.split(b':', 1)[0].lower(): line.split(b':', 1)[1].strip()
                   for line in raw_headers.split(b'\r\n')[1:] if b':' in line}
        if headers.get(b'sec-websocket-accept') != expected:
            raise AssertionError('Invalid WebSocket accept key')

    def read(self, count):
        while len(self.buffer) < count:
            data = self.socket.recv(max(count - len(self.buffer), 4096))
            if not data:
                raise EOFError('WebSocket disconnected')
            self.buffer.extend(data)
        value = bytes(self.buffer[:count])
        del self.buffer[:count]
        return value

    def frame(self, timeout=10):
        self.socket.settimeout(timeout)
        first, second = self.read(2)
        opcode = first & 0x0f
        length = second & 0x7f
        if length == 126:
            length = struct.unpack('!H', self.read(2))[0]
        elif length == 127:
            length = struct.unpack('!Q', self.read(8))[0]
        if second & 0x80:
            raise AssertionError('Server frames must be unmasked')
        if length > 2 * 1024 * 1024:
            raise AssertionError('Unexpected oversized server frame')
        return opcode, self.read(length)

    def send(self, opcode, payload=b''):
        mask = os.urandom(4)
        length = len(payload)
        prefix = bytes([0x80 | opcode])
        prefix += (bytes([0x80 | length]) if length < 126 else
                   bytes([0x80 | 126]) + struct.pack('!H', length) if length < 65536 else
                   bytes([0x80 | 127]) + struct.pack('!Q', length))
        self.socket.sendall(prefix + mask + bytes(value ^ mask[i % 4] for i, value in enumerate(payload)))

    def receive_until(self, predicate, timeout=12):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            opcode, payload = self.frame(max(0.1, deadline - time.monotonic()))
            if opcode == 9:
                self.send(10, payload)
            if predicate(opcode, payload):
                return payload
            if opcode == 8:
                raise AssertionError('WebSocket closed before expected message: ' + repr(payload))
        raise AssertionError('Expected WebSocket message did not arrive')

    def event(self, event_type, spool_id, timeout=12):
        def matches(opcode, payload):
            if opcode != 1:
                return False
            event = json.loads(payload)
            return (event.get('resource') == 'spool' and event.get('type') == event_type
                    and event.get('payload', {}).get('id') == spool_id)
        return json.loads(self.receive_until(matches, timeout))

    def close(self):
        try:
            self.send(8, struct.pack('!H', 1000))
        except OSError:
            pass
        self.socket.close()

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


class SpoolmanCompatibilitySmoke(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source_url = os.environ['SPOOLHERO_TEST_SOURCE_URL'].rstrip('/')
        cls.destination_url = os.environ['SPOOLHERO_TEST_DESTINATION_URL'].rstrip('/')
        if cls.source_url == cls.destination_url:
            raise ValueError('Use separate disposable source and destination databases')
        for url in (cls.source_url, cls.destination_url):
            if urlsplit(url).port == 5090:
                raise ValueError('Port 5090 is the real user inventory and must never be tested')

    def client(self, name='SOURCE'):
        api = Api(self, self.source_url if name == 'SOURCE' else self.destination_url)
        api.login(os.environ.get('SPOOLHERO_TEST_' + name + '_EMAIL', os.environ['SPOOLHERO_TEST_EMAIL']),
                  os.environ.get('SPOOLHERO_TEST_' + name + '_PASSWORD', os.environ['SPOOLHERO_TEST_PASSWORD']))
        return api

    def fixture(self, density=1.24, diameter=1.75, initial=1000, remaining=900):
        api = self.client()
        suffix = uuid.uuid4().hex[:10]
        api.project = api.request('POST', '/api/projects', {
            'name': 'Protocol test ' + suffix,
        }, 201)['id']
        owner = api.request('POST', '/api/owners', {'name': 'Owner ' + suffix}, 201)
        material = api.request('POST', '/api/materials', {
            'type': 'PLA', 'brand': 'Protocol ' + suffix, 'colorHex': '12AB34',
            'colorName': 'Green', 'minTempCelsius': 200, 'maxTempCelsius': 220,
            'diameterMm': diameter, 'weightGrams': 1000, 'densityGCm3': density,
            'bedTempCelsius': 60,
        }, 201)
        spool = api.request('POST', '/api/spools', {
            'filamentMaterialId': material['id'], 'ownerId': owner['id'],
            'initialWeightGrams': initial, 'emptySpoolWeightGrams': 225,
            'remainingWeightGrams': remaining, 'notes': 'Exact spool ' + suffix,
        }, 201)
        connection = api.request('POST', MANAGEMENT + '/enable')
        compat = api.printer(connection['connectionPath'])
        status = api.request('GET', MANAGEMENT)
        row = next(row for row in status['spools'] if row['spoolId'] == spool['id'])
        return api, compat, material, spool, row['id']

    def test_native_handshake_metadata_card_mapping_and_project_isolation(self):
        api, printer, material, spool, numeric = self.fixture()
        info = printer.call('GET', '/info')
        self.assertTrue(info.get('version'))
        for entity, key in (('spool', 'card_uids'), ('filament', 'variant')):
            fields = printer.call('GET', '/field/' + entity)
            self.assertIn(key, [field['key'] for field in fields])
            field = next(field for field in fields if field['key'] == key)
            self.assertEqual(field['field_type'], 'text')
            # Startup clients verify/create these definitions idempotently.
            printer.call('POST', f'/field/{entity}/{key}', {
                'name': field['name'], 'field_type': 'text', 'default_value': json.dumps(''),
            }, expected=(200, 201, 409))
        rows = printer.call('GET', '/spool?limit=1000&allow_archived=true')
        self.assertEqual([numeric], [row['id'] for row in rows])
        mapped = printer.call('GET', '/spool/' + str(numeric))
        self.assertIsInstance(mapped['id'], int)
        self.assertEqual(mapped['filament']['vendor']['name'], material['brand'])
        self.assertEqual(mapped['filament']['material'], 'PLA')
        self.assertEqual(mapped['filament']['color_hex'].upper(), '12AB34')
        self.assertEqual(mapped['spool_weight'], 225)
        self.assertEqual(mapped['initial_weight'], 1000)
        self.assertEqual(mapped['remaining_weight'], 900)
        self.assertEqual(mapped['used_weight'], 100)
        self.assertEqual(mapped['filament']['diameter'], 1.75)
        self.assertEqual(mapped['filament']['density'], 1.24)
        uid_string = '04A1B2C3D4E5F6,AABBCCDD'
        patched = printer.call('PATCH', '/spool/' + str(numeric), {
            'extra': {'card_uids': json.dumps(uid_string)},
        })
        self.assertEqual(json.loads(patched['extra']['card_uids']), uid_string)
        reread = printer.call('GET', '/spool/' + str(numeric))
        self.assertEqual(json.loads(reread['extra']['card_uids']), uid_string)
        status = api.request('GET', MANAGEMENT)
        status_row = next(row for row in status['spools'] if row['spoolId'] == spool['id'])
        self.assertEqual(status_row['cardUids'], uid_string.split(','))
        for malformed in ('"hello!"', '"ABC"', '"00112233445566778899AABBCCDDEEFF00"'):
            printer.call('PATCH', '/spool/' + str(numeric), {'extra': {'card_uids': malformed}}, INVALID)
        second_local = api.request('POST', '/api/spools', {
            'filamentMaterialId': material['id'], 'initialWeightGrams': 1000,
            'remainingWeightGrams': 700, 'emptySpoolWeightGrams': 180,
        }, 201)
        second_status = api.request('GET', MANAGEMENT)
        second_local_numeric = next(row['id'] for row in second_status['spools']
                                    if row['spoolId'] == second_local['id'])
        self.assertNotEqual(second_local_numeric, numeric)
        self.assertEqual(len(printer.call('GET', '/spool?limit=1&offset=1')), 1)
        printer.call('PATCH', '/spool/' + str(second_local_numeric), {
            'extra': {'card_uids': json.dumps(uid_string)},
        })
        self.assertEqual(json.loads(printer.call('GET', '/spool/' + str(numeric))['extra']['card_uids']), '')
        self.assertEqual(json.loads(printer.call('GET', '/spool/' + str(second_local_numeric))['extra']['card_uids']), uid_string)
        second_api, second_printer, second_material, second_spool, second_numeric = self.fixture()
        spoof = {'X-Project-Id': second_api.project, 'Authorization': 'Bearer ' + second_api.token}
        isolated = printer.call('GET', '/spool?limit=1000', headers=spoof)
        self.assertEqual([material['brand']] * 2, [row['filament']['vendor']['name'] for row in isolated])
        self.assertNotEqual(isolated[0]['filament']['vendor']['name'], second_material['brand'])
        printer.call('GET', '/spool/2147483647', expected=404)
        printer.call('POST', '/spool', {'filament_id': numeric}, expected=(404, 405))
        printer.call('GET', '/unknown-route', expected=404)

    def test_usage_concurrent_deductions_length_conversion_and_tare(self):
        api, printer, material, spool, numeric = self.fixture(density=1.3, diameter=2.85)
        path = '/spool/' + str(numeric)
        millimetres = 5000
        consumed = millimetres * math.pi * (2.85 / 2) ** 2 * 1.3 / 1000
        result = printer.call('PUT', path + '/use', {'use_length': millimetres})
        self.assertAlmostEqual(result['remaining_weight'], 900 - consumed, delta=0.02)
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            responses = list(pool.map(lambda _: printer.call('PUT', path + '/use', {'use_weight': 1.25}), range(32)))
        self.assertEqual(len(responses), 32)
        result = printer.call('GET', path)
        self.assertAlmostEqual(result['remaining_weight'], 900 - consumed - 40, delta=0.03)
        self.assertEqual(result['spool_weight'], 225, 'Usage must never deduct the empty plastic/cardboard reel')
        self.assertEqual(result['initial_weight'], 1000)
        hero = api.request('GET', '/api/spools/' + spool['id'])
        self.assertAlmostEqual(hero['remainingWeightGrams'], result['remaining_weight'], delta=0.01)
        self.assertEqual(hero['ownerId'], spool['ownerId'])
        self.assertEqual(hero['notes'], spool['notes'])
        self.assertEqual(hero['emptySpoolWeightGrams'], 225)
        expected_length = result['remaining_weight'] / 1.3 * 1000 / (math.pi * (2.85 / 2) ** 2)
        self.assertAlmostEqual(result['remaining_length'], expected_length, delta=0.2)
        self.assertTrue(result.get('last_used'))
        # Each malformed request must leave the weight unchanged.
        for body in ({}, {'use_length': -1}, {'use_weight': -1},
                     {'use_length': 10, 'use_weight': 1}, {'use_weight': 'bad'},
                     {'use_length': None}, {'use_weight': 1e30}):
            printer.call('PUT', path + '/use', body, INVALID)
        printer.call('PUT', path + '/use', expected=INVALID, raw=b'{broken')
        self.assertEqual(printer.call('GET', path)['remaining_weight'], result['remaining_weight'])
        depleted = printer.call('PUT', path + '/use', {'use_weight': 10000})
        self.assertEqual(depleted['remaining_weight'], 0)
        self.assertEqual(depleted['spool_weight'], 225)
        self.assertEqual(api.request('GET', '/api/spools/' + spool['id'])['remainingWeightGrams'], 0)
        self.assertTrue(api.request('GET', MANAGEMENT).get('lastUsageAt'))

    def test_fractional_reports_accumulate_instead_of_rounding_every_report_away(self):
        api, printer, material, spool, numeric = self.fixture()
        path = '/spool/' + str(numeric)
        # One millimetre of 1.75 mm PLA weighs about 0.003 g. Repeatedly
        # rounding each report to 0.01 g would lose all one hundred reports.
        for _ in range(100):
            printer.call('PUT', path + '/use', {'use_length': 1})
        expected_used = 100 * math.pi * (1.75 / 2) ** 2 * 1.24 / 1000
        actual_used = 900 - printer.call('GET', path)['remaining_weight']
        self.assertAlmostEqual(actual_used, math.floor(expected_used * 100) / 100, places=6)
        self.assertAlmostEqual(actual_used, expected_used, delta=0.011)

    @staticmethod
    def edit_body(spool, **changes):
        fields = ('filamentMaterialId', 'rfidTagUid', 'remainingWeightGrams', 'remainingPercent',
                  'initialWeightGrams', 'emptySpoolWeightGrams', 'printerId', 'storageLocationId',
                  'dryerId', 'purchasedAt', 'purchasePrice', 'reorderUrl', 'ownerId', 'notes')
        body = {key: spool.get(key) for key in fields}
        body['expectedUpdatedAt'] = spool['updatedAt']
        body.update(changes)
        return body

    def test_manual_edits_protect_printer_usage_and_validate_grams(self):
        api, printer, material, spool, numeric = self.fixture()
        path = '/api/spools/' + spool['id']
        stale = api.request('GET', path)
        printer.call('PUT', '/spool/' + str(numeric) + '/use', {'use_weight': 25})
        api.request('PATCH', path + '/remaining', {
            'remainingWeightGrams': 850, 'expectedUpdatedAt': stale['updatedAt'],
        }, expected=409)
        api.request('PUT', path, self.edit_body(stale, notes='Stale browser edit'), expected=409)
        current = api.request('GET', path)
        self.assertEqual(current['remainingWeightGrams'], 875)
        self.assertEqual(current['notes'], stale['notes'])
        fresh = api.request('PUT', path, self.edit_body(current, notes='Fresh browser edit'))
        self.assertEqual(fresh['remainingWeightGrams'], 875)
        self.assertEqual(fresh['notes'], 'Fresh browser edit')
        changed = api.request('PATCH', path + '/remaining', {
            'remainingWeightGrams': 250, 'remainingPercent': 99,
            'expectedUpdatedAt': fresh['updatedAt'],
        })
        self.assertEqual(changed['remainingWeightGrams'], 250)
        self.assertEqual(changed['remainingPercent'], 25,
                         'Legacy percentage must derive from authoritative grams, never override it')
        for amount in (-1, 1000000, 0.001):
            api.request('PATCH', path + '/remaining', {'remainingWeightGrams': amount}, expected=400)
            api.request('POST', '/api/spools', {
                'filamentMaterialId': material['id'], 'remainingWeightGrams': amount,
            }, expected=400)
        for field in ('remainingWeightGrams', 'initialWeightGrams', 'emptySpoolWeightGrams'):
            api.request('PUT', path, self.edit_body(changed, **{field: -1}), expected=400)
        self.assertEqual(api.request('GET', path)['remainingWeightGrams'], 250)
        new = api.request('POST', '/api/spools', {
            'filamentMaterialId': material['id'], 'initialWeightGrams': 500,
            'emptySpoolWeightGrams': 200, 'remainingWeightGrams': 125, 'remainingPercent': 99,
        }, 201)
        self.assertEqual(new['remainingPercent'], 25)

    def test_manual_uid_reassignment_keeps_printer_mapping_consistent(self):
        api, printer, material, spool, numeric = self.fixture()
        first_uid, second_uid = '04A1B2C3D4E5F6', 'AABBCCDD'
        printer.call('PATCH', '/spool/' + str(numeric), {
            'extra': {'card_uids': json.dumps(first_uid + ',' + second_uid)},
        })
        second = api.request('POST', '/api/spools', {
            'filamentMaterialId': material['id'], 'remainingWeightGrams': 800,
        }, 201)
        second_path = '/api/spools/' + second['id']
        edited = api.request('PUT', second_path, self.edit_body(second, rfidTagUid=first_uid))
        rows = printer.call('GET', '/spool?limit=1000')
        first = next(row for row in rows if row['id'] == numeric)
        self.assertEqual(json.loads(first['extra']['card_uids']), second_uid)
        status = api.request('GET', MANAGEMENT)
        second_numeric = next(row['id'] for row in status['spools'] if row['spoolId'] == second['id'])
        self.assertEqual(json.loads(printer.call('GET', '/spool/' + str(second_numeric))['extra']['card_uids']), first_uid)
        api.request('PUT', second_path, self.edit_body(edited, rfidTagUid=None))
        self.assertEqual(json.loads(printer.call('GET', '/spool/' + str(second_numeric))['extra']['card_uids']), '')

    def test_missing_density_rejects_length_usage_without_silent_estimate(self):
        api, printer, material, spool, numeric = self.fixture(density=None)
        path = '/spool/' + str(numeric)
        printer.call('PUT', path + '/use', {'use_length': 1000}, expected=422)
        self.assertEqual(api.request('GET', '/api/spools/' + spool['id'])['remainingWeightGrams'], 900)
        self.assertEqual(printer.call('PUT', path + '/use', {'use_weight': 10})['remaining_weight'], 890)

    def test_websocket_ping_updates_and_deleted_event(self):
        api, printer, material, spool, numeric = self.fixture()
        url = printer.url + printer.path + '/spool'
        with WebSocket(url) as stream:
            stream.send(9, b'Moonraker')
            self.assertEqual(stream.receive_until(lambda opcode, payload: opcode == 10 and payload == b'Moonraker'), b'Moonraker')
            # Moonraker tracks server pings; an accepted-but-silent socket is not
            # enough to validate its connection-health behaviour.
            stream.receive_until(lambda opcode, payload: opcode == 9, timeout=25)
            printer.call('PUT', '/spool/' + str(numeric) + '/use', {'use_weight': 5})
            event = stream.event('updated', numeric)
            self.assertEqual(event['payload']['remaining_weight'], 895)
            api.request('DELETE', '/api/spools/' + spool['id'], expected=204)
            stream.event('deleted', numeric)
        printer.call('GET', '/spool/' + str(numeric), expected=404)

    def test_management_permissions_rotation_and_disabled_tokens(self):
        api, printer, material, spool, numeric = self.fixture()
        api.request('GET', MANAGEMENT, expected=401, authenticated=False)
        suffix = uuid.uuid4().hex[:10]
        member = Api(self, api.url)
        password = 'Protocol-test-password-123!'
        email = 'protocol-member-' + suffix + '@example.test'
        member.request('POST', '/api/auth/register', {
            'email': email, 'username': 'protocol-member-' + suffix, 'password': password,
        }, authenticated=False)
        member.login(email, password)
        invitation = api.request('POST', '/api/projects/' + api.project + '/invitations', {'role': 'member'})
        member.request('POST', '/api/projects/join', {'token': invitation['token']})
        member.project = api.project
        for method, path in (('GET', MANAGEMENT), ('POST', MANAGEMENT + '/enable'),
                             ('POST', MANAGEMENT + '/rotate'), ('DELETE', MANAGEMENT)):
            member.request(method, path, expected=403)
        with WebSocket(printer.url + printer.path + '/spool') as stream:
            replacement = api.request('POST', MANAGEMENT + '/rotate')
            opcode, close_payload = stream.frame(timeout=8)
            self.assertEqual(opcode, 8, 'Rotating credentials must revoke existing printer WebSockets')
        rotated = api.printer(replacement['connectionPath'])
        self.assertNotEqual(printer.path, rotated.path)
        printer.call('GET', '/info', expected=DENIED)
        self.assertEqual(rotated.call('GET', '/spool/' + str(numeric))['remaining_weight'], 900)
        api.request('DELETE', MANAGEMENT, expected=(200, 204))
        self.assertFalse(api.request('GET', MANAGEMENT)['enabled'])
        rotated.call('GET', '/info', expected=DENIED)

    def test_backup_restore_preserves_numeric_ids_cards_and_tare_without_tokens(self):
        api, printer, material, spool, numeric = self.fixture()
        printer.call('PATCH', '/spool/' + str(numeric), {'extra': {'card_uids': json.dumps('04A1B2C3D4E5F6')}})
        printer.call('PUT', '/spool/' + str(numeric) + '/use', {'use_length': 1})
        untagged = api.request('POST', '/api/spools', {
            'filamentMaterialId': material['id'], 'initialWeightGrams': 500,
            'remainingWeightGrams': 450, 'emptySpoolWeightGrams': 150,
        }, 201)
        # Allocate a persistent compatibility number to the untagged spool.
        printer.call('GET', '/spool?limit=1000')
        deleted_highest = api.request('POST', '/api/spools', {
            'filamentMaterialId': material['id'], 'remainingWeightGrams': 50,
        }, 201)
        high_status = api.request('GET', MANAGEMENT)
        deleted_numeric = next(row['id'] for row in high_status['spools']
                               if row['spoolId'] == deleted_highest['id'])
        api.request('DELETE', '/api/spools/' + deleted_highest['id'], expected=204)
        document = api.request('GET', '/api/backup/export')
        serialized = json.dumps(document)
        token = printer.path.split('/api/spoolman/')[1].split('/')[0]
        self.assertNotIn(token, serialized, 'A transferable inventory must not disclose printer access credentials')
        exported = next(row for row in document['spools'] if row['id'] == spool['id'])
        self.assertEqual(exported['emptySpoolWeightGrams'], 225)
        # Export normally includes every administrated project. Keep this
        # fixture's project only so repeated test runs never collide with a
        # project restored by an earlier run or a different regression suite.
        document['projects'] = [row for row in document['projects'] if row['id'] == api.project]
        for collection in ('materials', 'owners', 'spools', 'printers', 'storageLocations', 'dryers'):
            document[collection] = [row for row in document[collection] if row.get('projectId') == api.project]
        document['materialFavorites'] = [row for row in document['materialFavorites']
                                         if row['materialId'] == material['id']]
        # Compatibility mapping collections carry their owning project ID.
        # Names are intentionally discovered from the format, so an added
        # mapping collection cannot be accidentally dropped by this fixture.
        for collection, rows in document.items():
            if collection not in ('projects', 'materials', 'owners', 'spools', 'printers',
                                  'storageLocations', 'dryers', 'materialFavorites') and isinstance(rows, list):
                document[collection] = [row for row in rows if row.get('projectId') == api.project]
        mapping = next(row for row in document['spoolmanMappings'] if row['spoolId'] == spool['id'])
        self.assertEqual(mapping['id'], numeric)
        self.assertGreater(mapping['usageRemainderGrams'], 0)
        untagged_mapping = next(row for row in document['spoolmanMappings'] if row['spoolId'] == untagged['id'])
        self.assertEqual(untagged_mapping['cardUids'], '')
        destination = self.client('DESTINATION')
        for mutation in ('negative_id', 'duplicate_id', 'unknown_spool', 'invalid_uid', 'invalid_remainder'):
            damaged = copy.deepcopy(document)
            row = damaged['spoolmanMappings'][0]
            if mutation == 'negative_id':
                row['id'] = -1
            elif mutation == 'duplicate_id':
                damaged['spoolmanMappings'].append(copy.deepcopy(row))
            elif mutation == 'unknown_spool':
                row['spoolId'] = str(uuid.uuid4())
            elif mutation == 'invalid_uid':
                row['cardUids'] = 'not-a-tag'
            else:
                row['usageRemainderGrams'] = 2
            destination.request('POST', '/api/backup/import', damaged, expected=400)
        destination.request('POST', '/api/backup/import', document)
        destination.project = api.project
        restored = destination.request('GET', '/api/spools/' + spool['id'])
        self.assertEqual(restored['ownerId'], spool['ownerId'])
        self.assertEqual(restored['emptySpoolWeightGrams'], 225)
        self.assertEqual(restored['initialWeightGrams'], 1000)
        self.assertFalse(destination.request('GET', MANAGEMENT)['enabled'])
        connection = destination.request('POST', MANAGEMENT + '/enable')
        restored_printer = destination.printer(connection['connectionPath'])
        mapped = restored_printer.call('GET', '/spool/' + str(numeric))
        self.assertEqual(mapped['id'], numeric)
        self.assertEqual(mapped['remaining_weight'], 900)
        self.assertEqual(mapped['spool_weight'], 225)
        self.assertEqual(json.loads(mapped['extra']['card_uids']), '04A1B2C3D4E5F6')
        self.assertEqual(mapped['filament']['vendor']['name'], material['brand'])
        # The pre-backup 1 mm plus this 3 mm must cross the 0.01 g threshold.
        # Losing the fractional remainder on transfer would leave 900 g.
        used = restored_printer.call('PUT', '/spool/' + str(numeric) + '/use', {'use_length': 3})
        self.assertEqual(used['remaining_weight'], 899.99)
        mapped_untagged = restored_printer.call('GET', '/spool/' + str(untagged_mapping['id']))
        self.assertEqual(mapped_untagged['initial_weight'], 500)
        self.assertEqual(mapped_untagged['remaining_weight'], 450)
        self.assertEqual(json.loads(mapped_untagged['extra']['card_uids']), '')
        new_spool = destination.request('POST', '/api/spools', {
            'filamentMaterialId': material['id'], 'remainingWeightGrams': 1000,
        }, 201)
        new_status = destination.request('GET', MANAGEMENT)
        new_number = next(row['id'] for row in new_status['spools'] if row['spoolId'] == new_spool['id'])
        self.assertGreater(new_number, deleted_numeric,
                           'A deleted spool number must not be reassigned after a backup transfer')

        # Old version-1 files predate tare, initial weights and compatibility
        # IDs. They must still restore without manufacturing or losing grams.
        legacy = copy.deepcopy(document)
        identities = {row['id']: str(uuid.uuid4()) for collection in (
            'projects', 'materials', 'owners', 'spools', 'printers', 'storageLocations', 'dryers')
            for row in legacy[collection]}

        def rewrite(value):
            if isinstance(value, dict):
                return {key: rewrite(item) for key, item in value.items()}
            if isinstance(value, list):
                return [rewrite(item) for item in value]
            return identities.get(value, value) if isinstance(value, str) else value

        legacy = rewrite(legacy)
        legacy.pop('spoolmanMappings', None)
        legacy.pop('spoolmanProjects', None)
        for row in legacy['spools']:
            row.pop('initialWeightGrams', None)
            row.pop('emptySpoolWeightGrams', None)
        destination.request('POST', '/api/backup/import', legacy)
        destination.project = identities[api.project]
        legacy_spool = destination.request('GET', '/api/spools/' + identities[spool['id']])
        self.assertEqual(legacy_spool['remainingWeightGrams'], 900)
        self.assertIsNone(legacy_spool.get('emptySpoolWeightGrams'))


if __name__ == '__main__':
    unittest.main(verbosity=2)
