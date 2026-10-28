#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
umask 077
docker compose config --quiet
if [[ -z $(docker compose ps -q --status running app) || -z $(docker compose ps -q --status running db) ]]; then
  echo 'Start the existing installation before updating. For a new installation use bash scripts/install.sh.' >&2
  exit 1
fi
# Download first: registry problems must not interrupt the current app.
docker compose pull app
mkdir -p backups
backup="backups/$(date -u +%Y%m%dT%H%M%SZ)-$$"
mkdir "$backup"
cp .env docker-compose.yml "$backup/"
docker compose images -q app > "$backup/previous-image.txt"
docker compose config --images > "$backup/configured-images.txt"
# Stop writes (including printer reports) while taking a consistent snapshot.
docker compose stop app
resume_old_app() { docker compose start app >/dev/null || true; }
trap resume_old_app EXIT
if ! docker compose exec -T db sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysqldump -uroot --single-transaction --routines --events --no-tablespaces --hex-blob SpoolManager' | gzip > "$backup/database.sql.gz"; then
  echo 'Backup failed; keeping the current app. No update was applied.' >&2
  exit 1
fi
gzip -t "$backup/database.sql.gz"
# From this point a new version may migrate the schema: never blindly roll back.
trap - EXIT
if ! docker compose up -d --no-deps --no-build --pull never --wait --wait-timeout 240 app; then
  printf 'Update did not become healthy. Backup: %s\nCheck docker compose logs app; see docs/DOCKER.md for recovery.\n' "$backup" >&2
  exit 1
fi
before_image=$(cat "$backup/previous-image.txt")
after_image=$(docker compose images -q app)
if [[ "$before_image" == "$after_image" ]]; then
  printf '\nNo newer image was published. The running image is unchanged. A GitHub build may still be running.\n'
else
  printf '\nUpdate complete. Inventory and printer configuration retained.\n'
fi
printf 'Backup saved to %s\n' "$backup"
