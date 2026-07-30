#!/bin/sh
set -eu

envsubst < /usr/share/nginx/runtime-config.json.template > /usr/share/nginx/html/runtime-config.json
