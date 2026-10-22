"""Round-trip inventory between TWO disposable SpoolHero instances.

Required: SPOOLHERO_TEST_SOURCE_URL, SPOOLHERO_TEST_DESTINATION_URL,
SPOOLHERO_TEST_EMAIL, SPOOLHERO_TEST_PASSWORD (platform admin on each instance).
Optional per-instance credentials: SPOOLHERO_TEST_SOURCE_EMAIL/PASSWORD and
SPOOLHERO_TEST_DESTINATION_EMAIL/PASSWORD. Never use a real inventory database.

For persisted notification/dryer metadata and an induced mid-import transaction
failure, also set SPOOLHERO_TEST_MYSQL_BINARY, SPOOLHERO_TEST_MYSQL_SOCKET,
SPOOLHERO_TEST_SOURCE_DB and SPOOLHERO_TEST_DESTINATION_DB. The database helper
uses root with no password unless SPOOLHERO_TEST_MYSQL_USER/PASSWORD are supplied.
Set LD_LIBRARY_PATH if using the portable MariaDB test runtime.

Uses only Python's standard library. Run: python3 tests/inventory_backup_smoke.py
Creates sample users/projects and retains restored inventory for UI inspection.
"""
import base64
import copy
import http.client
import json
import os
import re
import struct
import subprocess
import unittest
import uuid
import zlib
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen


COLLECTIONS = ('projects', 'materials', 'owners', 'spools', 'printers',
               'storageLocations', 'dryers', 'materialFavorites')
FORMAT = 'SpoolHero.InventoryBackup'


class Api:
    def __init__(self, case, url):
        self.case = case
        self.url = url.rstrip('/')
        self.token = None
        self.project = None
        self.headers = {}

    def request(self, method, path, data=None, expected=200, authenticated=True,
                raw=None, content_type='application/json'):
        headers = {'Content-Type': content_type}
        if authenticated and self.token:
            headers['Authorization'] = 'Bearer ' + self.token
        if self.project:
            headers['X-Project-Id'] = self.project
        body = raw if raw is not None else (json.dumps(data, ensure_ascii=False).encode()
                                          if data is not None else None)
        req = Request(self.url + '/api/' + path, data=body, headers=headers, method=method)
        try:
            response = urlopen(req, timeout=120)
        except HTTPError as error:
            response = error
        value = response.read()
        self.headers = response.headers
        statuses = (expected,) if isinstance(expected, int) else expected
        self.case.assertIn(response.status, statuses,
                           (method, path, response.status, value[:2000].decode(errors='replace')))
        if not value:
            return None
        try:
            return json.loads(value)
        except json.JSONDecodeError:
            return value.decode(errors='replace')

    def login(self, email, password):
        self.token = self.request('POST', 'auth/login', {
            'email': email, 'password': password,
        }, authenticated=False)['accessToken']

    def export(self):
        result = self.request('GET', 'backup/export')
        self.case.assertIn('attachment', self.headers.get('Content-Disposition', ''))
        self.case.assertIn('application/json', self.headers.get('Content-Type', ''))
        return result

    def upload_image(self, collection, entity_id):
        boundary = 'spoolhero-test-' + uuid.uuid4().hex
        image = test_png()
        body = ('--' + boundary + '\r\nContent-Disposition: form-data; name="file"; filename="sample.png"'
                '\r\nContent-Type: image/png\r\n\r\n').encode() + image + ('\r\n--' + boundary + '--\r\n').encode()
        return self.request('POST', collection + '/' + entity_id + '/image', raw=body,
                            content_type='multipart/form-data; boundary=' + boundary)

    def reject_oversized_import(self):
        # Kestrel rejects the declared size before reading the body. Sending a full
        # 65 MiB buffer with urllib races that rejection and yields BrokenPipe.
        parsed = urlsplit(self.url)
        connection_type = http.client.HTTPSConnection if parsed.scheme == 'https' else http.client.HTTPConnection
        connection = connection_type(parsed.hostname, parsed.port, timeout=30)
        try:
            connection.putrequest('POST', parsed.path.rstrip('/') + '/api/backup/import')
            connection.putheader('Authorization', 'Bearer ' + self.token)
            connection.putheader('Content-Type', 'application/json')
            connection.putheader('Content-Length', str(64 * 1024 * 1024 + 1))
            connection.endheaders(b' ')
            response = connection.getresponse()
            self.case.assertEqual(response.status, 413, response.read()[:2000])
        finally:
            connection.close()


def test_png():
    def chunk(kind, payload):
        return struct.pack('!I', len(payload)) + kind + payload + struct.pack('!I', zlib.crc32(kind + payload))
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('!2I5B', 2, 2, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(b'\0' + b'\xff\x80\x00\xff' * 2
                                           + b'\0' + b'\x00\x80\xff\xff' * 2)) + chunk(b'IEND', b''))


def canonical(document):
    result = copy.deepcopy(document)
    result.pop('exportedAt', None)
    for name in COLLECTIONS:
        result[name] = sorted(result[name], key=lambda row: row.get('id', row.get('materialId', '')))
    return result


class TestDatabase:
    def __init__(self):
        self.binary = os.environ.get('SPOOLHERO_TEST_MYSQL_BINARY')
        self.source = os.environ.get('SPOOLHERO_TEST_SOURCE_DB')
        self.destination = os.environ.get('SPOOLHERO_TEST_DESTINATION_DB')
        self.enabled = bool(self.binary and self.source and self.destination)
        if self.enabled:
            for name in (self.source, self.destination):
                if not re.fullmatch(r'[A-Za-z0-9_]+', name):
                    raise ValueError('Invalid disposable database name')
            if self.source == self.destination:
                raise ValueError('Source and destination databases must be different')

    def execute(self, database, sql):
        if not self.enabled or database not in (self.source, self.destination):
            raise ValueError('Only explicitly configured disposable databases may be changed')
        command = [self.binary, '--no-defaults', '--batch', '--skip-column-names',
                   '--user=' + os.environ.get('SPOOLHERO_TEST_MYSQL_USER', 'root')]
        socket = os.environ.get('SPOOLHERO_TEST_MYSQL_SOCKET')
        if socket:
            command.append('--socket=' + socket)
        else:
            command += ['--host=' + os.environ.get('SPOOLHERO_TEST_MYSQL_HOST', '127.0.0.1'),
                        '--port=' + os.environ.get('SPOOLHERO_TEST_MYSQL_PORT', '3306')]
        env = dict(os.environ)
        env['MYSQL_PWD'] = os.environ.get('SPOOLHERO_TEST_MYSQL_PASSWORD', '')
        result = subprocess.run(command + [database, '--execute', sql], capture_output=True,
                                text=True, env=env, timeout=30, check=True)
        return result.stdout


class InventoryBackupSmoke(unittest.TestCase):
    def test_inventory_round_trip_validation_and_atomicity(self):
        source_url = os.environ['SPOOLHERO_TEST_SOURCE_URL']
        destination_url = os.environ['SPOOLHERO_TEST_DESTINATION_URL']
        self.assertNotEqual(source_url.rstrip('/'), destination_url.rstrip('/'),
                            'Use two disposable instances backed by different databases.')
        # This port is reserved for the user's persistent local inventory.
        self.assertNotIn(':5090', source_url)
        self.assertNotIn(':5090', destination_url)
        source = Api(self, source_url)
        destination = Api(self, destination_url)
        for name, client in (('SOURCE', source), ('DESTINATION', destination)):
            client.request('GET', 'backup/export', expected=401, authenticated=False)
            client.request('POST', 'backup/import', {}, expected=401, authenticated=False)
            client.login(os.environ.get('SPOOLHERO_TEST_' + name + '_EMAIL', os.environ['SPOOLHERO_TEST_EMAIL']),
                         os.environ.get('SPOOLHERO_TEST_' + name + '_PASSWORD', os.environ['SPOOLHERO_TEST_PASSWORD']))
        database = TestDatabase()
        suffix = uuid.uuid4().hex[:10]

        # Keep unrelated destination inventory in place throughout the restore.
        # This also permits exporting a baseline on a fresh installation.
        existing_project = destination.request('POST', 'projects', {
            'name': 'Existing destination inventory ' + suffix,
            'description': 'This project must remain unchanged during every test.',
        }, 201)
        destination.project = existing_project['id']
        existing_material = destination.request('POST', 'materials', self.material('Existing destination ' + suffix), 201)
        destination.request('POST', 'spools', {
            'filamentMaterialId': existing_material['id'], 'remainingWeightGrams': 500,
            'remainingPercent': 50, 'notes': 'Preserve destination inventory 🧵',
        }, 201)
        destination.request('POST', 'favorites/' + existing_material['id'], expected=204)

        # A separate user's project must not leak through a platform admin's export.
        outsider = self.create_nonadmin(source_url, suffix + 's')
        outsider_project = outsider.request('POST', 'projects', {
            'name': 'Excluded private project ' + suffix,
            'description': 'Must not appear in another user backup',
        }, 201)
        outsider.project = outsider_project['id']
        outsider_material = outsider.request('POST', 'materials', self.material('Private outsider ' + suffix), 201)

        projects = []
        fixtures = []
        for index in range(2):
            project = source.request('POST', 'projects', {
                'name': f'Backup {index} – Ångström 🧵 {suffix}',
                'description': 'Shared printer\nÄgarnas material 日本語',
            }, 201)
            source.project = project['id']
            projects.append(project)
            owner = source.request('POST', 'owners', {'name': f'Ägare {index} 🧵'}, 201)
            material = source.request('POST', 'materials', self.material(f'Own material {index} {suffix}'), 201)
            printer = source.request('POST', 'printers', {
                'name': 'Skrivare – 日本語', 'notes': 'Printer notes\n<script>plain text</script>',
                'rfidTagUid': 'printer-' + suffix + str(index),
            }, 201)
            storage = source.request('POST', 'storage-locations', {
                'name': 'Hylla Å', 'description': 'Torr förvaring 🧵', 'rfidTagUid': 'storage-' + suffix + str(index),
            }, 201)
            dryer = source.request('POST', 'dryers', {
                'name': 'Torkare 🧵', 'description': 'Dryer settings\n60°C',
                'rfidTagUid': 'dryer-' + suffix + str(index),
            }, 201)
            for collection, item in (('printers', printer), ('storage-locations', storage), ('dryers', dryer)):
                image = source.upload_image(collection, item['id'])
                self.assertTrue(image['imageBase64'])
                self.assertEqual(image['imageContentType'], 'image/png')
            source.request('POST', 'favorites/' + material['id'], expected=204)
            fixtures.append({'owner': owner, 'material': material, 'printer': printer, 'storage': storage, 'dryer': dryer})

        # A source project member can export their own admin projects, but not the
        # inventory of a project where they are merely a member.
        invitation = source.request('POST', 'projects/' + projects[0]['id'] + '/invitations', {'role': 'member'})
        outsider.request('POST', 'projects/join', {'token': invitation['token']})
        limited_export = outsider.export()
        self.assertIn(outsider_project['id'], {p['id'] for p in limited_export['projects']})
        self.assertNotIn(projects[0]['id'], {p['id'] for p in limited_export['projects']})

        global_used = source.request('POST', 'admin/materials', self.material('Shared catalogue ' + suffix), 201)
        global_favorite = source.request('POST', 'admin/materials', self.material('Favorite catalogue ' + suffix), 201)
        global_unused = source.request('POST', 'admin/materials', self.material('Unused catalogue ' + suffix), 201)
        source.request('POST', 'favorites/' + global_favorite['id'], expected=204)
        source.request('POST', 'materials/' + global_used['id'] + '/click', expected=204)
        source.request('POST', 'materials/' + fixtures[0]['material']['id'] + '/click', expected=204)
        tags = []
        spools = []
        for index, project in enumerate(projects):
            source.project = project['id']
            f = fixtures[index]
            for slot, relation in enumerate(('printerId', 'storageLocationId', 'dryerId', None)):
                material = f['material'] if slot < 3 else global_used
                request = {
                    'filamentMaterialId': material['id'], 'ownerId': f['owner']['id'] if slot != 2 else None,
                    'remainingWeightGrams': 731.25, 'remainingPercent': 113.13 if index == 1 and slot == 0 else 73.13,
                    'purchasedAt': '2023-02-03T04:05:06Z', 'purchasePrice': 21.95,
                    'reorderUrl': 'https://example.invalid/spool?color=orange&pack=1',
                    'notes': f'Spole {index}/{slot} – Ägare 🧵\n日本語\n<script>keep literal</script>',
                    'rfidTagUid': f'spool-{suffix}-{index}-{slot}',
                }
                if relation:
                    request[relation] = f[{'printerId': 'printer', 'storageLocationId': 'storage', 'dryerId': 'dryer'}[relation]]['id']
                spool = source.request('POST', 'spools', request, 201)
                for lifecycle in ('open', 'repackage', 'reopen', 'dry'):
                    spool = source.request('PUT', 'spools/' + spool['id'] + '/' + lifecycle)
                if slot == 3:
                    spool = source.request('PUT', 'spools/' + spool['id'] + '/consume')
                self.assertTrue(all(spool[key] for key in ('openedAt', 'repackagedAt', 'reopenedAt', 'driedAt')))
                spools.append(spool)
                encoded = source.request('POST', 'tags/encode', {'spoolId': spool['id']})
                tags.append((project['id'], 'spool', spool['id'], request['rfidTagUid'], encoded))
            for entity_type, key in (('printer', 'printer'), ('storage', 'storage'), ('dryer', 'dryer')):
                entity = f[key]
                encoded = source.request('POST', 'tags/encode-entity', {'entityType': entity_type, 'entityId': entity['id']})
                tags.append((project['id'], entity_type, entity['id'], entity['rfidTagUid'], encoded))

        if database.enabled:
            source_spool = str(uuid.UUID(spools[3]['id']))
            source_dryer = str(uuid.UUID(fixtures[0]['dryer']['id']))
            source_material = str(uuid.UUID(global_used['id']))
            database.execute(database.source,
                "UPDATE spools SET low_spool_notified_at='2024-01-02 03:04:05' WHERE id='" + source_spool + "'; "
                "UPDATE dryers SET is_drying=1,drying_started_at='2025-01-01 01:02:03',"
                "drying_finish_at='2100-01-01 01:02:03',drying_notified_at='2025-01-01 01:02:03' WHERE id='" + source_dryer + "'; "
                "UPDATE filament_materials SET ofd_filament_id='backup-filament-" + suffix + "',"
                "ofd_variant_id='backup-color-" + suffix + "' WHERE id='" + source_material + "';")

        source.request('PUT', 'admin/settings/material-catalog', {'openFilamentDbEnabled': False}, 204)
        destination.request('PUT', 'admin/settings/material-catalog', {'openFilamentDbEnabled': True}, 204)
        document = source.export()
        self.assertEqual(document['format'], FORMAT)
        self.assertEqual(document['version'], 1)
        self.assertIs(document['openFilamentDbEnabled'], False)
        self.assertNotIn(outsider_project['id'], {p['id'] for p in document['projects']})
        material_ids = {m['id'] for m in document['materials']}
        self.assertIn(global_used['id'], material_ids)
        self.assertIn(global_favorite['id'], material_ids)
        self.assertNotIn(global_unused['id'], material_ids)
        self.assertNotIn(outsider_material['id'], material_ids)
        self.assertIsNone(next(m for m in document['materials'] if m['id'] == global_used['id'])['projectId'])
        for project, fixture in zip(projects, fixtures):
            source.project = project['id']
            material = source.request('GET', 'materials/' + fixture['material']['id'])
            exported_material = next(m for m in document['materials'] if m['id'] == material['id'])
            for key, value in material.items():
                self.assertEqual(exported_material[key], value, 'Export lost material property ' + key)
            self.assertEqual(exported_material['projectId'], project['id'])
            for collection, endpoint, fixture_key in (('printers', 'printers', 'printer'),
                                                        ('storageLocations', 'storage-locations', 'storage'),
                                                        ('dryers', 'dryers', 'dryer')):
                entity = source.request('GET', endpoint + '/' + fixture[fixture_key]['id'])
                exported_entity = next(row for row in document[collection] if row['id'] == entity['id'])
                for key, value in entity.items():
                    self.assertEqual(exported_entity['imageData' if key == 'imageBase64' else key], value,
                                     'Export lost location property ' + key)
        for spool in spools:
            exported_spool = next(s for s in document['spools'] if s['id'] == spool['id'])
            for key in exported_spool.keys() & spool.keys():
                self.assertEqual(exported_spool[key], spool[key], 'Export lost spool property ' + key)
        if database.enabled:
            metadata_spool = next(s for s in document['spools'] if s['id'] == spools[3]['id'])
            self.assertTrue(metadata_spool['lowSpoolNotifiedAt'].startswith('2024-01-02T03:04:05'))
            metadata_dryer = next(d for d in document['dryers'] if d['id'] == fixtures[0]['dryer']['id'])
            self.assertIs(metadata_dryer['isDrying'], True)
            self.assertTrue(all(metadata_dryer[key] for key in ('dryingStartedAt', 'dryingFinishAt', 'dryingNotifiedAt')))
            external_material = next(m for m in document['materials'] if m['id'] == global_used['id'])
            self.assertEqual(external_material['ofdFilamentId'], 'backup-filament-' + suffix)
            self.assertEqual(external_material['ofdVariantId'], 'backup-color-' + suffix)
        self.assertTrue(all('filamentMaterial' not in spool and 'owner' not in spool for spool in document['spools']))
        for forbidden in ('users', 'passwordHash', 'smtpSettings', 'invitations', 'projectMembers', 'accessToken'):
            self.assertNotIn(forbidden, json.dumps(document))

        nonadmin_destination = self.create_nonadmin(destination_url, suffix + 'd')
        before = canonical(destination.export())
        nonadmin_destination.request('POST', 'backup/import', document, 403)
        self.assertEqual(before, canonical(destination.export()))

        # Every rejected document must leave all existing destination inventory and
        # the catalogue setting unchanged, even when a valid project comes first.
        def reject(label, mutate, expected=400):
            damaged = copy.deepcopy(document)
            mutate(damaged)
            with self.subTest(rejected=label):
                destination.request('POST', 'backup/import?applyCatalogSetting=true', damaged, expected)
                self.assertEqual(before, canonical(destination.export()), label + ' caused partial writes')

        reject('wrong format', lambda d: d.update(format='Other.Inventory'))
        reject('unsupported version', lambda d: d.update(version=999))
        reject('missing format', lambda d: d.pop('format'))
        for collection in COLLECTIONS:
            reject('null ' + collection, lambda d, name=collection: d.update({name: None}))
            reject('missing ' + collection, lambda d, name=collection: d.pop(name))
        reject('duplicate project GUID', lambda d: d['projects'].append(copy.deepcopy(d['projects'][0])))
        reject('duplicate spool GUID', lambda d: d['spools'].append(copy.deepcopy(d['spools'][0])))
        reject('empty spool GUID', lambda d: d['spools'][0].update(id=str(uuid.UUID(int=0))))
        reject('unknown project', lambda d: d['owners'][0].update(projectId=str(uuid.uuid4())))
        reject('missing material', lambda d: d['spools'][0].update(filamentMaterialId=str(uuid.uuid4())))
        for field, foreign in (('ownerId', fixtures[1]['owner']), ('printerId', fixtures[1]['printer']),
                               ('storageLocationId', fixtures[1]['storage']), ('dryerId', fixtures[1]['dryer']),
                               ('filamentMaterialId', fixtures[1]['material'])):
            reject('cross-project ' + field, lambda d, key=field, row=foreign:
                   next(s for s in d['spools'] if s['id'] == spools[0]['id']).update({key: row['id']}))
        reject('unknown favorite', lambda d: d['materialFavorites'].append({'materialId': str(uuid.uuid4()), 'createdAt': d['exportedAt']}))
        reject('invalid color', lambda d: d['materials'][0].update(colorHex='invalid'))
        reject('invalid image bytes', lambda d: d['printers'][0].update(imageData='not-base64!'))
        reject('unsupported image type', lambda d: d['printers'][0].update(imageContentType='text/html'))
        reject('oversize inventory image', lambda d: d['printers'][0].update(
            imageData=base64.b64encode(b'x' * (4 * 1024 * 1024 + 1)).decode()))
        reject('invalid lifecycle date', lambda d: d['spools'][0].update(openedAt='0001-01-01T00:00:00'))
        reject('out-of-range amount', lambda d: d['spools'][0].update(remainingWeightGrams=1000000))
        reject('unsupported precision', lambda d: d['spools'][0].update(remainingWeightGrams=1.234))
        destination.request('POST', 'backup/import', raw=b'{"broken":', expected=400)
        self.assertEqual(before, canonical(destination.export()))

        if os.environ.get('SPOOLHERO_TEST_LARGE_BACKUP') == '1':
            destination.reject_oversized_import()
            self.assertEqual(before, canonical(destination.export()))

        if database.enabled:
            trigger = 'backup_test_fail_' + suffix
            try:
                database.execute(database.destination,
                    'CREATE TRIGGER `' + trigger + '` BEFORE INSERT ON spools FOR EACH ROW '
                    "SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Intentional backup rollback test'")
                destination.request('POST', 'backup/import?applyCatalogSetting=true', document, 400)
                self.assertEqual(before, canonical(destination.export()), 'Database error left partially imported rows')
                orphan_members = database.execute(database.destination,
                    'SELECT COUNT(*) FROM project_members m LEFT JOIN projects p ON p.id=m.project_id WHERE p.id IS NULL')
                self.assertEqual(orphan_members.strip(), '0')
            finally:
                database.execute(database.destination, 'DROP TRIGGER IF EXISTS `' + trigger + '`')

            # Fail on the final site-wide setting update, after inventory and
            # favorites have been inserted, to verify the entire transaction.
            try:
                database.execute(database.destination,
                    'CREATE TRIGGER `' + trigger + '` AFTER UPDATE ON site_settings FOR EACH ROW '
                    "SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Intentional settings rollback test'")
                destination.request('POST', 'backup/import?applyCatalogSetting=true', document, 400)
                self.assertEqual(before, canonical(destination.export()), 'Settings error left partial inventory')
            finally:
                database.execute(database.destination, 'DROP TRIGGER IF EXISTS `' + trigger + '`')

        result = destination.request('POST', 'backup/import', document)
        self.assertEqual(set(result['projectIds']), {p['id'] for p in document['projects']})
        for count_key, collection in (('projectCount', 'projects'), ('materialCount', 'materials'), ('ownerCount', 'owners'),
                                      ('spoolCount', 'spools'), ('printerCount', 'printers'), ('storageLocationCount', 'storageLocations'),
                                      ('dryerCount', 'dryers'), ('favoriteCount', 'materialFavorites')):
            self.assertEqual(result[count_key], len(document[collection]), count_key)
        self.assertIs(result['catalogSettingApplied'], False)
        self.assertIs(destination.request('GET', 'admin/settings/material-catalog')['openFilamentDbEnabled'], True)
        restored = destination.export()
        for collection in COLLECTIONS:
            expected_rows = canonical(document)[collection]
            actual_rows = canonical(restored)[collection]
            self.assertEqual(actual_rows, sorted(before[collection] + expected_rows,
                                                 key=lambda row: row.get('id', row.get('materialId', ''))), collection)
        for project in projects:
            destination.project = project['id']
            self.assertEqual(destination.request('GET', 'projects/' + project['id'])['myRole'], 'admin')
        self.assertEqual(set(destination.request('GET', 'favorites')),
                         {f['materialId'] for f in before['materialFavorites'] + document['materialFavorites']})
        for spool in spools:
            destination.project = spool['projectId']
            self.assertEqual(destination.request('GET', 'spools/' + spool['id']), spool)
        for project, kind, entity_id, serial, encoded in tags:
            destination.project = project
            identified = destination.request('POST', 'inventory/identify', {'jsonPayload': encoded['jsonPayload']})
            self.assertEqual((identified['entityType'], identified['entityId']), (kind, entity_id))
            scanned = destination.request('POST', 'inventory/identify', {'serialNumber': serial})
            self.assertEqual((scanned['entityType'], scanned['entityId']), (kind, entity_id))
            if kind == 'spool':
                tag = destination.request('POST', 'tags/encode', {'spoolId': entity_id})
                self.assertEqual(tag['base64'], encoded['base64'], 'Existing NFC spool payload changed')

        after = canonical(destination.export())
        destination.request('POST', 'backup/import?applyCatalogSetting=true', document, 409)
        self.assertEqual(after, canonical(destination.export()), 'GUID collision changed existing inventory')
        source_before_collision = canonical(source.export())
        source.request('POST', 'backup/import', document, 409)
        self.assertEqual(source_before_collision, canonical(source.export()))

        # A separate, empty project tests the explicit site-wide setting opt-in
        # without colliding with restored identities.
        setting_doc = {key: [] for key in COLLECTIONS}
        setting_doc.update(format=FORMAT, version=1, exportedAt=document['exportedAt'], openFilamentDbEnabled=False)
        setting_doc['projects'] = [{**document['projects'][0], 'id': str(uuid.uuid4()), 'name': 'Settings restore ' + suffix}]
        result = destination.request('POST', 'backup/import?applyCatalogSetting=true', setting_doc)
        self.assertIs(result['catalogSettingApplied'], True)
        self.assertIs(destination.request('GET', 'admin/settings/material-catalog')['openFilamentDbEnabled'], False)
        destination.project = projects[0]['id']
        self.assertEqual(destination.request('GET', 'spools/' + spools[3]['id'])['filamentMaterialId'], global_used['id'])
        self.assertEqual(destination.request('GET', 'materials/' + global_used['id'])['id'], global_used['id'])

        print('Verified complete export/import, Unicode, materials and shared catalogue dependencies, owner/location relations,')
        print('all lifecycle dates, image bytes, favorites, caller project scope, unauthenticated/nonadmin access,')
        print('malformed/cross-project documents, GUID collision atomicity, retained NFC/serial tag identity, and setting opt-in.')
        if database.enabled:
            print('Also verified persisted notification/dryer metadata and rollback after an induced database write failure.')
        print('Restored inspection project: ' + projects[0]['id'])

    def create_nonadmin(self, url, suffix):
        client = Api(self, url)
        email = 'backup-' + suffix + '@example.invalid'
        password = 'Backup-test-42!-' + suffix
        registration = client.request('POST', 'auth/register', {
            'username': 'backup-' + suffix, 'email': email, 'password': password,
        })
        self.assertFalse(registration.get('requiresEmailVerification'),
                         'Use disposable test instances with SMTP disabled.')
        client.login(email, password)
        return client

    @staticmethod
    def material(brand):
        return {
            'type': 'PLA+', 'brand': brand, 'colorHex': 'FF6400', 'colorName': 'Orange – Å 🧵',
            'minTempCelsius': 195, 'maxTempCelsius': 225, 'diameterMm': 2.85, 'weightGrams': 750,
            'bedTempCelsius': 65, 'densityGCm3': 1.24, 'dryTempCelsius': 50, 'dryTimeHours': 8,
            'notes': 'Shared material settings\n日本語\n<script>literal text</script>',
            'reorderUrl': 'https://example.invalid/material?color=orange&weight=750',
            'pricePerKg': 29.95, 'isPublic': False,
        }


if __name__ == '__main__':
    unittest.main()
