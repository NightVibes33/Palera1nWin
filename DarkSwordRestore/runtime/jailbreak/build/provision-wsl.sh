#!/usr/bin/env bash
set -Eeuo pipefail

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo 'provision-wsl.sh must run as root inside WSL.' >&2
  exit 2
fi

REPO=${1:-}
if [[ -z "$REPO" || ! -d "$REPO" ]]; then
  echo 'Usage: provision-wsl.sh <mounted-toolchain-root>' >&2
  exit 2
fi

BINARY="$REPO/dist/palera1n-linux-x86_64"
SHIM="$REPO/build/fake-checkra1n.sh"
if [[ ! -s "$BINARY" ]]; then
  echo "Missing packaged palera1n runtime: $BINARY" >&2
  exit 3
fi
if [[ ! -s "$SHIM" ]]; then
  echo "Missing packaged checkra1n compatibility shim: $SHIM" >&2
  exit 3
fi

export DEBIAN_FRONTEND=noninteractive
if command -v apt-get >/dev/null 2>&1; then
  apt-get update -o Acquire::Retries=3
  apt-get install -y --no-install-recommends \
    ca-certificates usbutils usbip usbmuxd libusb-1.0-0 libimobiledevice6
else
  echo 'This provisioner currently requires a Debian/Ubuntu WSL distribution with apt-get.' >&2
  exit 4
fi

modprobe vhci-hcd 2>/dev/null || true
install -d -m755 /opt/palera1n
install -m755 "$BINARY" /opt/palera1n/palera1n.new
install -m755 "$SHIM" /opt/palera1n/checkra1n
if [[ -e /opt/palera1n/palera1n ]]; then
  cp -a /opt/palera1n/palera1n /opt/palera1n/palera1n.previous
fi
mv -f /opt/palera1n/palera1n.new /opt/palera1n/palera1n
ln -sfn /opt/palera1n/palera1n /usr/local/bin/palera1n
ln -sfn /opt/palera1n/checkra1n /usr/local/bin/checkra1n

cat >/opt/palera1n/pln-run.sh <<'RUNNER'
#!/usr/bin/env bash
set -Eeuo pipefail

mapfile -t apple_rows < <(lsusb 2>/dev/null | grep -i ' ID 05ac:' || true)
if (( ${#apple_rows[@]} != 1 )); then
  echo "[Palera1nWin] Exactly one Apple USB device must be visible in WSL; found ${#apple_rows[@]}." >&2
  exit 65
fi

export PATH="/opt/palera1n:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
export PALERA1N_CHECKRA1N="/opt/palera1n/checkra1n"
exec /opt/palera1n/palera1n "$@"
RUNNER
chmod 755 /opt/palera1n/pln-run.sh

if ! timeout 15s /opt/palera1n/palera1n --version >/tmp/palera1nwin-version.txt 2>&1; then
  cat /tmp/palera1nwin-version.txt >&2 || true
  if [[ -x /opt/palera1n/palera1n.previous ]]; then
    mv -f /opt/palera1n/palera1n.previous /opt/palera1n/palera1n
  fi
  echo 'The packaged palera1n binary did not pass its version self-check.' >&2
  exit 5
fi
cat /tmp/palera1nwin-version.txt
rm -f /tmp/palera1nwin-version.txt

echo '[Palera1nWin] WSL runtime provisioned in /opt/palera1n.'
