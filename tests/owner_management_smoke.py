"""Exercise owner persistence and project isolation against a disposable running instance.

Set SPOOLHERO_TEST_URL, SPOOLHERO_TEST_EMAIL and SPOOLHERO_TEST_PASSWORD.
Creates two uniquely named projects and sample inventory, retained for UI inspection.
Uses only the Python standard library. Run: python3 tests/owner_management_smoke.py
"""
import json
import os
import unittest
import uuid
from urllib.error import HTTPError
from urllib.request import Request, urlopen


class OwnerManagementSmoke(unittest.TestCase):
    def test_owner_lifecycle_and_project_isolation(self):
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

        api('GET', 'owners', expected=401, authenticated=False)
        token = api('POST', 'auth/login', {
            'email': os.environ['SPOOLHERO_TEST_EMAIL'],
            'password': os.environ['SPOOLHERO_TEST_PASSWORD'],
        })['accessToken']
        api('GET', 'owners', expected=400)  # No project selected.
        suffix = uuid.uuid4().hex[:8]
        project_a = api('POST', 'projects', {'name': 'Owner tests A ' + suffix}, 201)['id']
        project_b = api('POST', 'projects', {'name': 'Owner tests B ' + suffix}, 201)['id']
        project_id = project_a
        owner = api('POST', 'owners', {'name': '  Alice  '}, 201)
        self.assertEqual(owner['name'], 'Alice')
        api('POST', 'owners', {'name': 'alice'}, 409)
        api('POST', 'owners', {'name': '   '}, 400)
        api('POST', 'owners', {'name': 'x' * 101}, 400)
        api('POST', 'owners', {'name': None}, 400)
        bob = api('POST', 'owners', {'name': 'Bob'}, 201)
        api('PUT', 'owners/' + bob['id'], {'name': 'Alice'}, 409)
        api('PUT', 'owners/' + bob['id'], {'name': ' '}, 400)
        material = api('POST', 'materials', {
            'brand': 'Owner test', 'type': 'PLA', 'colorHex': '33AACC',
            'weightGrams': 1000, 'minTempCelsius': 190, 'maxTempCelsius': 220,
        }, 201)
        spool_request = {
            'filamentMaterialId': material['id'], 'remainingWeightGrams': 750,
            'remainingPercent': 75, 'notes': 'Keep this note\n<script>plain text</script>',
        }
        spool = api('POST', 'spools', spool_request, 201)
        self.assertIsNone(spool['ownerId'])
        self.assertIsNone(spool['ownerName'])
        spool_request['ownerId'] = owner['id']
        updated = api('PUT', 'spools/' + spool['id'], spool_request)
        self.assertEqual(updated['ownerName'], 'Alice')
        self.assertEqual(updated['notes'], spool_request['notes'])
        created = api('POST', 'spools', spool_request, 201)
        self.assertEqual(created['ownerId'], owner['id'])
        # The copy action uses the same create request and must preserve ownership.
        copied = api('POST', 'spools', {**spool_request, 'ownerId': created['ownerId']}, 201)
        self.assertEqual(copied['ownerId'], owner['id'])
        api('PUT', 'owners/' + owner['id'], {'name': 'Ålice 🧵'})
        self.assertEqual(api('GET', 'spools/' + spool['id'])['ownerName'], 'Ålice 🧵')
        self.assertTrue(all(s['ownerName'] == 'Ålice 🧵' for s in api('GET', 'spools')))
        api('PUT', 'spools/' + spool['id'], {**spool_request, 'ownerId': None})
        self.assertIsNone(api('GET', 'spools/' + spool['id'])['ownerId'])
        project_id = project_b
        self.assertEqual(api('GET', 'owners'), [])
        other_owner = api('POST', 'owners', {'name': 'Ålice 🧵'}, 201)
        api('PUT', 'owners/' + owner['id'], {'name': 'Wrong project'}, 404)
        api('POST', 'spools', spool_request, 400)  # Owner from A cannot be assigned in B.
        project_id = project_a
        api('PUT', 'spools/' + spool['id'], {**spool_request, 'ownerId': other_owner['id']}, 400)
        api('POST', 'spools', {**spool_request, 'ownerId': str(uuid.uuid4())}, 400)
        self.assertIsNone(api('GET', 'spools/' + spool['id'])['ownerId'])
        # Leave representative groups and an unassigned spool for browser verification.
        api('POST', 'spools', {**spool_request, 'ownerId': bob['id']}, 201)
        print('Verified: validation, duplicates, Unicode rename, create/edit/copy/unassign, notes preservation, project isolation.')
        print('UI inspection project: ' + project_a)


if __name__ == '__main__':
    unittest.main()
