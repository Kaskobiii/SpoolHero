"""Disposable Docker installation only: persist a real spool across recreation."""
import json
import sys
from pathlib import Path
from urllib.request import Request, urlopen

mode, base, fixture = sys.argv[1:]
assert base == 'http://127.0.0.1:5199', 'Only run on the disposable CI installation'

def api(method, path, body=None, project=None):
    headers = {'Content-Type': 'application/json'}
    if project:
        headers['X-Project-Id'] = project
    data = None if body is None else json.dumps(body).encode()
    with urlopen(Request(base + path, data=data, headers=headers, method=method), timeout=30) as response:
        raw = response.read()
        return json.loads(raw) if raw else None

if mode == 'create':
    project = api('POST', '/api/projects', {'name': 'Docker persistence test'})['id']
    owner = api('POST', '/api/owners', {'name': 'Container owner'}, project)['id']
    material = api('POST', '/api/materials', {
        'brand': 'Local', 'type': 'PLA', 'colorHex': '2266BB',
        'diameterMm': 1.75, 'densityGCm3': 1.24, 'weightGrams': 1000,
        'minTempCelsius': 200, 'maxTempCelsius': 220}, project)['id']
    spool = api('POST', '/api/spools', {
        'filamentMaterialId': material, 'ownerId': owner, 'rfidTagUid': 'AABBCCDD',
        'remainingWeightGrams': 850, 'initialWeightGrams': 1000,
        'emptySpoolWeightGrams': 190, 'notes': 'Survives container replacement'}, project)
    Path(fixture).write_text(json.dumps({'project': project, 'spool': spool,
                                       'identity': api('GET', '/api/local/session')['id']}))
elif mode == 'verify':
    saved = json.loads(Path(fixture).read_text())
    assert api('GET', '/api/local/session')['id'] == saved['identity']
    spool = api('GET', '/api/spools/' + saved['spool']['id'], project=saved['project'])
    for key in ['id', 'ownerId', 'rfidTagUid', 'remainingWeightGrams',
                'initialWeightGrams', 'emptySpoolWeightGrams', 'notes']:
        assert spool[key] == saved['spool'][key], (key, spool[key], saved['spool'][key])
    print('Saved inventory survived app container replacement.')
else:
    raise ValueError(mode)
