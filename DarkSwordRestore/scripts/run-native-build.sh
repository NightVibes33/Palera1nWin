#!/usr/bin/env bash
set -Eeuo pipefail

WORKSPACE_UNIX="$(cygpath -u "${GITHUB_WORKSPACE:?}")"
ROOT="$WORKSPACE_UNIX/DarkSwordRestore"
SOURCE_SCRIPT="$ROOT/scripts/build-native-final.sh"
GENERATED_SCRIPT="$ROOT/build/build-native-generated.sh"
mkdir -p "$(dirname "$GENERATED_SCRIPT")"
cp "$SOURCE_SCRIPT" "$GENERATED_SCRIPT"

python - "$GENERATED_SCRIPT" <<'PY'
from __future__ import annotations

import pathlib
import sys

path = pathlib.Path(sys.argv[1])
text = path.read_text(encoding="utf-8")

# Force the iPhoneOS/iPad resource variants. The official archive contains
# both iphoneos and tvOS overlays; generic alias scoring can otherwise select
# the tvOS payload simply because of archive ordering.
old_overlay = '("overlay_bin", ("overlay",), None),'
new_overlay = '("overlay_bin", ("overlay_iphoneos", "overlay"), None),'
old_union = '("union_bin", ("union",), None),'
new_union = '("union_bin", ("union_iphoneos", "union"), None),'
if old_overlay in text:
    text = text.replace(old_overlay, new_overlay, 1)
if old_union in text:
    text = text.replace(old_union, new_union, 1)
if new_overlay not in text or new_union not in text:
    raise SystemExit("Could not force the iPhoneOS overlay/union resources")

# turdus' bundled libDER uses Apple's __unused spelling. MinGW does not define
# it, so add the conventional GCC attribute to that one source file after the
# fork is cloned and before configure/make begins.
marker = '# Download the official module archive linked by sep.lol and verify its hash.'
compatibility_patch = r'''# MinGW compatibility for the bundled Apple DER source.
python - "$BUILD/idevicerestore/src/libDER/oids.c" <<'PY_DER'
from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding="utf-8")
prefix = "#ifndef __unused\n#define __unused __attribute__((unused))\n#endif\n\n"
if "__unused" not in source:
    raise SystemExit("Expected __unused declarations were not found in libDER/oids.c")
if "#define __unused" not in source:
    source = prefix + source
path.write_text(source, encoding="utf-8", newline="\n")
print("Patched libDER __unused compatibility for MinGW")
PY_DER

'''
if "Patched libDER __unused compatibility for MinGW" not in text:
    if marker not in text:
        raise SystemExit("Could not locate the turdus resource-build insertion point")
    text = text.replace(marker, compatibility_patch + marker, 1)

# Libtool creates src/idevicerestore.exe as a launcher that expects the real
# program in src/.libs. Copying only that launcher produces exit code 127 in
# the portable ZIP. Stage the actual PE restore engine instead.
old_turdus_copy = 'cp src/idevicerestore.exe "$STAGE/turdus_merula.exe"'
new_turdus_copy = '''TURDUS_BINARY="$BUILD/idevicerestore/src/.libs/idevicerestore.exe"
if [[ ! -s "$TURDUS_BINARY" ]]; then
  echo "The real libtool turdus executable was not produced: $TURDUS_BINARY" >&2
  exit 7
fi
cp "$TURDUS_BINARY" "$STAGE/turdus_merula.exe"'''
if old_turdus_copy not in text:
    raise SystemExit("Could not locate the turdus executable copy step")
text = text.replace(old_turdus_copy, new_turdus_copy, 1)

# Preserve the original Windows openra1n binary as the core checkm8/PongoOS
# engine. Its upstream Makefile names the output "openra1n" even though MinGW
# creates "openra1n.exe", causing the strip step to fail. Correct BIN before
# compiling, then build the user-facing driver-readiness wrapper.
old_openra1n = '''make LIBUSB=1
cp openra1n.exe "$STAGE/openra1n.exe"
popd
'''
new_openra1n = '''sed -i 's/^BIN = openra1n$/BIN = openra1n.exe/' Makefile
grep -q '^BIN = openra1n.exe$' Makefile
make LIBUSB=1
cp openra1n.exe "$STAGE/openra1n-core.exe"
popd

gcc -std=c11 -O2 -Wall -Wextra -municode \
  "$ROOT/native/openra1n-wrapper/openra1n_wrapper.c" \
  -o "$STAGE/openra1n.exe"
'''
if old_openra1n not in text:
    raise SystemExit("Could not locate the openra1n packaging block")
text = text.replace(old_openra1n, new_openra1n, 1)

# Package the native normal-mode and recovery/DFU identity tools used by the
# exact-device IPSW downloader. Their dependencies are collected by the same
# recursive ldd pass as the restore binaries.
identity_marker = '# Collect all transitive MinGW runtime DLLs until the set stabilizes.'
identity_patch = '''# Package exact-device identity tools.
for identity_tool in ideviceinfo.exe irecovery.exe; do
  identity_source="/mingw64/bin/$identity_tool"
  if [[ ! -s "$identity_source" ]]; then
    echo "Missing required identity tool: $identity_source" >&2
    exit 8
  fi
  cp "$identity_source" "$STAGE/$identity_tool"
done

'''
if identity_marker not in text:
    raise SystemExit("Could not locate the native runtime dependency collection block")
if "Package exact-device identity tools" not in text:
    text = text.replace(identity_marker, identity_patch + identity_marker, 1)

# GitHub's MSYS2 root can be rendered as either /mingw64/bin or as a full
# runner path ending in /mingw64/bin. Match the segment anywhere in the ldd
# path so every transitive runtime DLL is copied into the portable package.
old_dependency_filter = "awk '$3 ~ /^\\/mingw64\\/bin\\// { print $3 }'"
new_dependency_filter = "awk '$3 ~ /\\/mingw64\\/bin\\// { print $3 }'"
if old_dependency_filter not in text:
    raise SystemExit("Could not locate the MinGW dependency filter")
text = text.replace(old_dependency_filter, new_dependency_filter, 1)

# The custom turdus libirecovery fork is intentionally linked statically into
# turdus_merula.exe. A separate libirecovery DLL is therefore not a portable
# runtime requirement; successful linking proves the Pongo APIs are present.
path.write_text(text, encoding="utf-8", newline="\n")
PY

chmod +x "$GENERATED_SCRIPT"
exec bash "$GENERATED_SCRIPT"
