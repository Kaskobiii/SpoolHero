"""No-login regression against two disposable servers, never port 5090.

Set SPOOLHERO_TEST_SOURCE_URL and SPOOLHERO_TEST_DESTINATION_URL. Optionally
SPOOLHERO_TEST_LEGACY_FIXTURE points to a pre-upgrade snapshot of projects,
spools and identityId made using the old authenticated build. No credentials,
JWT secret or session cookie are used by the new servers or these requests.
"""
import json, os, unittest, uuid
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import Request,urlopen
from spoolman_compat_smoke import WebSocket

class SharedInventorySmoke(unittest.TestCase):
    def setUp(self):
        self.source=os.environ['SPOOLHERO_TEST_SOURCE_URL'].rstrip('/')
        self.destination=os.environ['SPOOLHERO_TEST_DESTINATION_URL'].rstrip('/')
        assert self.source!=self.destination
        assert all(urlsplit(u).port!=5090 for u in (self.source,self.destination))

    def api(self,method,path,body=None,project=None,expected=200,base=None,headers=None):
        h={'Content-Type':'application/json'}
        if project: h['X-Project-Id']=project
        h.update(headers or {})
        try: response=urlopen(Request((base or self.source)+path,data=json.dumps(body).encode() if body is not None else None,headers=h,method=method),timeout=30)
        except HTTPError as ex: response=ex
        raw=response.read()
        self.assertEqual(response.status,expected,(method,path,raw[:1000]))
        self.assertNotIn('Set-Cookie',response.headers)
        if not raw:return None
        self.assertTrue('json' in response.headers.get('Content-Type',''),response.headers)
        return json.loads(raw)

    def test_shared_inventory_without_login(self):
        identity=self.api('GET','/api/local/session')
        self.assertTrue(identity['isPlatformAdmin'])
        self.assertNotIn('accessToken',identity)
        self.assertEqual(identity,self.api('GET','/api/local/session',headers={'Authorization':'Bearer expired-or-invented-token'}))
        projects=self.api('GET','/api/projects');self.assertGreater(len(projects),0)
        fixture_path=os.environ.get('SPOOLHERO_TEST_LEGACY_FIXTURE')
        if fixture_path:
            old=json.loads(Path(fixture_path).read_text())
            self.assertEqual(identity['id'],old['identityId'])
            self.assertTrue({p['id'] for p in old['projects']} <= {p['id'] for p in projects})
            for saved in old['spools']:
                got=self.api('GET','/api/spools/'+saved['id'],project=saved['projectId'])
                for key in ['id','projectId','ownerId','notes','rfidTagUid','initialWeightGrams','emptySpoolWeightGrams','remainingWeightGrams']:
                    self.assertEqual(got[key],saved[key],key)
            favorites=self.api('GET','/api/favorites')
            self.assertTrue({m['id'] for m in old['materials']} <= set(favorites))
        for method,path in [('GET','/api/auth/me'),('POST','/api/auth/login'),('POST','/api/auth/register'),('PUT','/api/auth/password'),('POST','/api/auth/forgot-password'),('DELETE','/api/auth/account'),('GET','/api/admin/users'),('GET','/api/projects/'+projects[0]['id']+'/members'),('POST','/api/projects/'+projects[0]['id']+'/invitations')]:
            self.api(method,path,{},expected=404)
        for path in ['/login','/register','/forgot-password','/reset-password','/verify-email','/settings/account','/settings/password','/admin/users']:
            with urlopen(self.source+path,timeout=20) as response:
                self.assertEqual(urlsplit(response.url).path,'/')
                self.assertIn('text/html',response.headers.get('Content-Type',''))
        self.api('GET','/api/spools',expected=400) # Project selection is organization, not authentication.
        self.api('GET','/api/spools',project=str(uuid.uuid4()),expected=403)
        suffix=uuid.uuid4().hex[:8]
        p=self.api('POST','/api/projects',{'name':'No login '+suffix},expected=201)['id']
        owner=self.api('POST','/api/owners',{'name':'Shared owner'},project=p,expected=201)
        material=self.api('POST','/api/materials',{'brand':'Local','type':'PLA','colorHex':'2266BB','diameterMm':1.75,'densityGCm3':1.24,'weightGrams':1000,'minTempCelsius':200,'maxTempCelsius':220},project=p,expected=201)
        spool=self.api('POST','/api/spools',{'filamentMaterialId':material['id'],'ownerId':owner['id'],'rfidTagUid':'AABBCCDD','remainingWeightGrams':850,'initialWeightGrams':1000,'emptySpoolWeightGrams':190},project=p,expected=201)
        self.api('PUT','/api/local/dashboard-onboarding',{'dismissed':True},expected=204)
        self.api('GET','/api/admin/settings/material-catalog')
        self.api('POST','/api/favorites/'+material['id'],expected=204)
        self.assertIn(material['id'],self.api('GET','/api/favorites',headers={'Authorization':'Bearer irrelevant'}))
        self.api('POST','/api/owners',{'name':'Cross site forbidden'},project=p,headers={'Origin':'https://unrelated.example'},expected=403)
        self.api('POST','/api/owners',{'name':'Same origin allowed'},project=p,headers={'Origin':self.source},expected=201)
        connection=self.api('POST','/api/integrations/spoolman/enable',project=p)['connectionPath']
        wirebase=connection+'/api/v1'
        self.api('GET',wirebase+'/info')
        row=next(s for s in self.api('GET',wirebase+'/spool') if 'AABBCCDD' in json.loads(s['extra']['card_uids']))
        with WebSocket(self.source+wirebase+'/spool') as ws:
            ws.send(9,b'no-login');ws.receive_until(lambda opcode,payload:opcode==10 and payload==b'no-login')
            self.api('PUT',wirebase+'/spool/'+str(row['id'])+'/use',{'use_weight':1.25})
        after=self.api('GET','/api/spools/'+spool['id'],project=p)
        self.assertEqual(after['remainingWeightGrams'],848.75)
        self.assertEqual(after['emptySpoolWeightGrams'],190)
        self.assertEqual(after['ownerId'],owner['id'])
        exported=self.api('GET','/api/backup/export')
        self.assertTrue(set(x['id'] for x in self.api('GET','/api/projects'))<=set(x['id'] for x in exported['projects']))
        self.assertNotIn('users',exported)
        result=self.api('POST','/api/backup/import',exported,base=self.destination)
        self.assertIn(p,result['projectIds'])
        restored=self.api('GET','/api/spools/'+spool['id'],project=p,base=self.destination)
        for key in ['ownerId','remainingWeightGrams','emptySpoolWeightGrams','rfidTagUid']:
            self.assertEqual(restored[key],after[key])
        self.assertFalse(self.api('GET','/api/integrations/spoolman',project=p,base=self.destination)['enabled'])
        self.api('POST','/api/backup/import',exported,base=self.destination,expected=409)
        self.assertEqual(self.api('GET','/api/spools/'+spool['id'],project=p,base=self.destination),restored)
        print('Verified credential-free access, removed account routes, preserved identity/inventory/favorites, shared CRUD/preferences, browser-origin checks, printer WS/usage, full backup restore and collision protection.')

if __name__=='__main__':unittest.main(verbosity=2)
