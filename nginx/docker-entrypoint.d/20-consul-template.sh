#!/bin/sh
set -e

# Render initial config — nginx hasn't started yet, so no reload
consul-template \
  -consul-addr="${CONSUL_HTTP_ADDR:-http://consul:8500}" \
  -template="/etc/consul-template/nginx.ctmpl:/etc/nginx/conf.d/default.conf" \
  -once \
  -log-level=info

# Watch for changes and reload nginx
consul-template \
  -consul-addr="${CONSUL_HTTP_ADDR:-http://consul:8500}" \
  -template="/etc/consul-template/nginx.ctmpl:/etc/nginx/conf.d/default.conf:nginx -s reload" \
  -log-level=info \
  -wait=5s &
