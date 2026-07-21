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

path.write_text(text, encoding="utf-8", newline="\n")
PY

chmod +x "$GENERATED_SCRIPT"
exec bash "$GENERATED_SCRIPT"
