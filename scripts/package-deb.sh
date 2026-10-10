#!/bin/sh
# Builds a Debian package of Zitat for the given Debian architecture (arm64
# or amd64) and prints its path.
set -eu

repository=$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)

case "${1-}" in
  arm64) rid=linux-arm64 ;;
  amd64) rid=linux-x64 ;;
  *)
    echo "usage: $0 arm64|amd64" >&2
    exit 2
    ;;
esac

"$repository/scripts/publish-$rid.sh" >/dev/null

# Put the UTC build timestamp before the commit hash so later timestamps
# produce higher package versions, including rebuilds of the same commit.
version="0+$(date -u +%Y%m%d%H%M%S).g$(git -C "$repository" rev-parse --short HEAD)"
if [ -n "$(git -C "$repository" status --porcelain)" ]; then
  version="$version.dirty"
fi

staged="$repository/artifacts/deb-root/usr/lib/zitat"
rm -rf "$repository/artifacts/deb-root"
mkdir -p "$(dirname "$staged")"
cp -R "$repository/artifacts/$rid" "$staged"

docs="$repository/artifacts/deb-root/usr/share/doc/zitat"
mkdir -p "$docs"
cp "$repository/deploy/debian/copyright" "$docs/copyright"
gzip -n -9 -c "$repository/deploy/debian/third-party-notices" \
  >"$docs/third-party-notices.gz"
{
  printf 'zitat (%s) unstable; urgency=medium\n\n' "$version"
  printf '  * Build from Git commit %s.\n\n' "$(git -C "$repository" rev-parse HEAD)"
  printf ' -- Ori Livneh <ori.livneh@gmail.com>  %s\n' \
    "$(LC_ALL=C date -u '+%a, %d %b %Y %H:%M:%S +0000')"
} | gzip -n -9 >"$docs/changelog.gz"

package="$repository/artifacts/zitat_${version}_$1.deb"
(
  cd "$repository"
  ZITAT_DEB_ARCH=$1 ZITAT_VERSION=$version \
    nfpm package --config deploy/nfpm.yaml --packager deb --target "$package" >&2
)
printf '%s\n' "$package"
