#!/bin/sh
# Builds Zitat and deploys it to calx, listening on the tailnet interface.
#
# calx is the maintainer's own test host, not a generic deploy target;
# this script is specific to it and is not meant to be reused as-is
# for another host.
#
# calx is not config-managed for this app; this script owns the app
# directory (/opt/zitat) plus two host-level files it installs the first
# time it runs: /etc/systemd/system/zitat.service and /etc/zitat.env. Any
# time it changes either of those, it prints what changed.
set -eu

repository=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
host=calx
remote_tmp=$(ssh "$host" mktemp -d)
trap 'ssh "$host" rm -rf "$remote_tmp"' EXIT

tarball=$("$repository/scripts/publish-linux-x64.sh")

echo "Copying build and unit files to $host..."
scp "$tarball" "$repository/deploy/zitat.service" "$repository/deploy/zitat.env.example" \
  "$host:$remote_tmp/"

ssh "$host" sh -s -- "$remote_tmp" <<'REMOTE'
set -eu
remote_tmp=$1
tarball="$remote_tmp/$(basename "$(ls "$remote_tmp"/zitat-linux-*.tar.gz)")"
unit="$remote_tmp/zitat.service"
env_example="$remote_tmp/zitat.env.example"

need_daemon_reload=0

if ! cmp -s "$unit" /etc/systemd/system/zitat.service 2>/dev/null; then
  echo "==> Installing /etc/systemd/system/zitat.service (outside /opt/zitat)"
  sudo install -m 0644 "$unit" /etc/systemd/system/zitat.service
  need_daemon_reload=1
fi

if [ ! -e /etc/zitat.env ]; then
  echo "==> Creating /etc/zitat.env (outside /opt/zitat)"
  tailnet_ip=$(tailscale ip -4)
  sudo sh -c "sed 's#http://127.0.0.1:8080#http://$tailnet_ip:8080#' '$env_example' > /etc/zitat.env"
  sudo chmod 0644 /etc/zitat.env
  echo "    Bound to tailnet address $tailnet_ip:8080. If this host's"
  echo "    tailnet IP ever changes, update /etc/zitat.env by hand."
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
sudo systemctl --no-pager --full status zitat.service
REMOTE
