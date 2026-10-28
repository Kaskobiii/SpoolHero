#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
umask 077
command -v docker >/dev/null
docker compose version >/dev/null
if [[ ! -f .env ]]; then
  password=$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')
  printf 'MYSQL_ROOT_PASSWORD=%s\nSPOOLHERO_PORT=5000\nSPOOLHERO_IMAGE=ghcr.io/kaskobiii/spoolhero:latest\n' "$password" > .env
fi
bash scripts/enable-updater.sh --prepare
docker compose config --quiet
docker compose --profile maintenance up -d --build --no-deps updater
docker compose pull
docker compose up -d --no-build --wait --wait-timeout 240
printf '\nSpoolHero is ready at http://<this-machine-LAN-IP>:<SPOOLHERO_PORT from .env> (default 5000).\nImport your Export all file under Settings > Backup & transfer.\n'
