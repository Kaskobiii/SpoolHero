#!/usr/bin/env bash
# Verify an IP-address HTTPS handshake without SNI against real Caddy.
set -euo pipefail
fixture=$(mktemp -d)
name="spoolhero-tls-test-$$"
cleanup() { docker rm -f -v "$name" >/dev/null 2>&1 || true; rm -rf "$fixture"; }
trap cleanup EXIT
cat > "$fixture/Caddyfile" <<'CADDY'
{
    default_sni 127.0.0.1
}
https://127.0.0.1:5243 {
    tls internal
    respond "IP HTTPS works"
}
CADDY
docker run -d --name "$name" -p 127.0.0.1:5243:5243 -v "$fixture/Caddyfile:/etc/caddy/Caddyfile:ro" caddy:2-alpine
for attempt in {1..30}; do
    if docker cp "$name:/data/caddy/pki/authorities/local/root.crt" "$fixture/root.crt" >/dev/null 2>&1 &&
       curl --silent --show-error --fail --cacert "$fixture/root.crt" https://127.0.0.1:5243 > "$fixture/response"; then
        test "$(cat "$fixture/response")" = "IP HTTPS works"
        exit 0
    fi
    sleep 1
done
docker logs "$name"
exit 1
