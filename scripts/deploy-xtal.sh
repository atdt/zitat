#!/bin/sh
# Builds Zitat and deploys it to xtal, where Caddy serves it on the tailnet
# under /zitat/.
#
# xtal is the maintainer's own test host, not a generic deploy target;
# this script is specific to it and is not meant to be reused as-is
# for another host.
#
# xtal is not config-managed for this app; this script owns the app
# directory (/opt/zitat) plus /etc/systemd/system/zitat.service and
# /etc/caddy/tailnet.d/zitat, and prints a line whenever it changes either
# of those.
set -eu

repository=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
host=xtal
remote_tmp=$(ssh "$host" mktemp -d)
trap 'ssh "$host" rm -rf "$remote_tmp"' EXIT

tarball=$("$repository/scripts/publish-linux-arm64.sh")

echo "Copying build and unit files to $host..."
scp "$tarball" "$repository/deploy/zitat.service" "$repository/deploy/zitat.caddy" \
  "$host:$remote_tmp/"

ssh "$host" sh -s -- "$remote_tmp" <<'REMOTE'
set -eu
remote_tmp=$1
tarball="$remote_tmp/$(basename "$(ls "$remote_tmp"/zitat-linux-*.tar.gz)")"
unit="$remote_tmp/zitat.service"
route="$remote_tmp/zitat.caddy"

need_daemon_reload=0

if ! cmp -s "$unit" /etc/systemd/system/zitat.service 2>/dev/null; then
  echo "==> Installing /etc/systemd/system/zitat.service (outside /opt/zitat)"
  sudo install -m 0644 "$unit" /etc/systemd/system/zitat.service
  need_daemon_reload=1
fi

sudo mkdir -p /opt/zitat
sudo systemctl stop zitat.service 2>/dev/null || true
sudo tar -C /opt/zitat -xzf "$tarball"
sudo chown -R root:root /opt/zitat
sudo chmod -R a+rX /opt/zitat

if [ "$need_daemon_reload" -eq 1 ]; then
  sudo systemctl daemon-reload
fi

sudo systemctl enable --now zitat.service

if ! cmp -s "$route" /etc/caddy/tailnet.d/zitat 2>/dev/null; then
  echo "==> Installing /etc/caddy/tailnet.d/zitat (outside /opt/zitat)"
  sudo install -D -m 0644 "$route" /etc/caddy/tailnet.d/zitat
  sudo systemctl reload caddy
fi

sudo systemctl --no-pager --full status zitat.service
REMOTE
