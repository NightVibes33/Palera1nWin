#!/usr/bin/env bash
set -Eeuo pipefail

if [[ -n "${RUNNER_TEMP:-}" ]]; then
  RUNNER_TEMP_UNIX="$(cygpath -u "$RUNNER_TEMP")"
else
  RUNNER_TEMP_UNIX="/tmp"
fi
WORK_ROOT="$RUNNER_TEMP_UNIX/darksword-libirecovery"
LOG_ROOT="$(cygpath -u "${GITHUB_WORKSPACE:?}")/DarkSwordRestore/build"
LOG_PATH="$LOG_ROOT/libirecovery-libusb-build.log"

rm -rf "$WORK_ROOT"
mkdir -p "$WORK_ROOT" "$LOG_ROOT"
: > "$LOG_PATH"
exec > >(tee -a "$LOG_PATH") 2>&1

on_error() {
  local status=$?
  echo "DarkSword libirecovery build failed with exit code $status"
  tail -n 160 "$LOG_PATH" || true
  exit "$status"
}
trap on_error ERR
set -x

export PATH="/mingw64/bin:/usr/bin:$PATH"
export PKG_CONFIG_PATH="/mingw64/lib/pkgconfig:/mingw64/share/pkgconfig"

# Build the turdus-compatible libirecovery API while retaining MinGW's normal
# Windows ABI/threading and routing USB operations through libusb/libusbK. This
# fork provides the Pongo upload/control helpers consumed by turdus merula.
git clone --depth 1 --branch sephaxx \
  https://github.com/turdus-m3rula/libirecovery.git \
  "$WORK_ROOT/source"

python - "$WORK_ROOT/source" <<'PY'
from __future__ import annotations

import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1])
configure = root / "configure.ac"
source = root / "src/libirecovery.c"
header = root / "include/libirecovery.h"

configure_text = configure.read_text(encoding="utf-8")
needle = 'AS_IF([test "x$win32" = "xtrue"], ['
if needle not in configure_text:
    raise SystemExit("Could not locate libirecovery Windows backend selection")
configure_text = configure_text.replace(
    needle,
    'AS_IF([test "x$win32" = "xdisabled-for-darksword"], [',
    1,
)
configure.write_text(configure_text, encoding="utf-8")

header_text = header.read_text(encoding="utf-8")
required_api = (
    "IRECV_K_PONGO_MODE",
    "irecv_send_pongo",
    "irecv_usb_control_transfer_no_timeout_retval",
    "irecv_pongo_send_buffer",
)
missing = [symbol for symbol in required_api if symbol not in header_text]
if missing:
    raise SystemExit(f"The turdus libirecovery API is incomplete: {missing}")

text = source.read_text(encoding="utf-8")
replacements = {
    "#ifndef _WIN32": "#if !defined(_WIN32) || defined(IRECV_FORCE_LIBUSB)",
    "#ifdef _WIN32": "#if defined(_WIN32) && !defined(IRECV_FORCE_LIBUSB)",
    "#ifndef WIN32": "#if !defined(WIN32) || defined(IRECV_FORCE_LIBUSB)",
    "#ifdef WIN32": "#if defined(WIN32) && !defined(IRECV_FORCE_LIBUSB)",
}
for old, new in replacements.items():
    count = text.count(old)
    if count:
        print(f"Replacing {count} occurrences of {old}")
        text = text.replace(old, new)

# Include PongoOS in every known-mode list or predicate used by the libusb
# enumeration and interface-selection paths. The turdus fork already carries
# most of these checks; the replacements are intentionally idempotent.
text = text.replace(
    "IRECV_K_PORT_DFU_MODE, KIS_PRODUCT_ID",
    "IRECV_K_PORT_DFU_MODE, IRECV_K_PONGO_MODE, KIS_PRODUCT_ID",
)
text = text.replace(
    "usb_descriptor.idProduct == IRECV_K_PORT_DFU_MODE ||\n\t\t\t\tusb_descriptor.idProduct == KIS_PRODUCT_ID",
    "usb_descriptor.idProduct == IRECV_K_PORT_DFU_MODE ||\n\t\t\t\tusb_descriptor.idProduct == IRECV_K_PONGO_MODE ||\n\t\t\t\tusb_descriptor.idProduct == KIS_PRODUCT_ID",
)
text = text.replace(
    "client->mode == IRECV_K_PORT_DFU_MODE || client->mode == IRECV_K_WTF_MODE",
    "client->mode == IRECV_K_PORT_DFU_MODE || client->mode == IRECV_K_PONGO_MODE || client->mode == IRECV_K_WTF_MODE",
)
text = re.sub(
    r"(IRECV_K_PORT_DFU_MODE\s*\|\|\s*\n)(?!\s*[^\n]*IRECV_K_PONGO_MODE)",
    r"\1\t\t\t\tusb_descriptor.idProduct == IRECV_K_PONGO_MODE ||\n",
    text,
)

# MinGW does not expose the legacy BSD bzero symbol. The async transfer code
# only needs a zero-initialized structure, so use the portable C equivalent.
text = text.replace(
    "bzero(&transfer, sizeof(struct irecv_async_transfer));",
    "memset(&transfer, 0, sizeof(struct irecv_async_transfer));",
)

if "IRECV_FORCE_LIBUSB" not in text or "IRECV_K_PONGO_MODE" not in text:
    raise SystemExit("The libusb/Pongo source patch did not apply")
if "bzero(&transfer" in text:
    raise SystemExit("The MinGW bzero compatibility patch did not apply")
for symbol in required_api[1:]:
    if symbol not in text:
        raise SystemExit(f"Missing turdus Pongo implementation: {symbol}")
source.write_text(text, encoding="utf-8")
PY

pushd "$WORK_ROOT/source"
CPPFLAGS="${CPPFLAGS:-} -DIRECV_FORCE_LIBUSB" \
  ./autogen.sh \
    --prefix=/mingw64 \
    --with-tools \
    --without-udev
make -j2
make install
popd

# Confirm the installed public API and DLL are the DarkSword libusb build.
for symbol in \
  IRECV_K_PONGO_MODE \
  irecv_send_pongo \
  irecv_usb_control_transfer_no_timeout_retval \
  irecv_pongo_send_buffer; do
  grep -q "$symbol" /mingw64/include/libirecovery.h
done
DLL_PATH="$(find /mingw64/bin -maxdepth 1 -type f -iname 'libirecovery-1.0*.dll' -print -quit)"
[[ -n "$DLL_PATH" && -s "$DLL_PATH" ]]
ldd "$DLL_PATH" | tee "$LOG_ROOT/libirecovery-libusb-dependencies.txt"
ldd "$DLL_PATH" | grep -qi 'libusb-1.0'

# Export a tiny compatibility header into subsequent GitHub Actions steps.
# Turdus' embedded Apple DER sources use __unused, which MinGW does not define.
COMPAT_HEADER="/mingw64/include/darksword-mingw-compat.h"
cat > "$COMPAT_HEADER" <<'EOF'
#ifndef DARKSWORD_MINGW_COMPAT_H
#define DARKSWORD_MINGW_COMPAT_H
#ifndef __unused
#define __unused __attribute__((unused))
#endif
#endif
EOF
if [[ -n "${GITHUB_ENV:-}" ]]; then
  GITHUB_ENV_UNIX="$(cygpath -u "$GITHUB_ENV")"
  printf 'CPPFLAGS=-include /mingw64/include/darksword-mingw-compat.h\n' >> "$GITHUB_ENV_UNIX"
fi

mkdir -p /mingw64/share/darksword
{
  echo "commit=$(git -C "$WORK_ROOT/source" rev-parse HEAD)"
  echo "repository=turdus-m3rula/libirecovery"
  echo "branch=sephaxx"
  echo "dll=$DLL_PATH"
  echo "backend=libusb"
  echo "pongo-pid=0x4141"
  echo "compat-header=$COMPAT_HEADER"
} > /mingw64/share/darksword/libirecovery-libusb.txt

trap - ERR
echo "DarkSword turdus libirecovery libusbK backend built successfully."
