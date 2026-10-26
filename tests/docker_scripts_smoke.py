"""Exercise update failure paths with a fake Docker CLI; never touches a daemon."""
import gzip
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
FAKE = '''#!/usr/bin/env python3
import json, os, sys
args=sys.argv[1:]
with open(os.environ['CALLS'], 'a') as f: f.write(json.dumps(args)+'\\n')
scenario=os.environ.get('SCENARIO','ok')
if args[:3]==['compose','ps','-q']: print('container')
if args[:3]==['compose','pull','app'] and scenario=='pull-fail': sys.exit(1)
if args[:3]==['compose','images','-q']: print('sha256:previous-image')
if args[:3]==['compose','config','--images']: print('ghcr.io/kaskobiii/spoolhero:latest')
if args[:3]==['compose','exec','-T']:
    if scenario=='backup-fail': print('partial'); sys.exit(1)
    print('-- complete SQL fixture')
if args[:3]==['compose','up','-d'] and scenario=='health-fail': sys.exit(1)
'''

class UpdateTests(unittest.TestCase):
    def run_script(self, scenario, script='update.sh'):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        (root/'scripts').mkdir()
        shutil.copy(ROOT/'scripts'/script, root/'scripts'/script)
        shutil.copy(ROOT/'docker-compose.yml', root/'docker-compose.yml')
        if script == 'update.sh': (root/'.env').write_text('MYSQL_ROOT_PASSWORD=fixture-only\n')
        (root/'bin').mkdir()
        fake = root/'bin/docker'; fake.write_text(FAKE); fake.chmod(0o700)
        calls = root/'calls.jsonl'
        result = subprocess.run(['bash', str(root/'scripts'/script)], env={
            **os.environ, 'PATH': str(root/'bin')+os.pathsep+os.environ['PATH'],
            'CALLS': str(calls), 'SCENARIO': scenario}, capture_output=True, text=True)
        import json
        log = [json.loads(line) for line in calls.read_text().splitlines()]
        return root, result, log

    def test_success_retains_database_and_creates_valid_backup(self):
        root, result, calls = self.run_script('ok')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('Update complete', result.stdout)
        self.assertIn('complete SQL fixture', gzip.decompress(next(root.glob('backups/*/database.sql.gz')).read_bytes()).decode())
        self.assertIn(['compose', 'stop', 'app'], calls)
        up = next(c for c in calls if c[:2] == ['compose', 'up'])
        self.assertIn('--no-deps', up); self.assertEqual(up[-1], 'app')
        self.assertNotIn(['compose', 'down'], calls)
        self.assertEqual(next(root.glob('backups/*/.env')).stat().st_mode & 0o777, 0o600)

    def test_failed_download_does_not_stop_app(self):
        _, result, calls = self.run_script('pull-fail')
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any(c[:2] == ['compose', 'stop'] for c in calls))

    def test_failed_backup_restarts_existing_app_without_replacement(self):
        _, result, calls = self.run_script('backup-fail')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(['compose', 'start', 'app'], calls)
        self.assertFalse(any(c[:2] == ['compose', 'up'] for c in calls))

    def test_failed_new_app_does_not_claim_success_or_blindly_rollback(self):
        root, result, calls = self.run_script('health-fail')
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn('Update complete', result.stdout)
        self.assertTrue(list(root.glob('backups/*/database.sql.gz')))
        self.assertNotIn(['compose', 'start', 'app'], calls)

    def test_install_generates_private_password(self):
        root, result, _ = self.run_script('ok', 'install.sh')
        self.assertEqual(result.returncode, 0, result.stderr)
        env = (root/'.env').read_text()
        self.assertRegex(env, r'MYSQL_ROOT_PASSWORD=[a-f0-9]{64}\n')
        self.assertEqual((root/'.env').stat().st_mode & 0o777, 0o600)

if __name__ == '__main__': unittest.main()
