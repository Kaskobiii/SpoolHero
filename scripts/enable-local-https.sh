#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
ip=${1:-}
if [[ ! "$ip" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]]; then
  echo 'Usage: bash scripts/enable-local-https.sh 192.168.0.100' >&2; exit 1
fi
IFS=. read -r a b c d <<< "$ip"
for octet in "$a" "$b" "$c" "$d"; do
  if (( 10#$octet > 255 )); then echo 'Invalid IPv4 address.' >&2; exit 1; fi
done
mkdir -p local-https/public
cat > local-https/Caddyfile <<CADDY
{
    default_sni $ip
}
https://$ip {
    tls internal
    reverse_proxy app:5000
}
http://$ip:8081 {
    @certificate path /spoolhero-root.crt
    handle @certificate {
        rewrite * /root.crt
        root * /srv
        file_server
    }
    handle {
        respond "Not found" 404
    }
}
CADDY
compose=(docker compose -f docker-compose.yml -f deploy/docker-compose.https.yml)
"${compose[@]}" up -d --no-deps caddy
# Apply a changed LAN address even when the mounted file is the only change.
"${compose[@]}" restart caddy
for attempt in {1..30}; do
  if "${compose[@]}" exec -T caddy test -s /data/caddy/pki/authorities/local/root.crt; then
    "${compose[@]}" exec -T caddy cat /data/caddy/pki/authorities/local/root.crt > local-https/public/root.crt
    printf '\nHTTPS ready: https://%s\nOn each Android phone download http://%s:8081/spoolhero-root.crt\nInstall it as a CA certificate in Android Settings, then open the HTTPS address in Chrome.\n' "$ip" "$ip"
    exit 0
  fi
  sleep 1
done
echo 'Certificate setup did not finish. Check docker compose -f docker-compose.yml -f deploy/docker-compose.https.yml logs caddy.' >&2
exit 1
