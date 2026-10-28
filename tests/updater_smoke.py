"""Exercise the private updater without access to a Docker daemon."""
import importlib.util
import json
from pathlib import Path
import threading
from types import SimpleNamespace
import unittest
from urllib.request import Request, urlopen
from urllib.error import HTTPError
from http.server import ThreadingHTTPServer

spec = importlib.util.spec_from_file_location('updater', Path(__file__).resolve().parents[1] / 'deploy/updater/server.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class FakeDocker:
    def __init__(self, latest='new', fail=False):
        self.current, self.latest, self.fail, self.calls = 'old', latest, fail, []
    def __call__(self, args, **kwargs):
        self.calls.append(args)
        output, code = '', 0
        if args[:3] == ['docker', 'compose', 'ps']: output = 'container'
        elif args[:2] == ['docker', 'inspect']: output = self.current
        elif args[:3] == ['docker', 'compose', 'config']:
            output = json.dumps({'services': {'app': {'image': 'registry/app:latest', 'environment': {'SECRET': 'never-expose'}}}})
        elif args[:3] == ['docker', 'compose', 'pull']: code = int(self.fail)
        elif args[:3] == ['docker', 'image', 'inspect']:
            output = self.latest if args[4] == '{{.Id}}' else args[-1] + '-revision'
        elif args == ['bash', 'scripts/update.sh']: self.current = self.latest
        else: raise AssertionError(args)
        return SimpleNamespace(stdout=output, returncode=code)

class UpdaterTests(unittest.TestCase):
    def make(self, **kwargs):
        fake = FakeDocker(**kwargs)
        return module.Updater('/installation', fake), fake
    def test_same_image_does_not_restart_or_back_up(self):
        updater, fake = self.make(latest='old')
        updater.work('update')
        self.assertEqual(updater.snapshot()['phase'], 'current')
        self.assertFalse(any(c[0] == 'bash' for c in fake.calls))
    def test_check_reports_available_without_changing_app(self):
        updater, fake = self.make()
        updater.work('check')
        self.assertTrue(updater.snapshot()['available'])
        self.assertEqual(fake.current, 'old')
        self.assertNotIn('never-expose', json.dumps(updater.snapshot()))
    def test_update_runs_backup_script_and_verifies_running_image(self):
        updater, fake = self.make()
        updater.work('update')
        self.assertEqual(updater.snapshot()['phase'], 'complete')
        self.assertIn(['bash', 'scripts/update.sh'], fake.calls)
        self.assertEqual(fake.current, 'new')
    def test_failed_pull_leaves_app_alone(self):
        updater, fake = self.make(fail=True)
        updater.work('update')
        self.assertEqual(updater.snapshot()['phase'], 'failed')
        self.assertEqual(fake.current, 'old')
        self.assertFalse(any(c[0] == 'bash' for c in fake.calls))
    def test_busy_rejects_second_operation(self):
        updater, _ = self.make()
        updater.state['busy'] = True
        self.assertFalse(updater.start('update'))
    def test_private_http_requires_token_and_accepts_only_fixed_routes(self):
        server = ThreadingHTTPServer(('127.0.0.1', 0), module.Handler)
        server.token = 'fixture-token'
        server.updater, _ = self.make()
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            address = 'http://127.0.0.1:' + str(server.server_port)
            with self.assertRaises(HTTPError) as rejected: urlopen(address + '/status')
            self.assertEqual(rejected.exception.code, 401)
            request = Request(address + '/status', headers={'Authorization': 'Bearer fixture-token'})
            with urlopen(request) as response:
                self.assertEqual(response.status, 200)
                self.assertNotIn('fixture-token', response.read().decode())
            request = Request(address + '/shell', data=b'anything', headers={'Authorization': 'Bearer fixture-token'})
            with self.assertRaises(HTTPError) as missing: urlopen(request)
            self.assertEqual(missing.exception.code, 404)
        finally:
            server.shutdown(); server.server_close(); thread.join()

if __name__ == '__main__': unittest.main()
