#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
umask 077
if [[ ! -f .env ]]; then echo 'Run scripts/install.sh for a new installation first.' >&2; exit 1; fi
if ! grep -q '^SPOOLHERO_UPDATER_TOKEN=' .env; then
  printf '\nSPOOLHERO_UPDATER_TOKEN=%s\n' "$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')" >> .env
fi
if ! grep -q '^SPOOLHERO_INSTALL_DIR=' .env; then printf 'SPOOLHERO_INSTALL_DIR=%s\n' "$PWD" >> .env; fi
if [[ "${1:-}" == "--prepare" ]]; then exit 0; fi
docker compose --profile maintenance up -d --build --no-deps updater
bash scripts/update.sh
printf '\nFuture updates are under Settings > Updates in SpoolHero.\n'
