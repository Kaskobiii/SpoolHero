"""Verify catalog configuration and project material visibility on a disposable instance.

Set SPOOLHERO_TEST_URL, SPOOLHERO_TEST_EMAIL and SPOOLHERO_TEST_PASSWORD to an
admin account. SMTP must be disabled so the temporary ordinary user can log in.
Creates uniquely named test inventory. Restores the catalog setting afterwards.
Run: python3 tests/material_catalog_smoke.py
"""
import base64
import gzip
import json
import os
import unittest
import uuid
from urllib.error import HTTPError
from urllib.request import Request, urlopen


class MaterialCatalogSmoke(unittest.TestCase):
    def test_catalog_visibility_persistence_and_saved_references(self):
        base = os.environ['SPOOLHERO_TEST_URL'].rstrip('/')
        token = None
        project_id = None

        def api(method, path, body=None, expected=200, authenticated=True):
            headers = {'Content-Type': 'application/json'}
            if authenticated and token:
                headers['Authorization'] = 'Bearer ' + token
            if project_id:
                headers['X-Project-Id'] = project_id
            request = Request(base + '/api/' + path,
                              data=json.dumps(body).encode() if body is not None else None,
                              headers=headers, method=method)
            try:
                response = urlopen(request, timeout=30)
            except HTTPError as error:
                response = error
            raw = response.read().decode()
            self.assertEqual(response.status, expected, (method, path, raw))
            return json.loads(raw) if raw else None

        def ids(materials):
            return {m['id'] for m in materials}

        path = 'admin/settings/material-catalog'
        api('GET', path, expected=401, authenticated=False)
        token = api('POST', 'auth/login', {
            'email': os.environ['SPOOLHERO_TEST_EMAIL'],
            'password': os.environ['SPOOLHERO_TEST_PASSWORD'],
        })['accessToken']
        admin_token = token
        original = api('GET', path)
        suffix = uuid.uuid4().hex[:8]
        project_a = api('POST', 'projects', {'name': 'Catalog test A ' + suffix}, 201)['id']
        project_b = api('POST', 'projects', {'name': 'Catalog test B ' + suffix}, 201)['id']
        material_request = {
            'brand': 'Catalog' + suffix, 'type': 'PLA', 'colorHex': '1155AA',
            'minTempCelsius': 190, 'maxTempCelsius': 220, 'weightGrams': 1000,
        }

        try:
            api('PUT', path, {'openFilamentDbEnabled': True}, 204)
            global_material = api('POST', 'admin/materials', {
                **material_request, 'type': 'GLOBAL' + suffix, 'colorName': 'Global' + suffix,
            }, 201)
            project_id = project_b
            other_material = api('POST', 'materials', material_request, 201)
            project_id = project_a
            own_material = api('POST', 'materials', {
                **material_request, 'type': 'OWN' + suffix, 'colorName': 'Own' + suffix,
            }, 201)
            expected_ids = {own_material['id'], global_material['id']}
            self.assertEqual(ids(api('GET', 'materials?search=Catalog' + suffix)), expected_ids)
            self.assertNotIn(other_material['id'], ids(api('GET', 'materials')))
            spool = api('POST', 'spools', {
                'filamentMaterialId': global_material['id'], 'remainingWeightGrams': 800,
                'remainingPercent': 80,
            }, 201)
            api('POST', 'favorites/' + global_material['id'], expected=204)
            api('POST', 'favorites/' + own_material['id'], expected=204)

            api('PUT', path, {'openFilamentDbEnabled': False}, 204)
            self.assertFalse(api('GET', path)['openFilamentDbEnabled'])
            self.assertEqual(ids(api('GET', 'materials')), {own_material['id']})
            self.assertEqual(api('GET', 'materials/count')['count'], 1)
            page = api('GET', 'materials/paged?pageSize=1')
            self.assertEqual(page['totalCount'], 1)
            self.assertEqual(ids(page['items']), {own_material['id']})
            self.assertEqual(page['types'], ['OWN' + suffix])
            self.assertEqual(page['colors'], ['Own' + suffix])
            self.assertEqual(ids(api('GET', 'materials/search?q=Catalog' + suffix)), {own_material['id']})
            selected = ','.join(expected_ids)
            self.assertEqual(ids(api('GET', 'materials?ids=' + selected)), {own_material['id']})
            self.assertEqual(ids(api('GET', 'materials/paged?favoritesOnly=true')['items']), {own_material['id']})
            for export_path in ('materials/export', 'materials/export?ids=' + selected):
                exported = api('GET', export_path)['base64']
                payload = json.loads(gzip.decompress(base64.b64decode(exported)))
                self.assertEqual({m['Id'] for m in payload['Materials']}, {own_material['id']})
            api('GET', 'materials/export/orca?ids=' + global_material['id'], expected=404)
            self.assertEqual(api('GET', 'materials/' + global_material['id'])['id'], global_material['id'])
            saved = api('GET', 'spools/' + spool['id'])
            self.assertEqual(saved['filamentMaterialId'], global_material['id'])
            self.assertEqual(saved['materialType'], 'GLOBAL' + suffix)
            self.assertIn('disabled', api('POST', 'admin/ofd-sync', expected=400)['message'])

            normal_email = 'catalog-' + suffix + '@example.test'
            normal_password = 'Catalog-test-' + suffix
            registered = api('POST', 'auth/register', {
                'username': 'catalog-' + suffix, 'email': normal_email, 'password': normal_password,
            })
            self.assertFalse(registered['requiresEmailVerification'], 'Use a disposable instance with SMTP disabled.')
            token = api('POST', 'auth/login', {'email': normal_email, 'password': normal_password})['accessToken']
            api('GET', path, expected=403)
            api('PUT', path, {'openFilamentDbEnabled': True}, expected=403)
            api('POST', 'admin/ofd-sync', expected=403)
            token = admin_token
            self.assertFalse(api('GET', path)['openFilamentDbEnabled'])
            api('PUT', path, {'openFilamentDbEnabled': True}, 204)
            self.assertEqual(ids(api('GET', 'materials?search=Catalog' + suffix)), expected_ids)
            print('Verified: persistent catalog setting, admin authorization, list/count/search/picker/favorites/export filtering, sync guard, project isolation, and preserved spool material references.')
        finally:
            token = admin_token
            api('PUT', path, original, 204)


if __name__ == '__main__':
    unittest.main()
