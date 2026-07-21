#!/usr/bin/env bash
set -Eeuo pipefail

WORK_ROOT="${RUNNER_TEMP:-/tmp}/darksword-libirecovery"
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

# Build a Windows DLL that retains MinGW's normal Windows ABI/threading while
# routing only libirecovery's USB operations through libusb/libusbK. The stock
# Windows backend speaks to Apple's SetupAPI driver and cannot open the DFU and
# Pongo devices after DarkSword assigns libusbK.
git clone --depth 1 https://github.com/libimobiledevice/libirecovery.git "$WORK_ROOT/source"

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
if "IRECV_K_PONGO_MODE" not in header_text:
    header_text = header_text.replace(
        "IRECV_K_PORT_DFU_MODE     = 0xf014",
        "IRECV_K_PORT_DFU_MODE     = 0xf014,\n\tIRECV_K_PONGO_MODE       = 0x4141",
        1,
    )
if "IRECV_K_PONGO_MODE" not in header_text:
    raise SystemExit("Could not add the Pongo product identifier")
header.write_text(header_text, encoding="utf-8")

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
# enumeration and interface-selection paths.
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

# Catch vertically formatted predicates without altering already patched ones.
text = re.sub(
    r"(IRECV_K_PORT_DFU_MODE\s*\|\|\s*\n)(?!\s*[^\n]*IRECV_K_PONGO_MODE)",
    r"\1\t\t\t\tusb_descriptor.idProduct == IRECV_K_PONGO_MODE ||\n",
    text,
)

if "IRECV_FORCE_LIBUSB" not in text or "IRECV_K_PONGO_MODE" not in text:
    raise SystemExit("The libusb/Pongo source patch did not apply")
source.write_text(text, encoding="utf-8")
PY

pushd "$WORK_ROOT/source"
CPPFLAGS="${CPPFLAGS:-} -DIRECV_FORCE_LIBUSB" \
  ./autogen.sh \
    --prefix=/mingw64 \
    --without-tools \
    --without-udev
make -j2
make install
popd

# Confirm the installed public API and DLL are the DarkSword libusb build.
grep -q 'IRECV_K_PONGO_MODE' /mingw64/include/libirecovery.h
DLL_PATH="$(find /mingw64/bin -maxdepth 1 -type f -iname 'libirecovery-1.0*.dll' -print -quit)"
[[ -n "$DLL_PATH" && -s "$DLL_PATH" ]]
ldd "$DLL_PATH" | tee "$LOG_ROOT/libirecovery-libusb-dependencies.txt"
ldd "$DLL_PATH" | grep -qi 'libusb-1.0'

mkdir -p /mingw64/share/darksword
{
  echo "commit=$(git -C "$WORK_ROOT/source" rev-parse HEAD)"
  echo "dll=$DLL_PATH"
  echo "backend=libusb"
  echo "pongo-pid=0x4141"
} > /mingw64/share/darksword/libirecovery-libusb.txt

trap - ERR
echo "DarkSword libirecovery libusbK backend built successfully."
