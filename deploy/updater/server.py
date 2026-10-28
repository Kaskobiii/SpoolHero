"""Private fixed-command Docker updater, explicitly authorized by the LXC owner."""
import hmac
import json
import os
from pathlib import Path
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

class Updater:
    def __init__(self, directory, run=subprocess.run):
        self.directory, self.run = directory, run
        self.lock = threading.Lock()
        self.state = dict(supported=True, phase='idle', busy=False, available=False,
                          message='Check for a newer image.', currentVersion='', latestVersion='')

    def command(self, args):
        result = self.run(args, cwd=self.directory, capture_output=True, text=True, timeout=1800)
        if result.returncode:
            raise RuntimeError('Command failed. Check the updater container logs.')
        return result.stdout.strip()

    def snapshot(self):
        with self.lock: return dict(self.state)

    def start(self, action):
        with self.lock:
            if self.state['busy']: return False
            self.state.update(busy=True, phase='checking' if action == 'check' else 'updating',
                              message='Downloading the newest published image.' if action == 'check' else 'Backing up inventory and installing the image.')
        threading.Thread(target=self.work, args=(action,), daemon=True).start()
        return True

    def image(self):
        container = self.command(['docker', 'compose', 'ps', '-q', 'app'])
        current = self.command(['docker', 'inspect', '--format', '{{.Image}}', container])
        config = json.loads(self.command(['docker', 'compose', 'config', '--format', 'json']))
        return current, config['services']['app']['image']

    def revision(self, image):
        value = self.command(['docker', 'image', 'inspect', '--format', '{{index .Config.Labels "org.opencontainers.image.revision"}}', image])
        return value if value and value != '<no value>' else image

    def work(self, action):
        try:
            current, target = self.image()
            self.command(['docker', 'compose', 'pull', 'app'])
            latest = self.command(['docker', 'image', 'inspect', '--format', '{{.Id}}', target])
            versions = dict(currentVersion=self.revision(current), latestVersion=self.revision(latest))
            if latest == current:
                with self.lock:
                    self.state.update(**versions, phase='current', busy=False, available=False,
                                      message='Already running the newest published image. A GitHub build may still be running.')
                return
            if action == 'check':
                with self.lock:
                    self.state.update(**versions, phase='ready', busy=False, available=True,
                                      message='A newer image is ready to install.')
                return
            self.command(['bash', 'scripts/update.sh'])
            after, _ = self.image()
            if after != latest: raise RuntimeError('Running image does not match the downloaded image.')
            with self.lock:
                self.state.update(currentVersion=self.revision(after), latestVersion=self.revision(after),
                                  phase='complete', busy=False, available=False,
                                  message='Update complete. Inventory was backed up and the app is healthy. Reload this page.')
        except Exception as error:
            print(type(error).__name__ + ': ' + str(error), flush=True)
            with self.lock:
                self.state.update(phase='failed', busy=False, available=False,
                                  message='Update failed. Any completed backup is retained. Check docker compose logs updater and app.')

class Handler(BaseHTTPRequestHandler):
    def reply(self, status, data):
        payload = json.dumps(data).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def authorized(self):
        token = self.server.token
        return bool(token) and hmac.compare_digest(self.headers.get('Authorization', ''), 'Bearer ' + token)

    def do_GET(self):
        if not self.authorized(): return self.reply(401, {'message': 'Unauthorized'})
        if self.path != '/status': return self.reply(404, {'message': 'Not found'})
        self.reply(200, self.server.updater.snapshot())

    def do_POST(self):
        if not self.authorized(): return self.reply(401, {'message': 'Unauthorized'})
        if self.path not in ('/check', '/update'): return self.reply(404, {'message': 'Not found'})
        if not self.server.updater.start(self.path[1:]): return self.reply(409, {'message': 'Already running'})
        self.reply(202, self.server.updater.snapshot())

    def log_message(self, *_): pass

if __name__ == '__main__':
    directory = os.environ['SPOOLHERO_INSTALL_DIR']
    if not Path(directory, 'scripts/update.sh').is_file(): raise SystemExit('Missing installation directory')
    server = ThreadingHTTPServer(('0.0.0.0', 8091), Handler)
    server.token = os.environ['SPOOLHERO_UPDATER_TOKEN']
    if len(server.token) < 32: raise SystemExit('Updater token must be at least 32 characters')
    server.updater = Updater(directory)
    server.serve_forever()
