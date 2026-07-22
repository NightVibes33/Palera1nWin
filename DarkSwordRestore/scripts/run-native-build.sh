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

# Every source dependency is detached at a reviewed immutable commit. Branch tips
# must never change the contents of an already-reviewed release build.
old_builder = '''build_autotools() {
  local repository="$1"
  local ref="$2"
  local directory="$3"
  shift 3
  git clone --depth 1 --branch "$ref" "$repository" "$BUILD/$directory"
  pushd "$BUILD/$directory"
  # libfragmentzip derives its pkg-config version from the repository commit
  # count. A depth-1 clone reports version 1, while turdus requires >=54.
  if [[ "$directory" == "libfragmentzip" ]]; then
    git fetch --unshallow --tags
  fi
  ./autogen.sh --prefix=/mingw64 "$@"
  make
  make install
  popd
}'''
new_builder = '''build_autotools() {
  local repository="$1"
  local commit="$2"
  local directory="$3"
  shift 3
  git clone --no-tags "$repository" "$BUILD/$directory"
  git -C "$BUILD/$directory" checkout --detach "$commit"
  test "$(git -C "$BUILD/$directory" rev-parse HEAD)" = "$commit"
  pushd "$BUILD/$directory"
  ./autogen.sh --prefix=/mingw64 "$@"
  make
  make install
  popd
}'''
if old_builder not in text:
    raise SystemExit("Could not locate build_autotools for commit pinning")
text = text.replace(old_builder, new_builder, 1)
text = text.replace(
    'build_autotools "https://github.com/libimobiledevice/libtatsu.git" master libtatsu',
    'build_autotools "https://github.com/libimobiledevice/libtatsu.git" 60a39f36d719344360ec2e87563ed43f61f0530f libtatsu',
    1,
)
text = text.replace(
    'build_autotools "https://github.com/turdus-m3rula/libfragmentzip.git" non-libgeneral libfragmentzip',
    'build_autotools "https://github.com/turdus-m3rula/libfragmentzip.git" 84e47176fee2d856c81f87f2caaa7aca2df679ae libfragmentzip',
    1,
)
old_idevice = '''git clone --depth 1 --branch sephaxx \\
  https://github.com/turdus-m3rula/idevicerestore_fork.git \\
  "$BUILD/idevicerestore"'''
new_idevice = '''git clone --no-tags https://github.com/turdus-m3rula/idevicerestore_fork.git "$BUILD/idevicerestore"
git -C "$BUILD/idevicerestore" checkout --detach c2ad454aecc3354f3b1a15dcb4d4b4dc0e83b743
test "$(git -C "$BUILD/idevicerestore" rev-parse HEAD)" = c2ad454aecc3354f3b1a15dcb4d4b4dc0e83b743'''
if old_idevice not in text:
    raise SystemExit("Could not pin idevicerestore")
text = text.replace(old_idevice, new_idevice, 1)
text = text.replace(
    'git clone --depth 1 https://github.com/mineek/openra1n.git "$BUILD/openra1n"',
    'git clone --no-tags https://github.com/mineek/openra1n.git "$BUILD/openra1n"\n'
    'git -C "$BUILD/openra1n" checkout --detach 4595a5333e4134ade77b43fb2259e880b85801ee\n'
    'test "$(git -C "$BUILD/openra1n" rev-parse HEAD)" = 4595a5333e4134ade77b43fb2259e880b85801ee',
    1,
)

# Force the iPhoneOS/iPad resource variants.
for old, new in [
    ('("overlay_bin", ("overlay",), None),', '("overlay_bin", ("overlay_iphoneos", "overlay"), None),'),
    ('("union_bin", ("union",), None),', '("union_bin", ("union_iphoneos", "union"), None),'),
]:
    if old in text:
        text = text.replace(old, new, 1)
    if new not in text:
        raise SystemExit(f"Could not force resource variant: {new}")

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
PY_DER

'''
if marker not in text:
    raise SystemExit("Could not locate DER patch insertion")
text = text.replace(marker, compatibility_patch + marker, 1)

old_turdus_copy = 'cp src/idevicerestore.exe "$STAGE/turdus_merula.exe"'
new_turdus_copy = '''TURDUS_BINARY="$BUILD/idevicerestore/src/.libs/idevicerestore.exe"
if [[ ! -s "$TURDUS_BINARY" ]]; then
  echo "The real libtool turdus executable was not produced: $TURDUS_BINARY" >&2
  exit 7
fi
cp "$TURDUS_BINARY" "$STAGE/turdus_merula.exe"'''
if old_turdus_copy not in text:
    raise SystemExit("Could not locate turdus executable copy")
text = text.replace(old_turdus_copy, new_turdus_copy, 1)

old_openra1n = '''make LIBUSB=1
cp openra1n.exe "$STAGE/openra1n.exe"
popd
'''
new_openra1n = '''sed -i 's/^BIN = openra1n$/BIN = openra1n.exe/' Makefile
grep -q '^BIN = openra1n.exe$' Makefile
make LIBUSB=1
cp openra1n.exe "$STAGE/openra1n-core.exe"
popd

gcc -std=c11 -O2 -Wall -Wextra -municode \\
  "$ROOT/native/openra1n-wrapper/openra1n_wrapper.c" \\
  -o "$STAGE/openra1n.exe" \\
  -lsetupapi
'''
if old_openra1n not in text:
    raise SystemExit("Could not locate openra1n packaging block")
text = text.replace(old_openra1n, new_openra1n, 1)

identity_marker = '# Collect all transitive MinGW runtime DLLs until the set stabilizes.'
identity_patch = '''# Package exact-device identity tools.
for identity_tool in ideviceinfo.exe irecovery.exe; do
  identity_source="/mingw64/bin/$identity_tool"
  [[ -s "$identity_source" ]] || { echo "Missing identity tool: $identity_source" >&2; exit 8; }
  cp "$identity_source" "$STAGE/$identity_tool"
done

# Package the reviewed official palera1n v2.3 Linux x86_64 release asset.
PALERA1N_URL="https://github.com/palera1n/palera1n/releases/download/v2.3/palera1n-linux-x86_64"
PALERA1N_SHA256="037c2b398bc13bab277ae9abb841ae3c5c5bc89e22332bbcbcd8d04b68214292"
mkdir -p "$STAGE/dist"
curl --fail --location --retry 3 --retry-all-errors "$PALERA1N_URL" --output "$STAGE/dist/palera1n-linux-x86_64"
printf '%s  %s\n' "$PALERA1N_SHA256" "$STAGE/dist/palera1n-linux-x86_64" | sha256sum --check -
python - "$STAGE/dist/palera1n-linux-x86_64" <<'PY_ELF'
from pathlib import Path
import sys
path = Path(sys.argv[1])
data = path.read_bytes()
if len(data) < 65536 or not data.startswith(b"\\x7fELF"):
    raise SystemExit("Packaged palera1n runtime is not a valid ELF executable")
PY_ELF

'''
if identity_marker not in text:
    raise SystemExit("Could not locate runtime packaging insertion")
text = text.replace(identity_marker, identity_patch + identity_marker, 1)

old_filter = "awk '$3 ~ /^\\/mingw64\\/bin\\// { print $3 }'"
new_filter = "awk '$3 ~ /\\/mingw64\\/bin\\// { print $3 }'"
if old_filter not in text:
    raise SystemExit("Could not locate dependency filter")
text = text.replace(old_filter, new_filter, 1)

manifest_marker = 'echo "libfragmentzip-version=$fragmentzip_version"'
if manifest_marker not in text:
    raise SystemExit("Could not locate native manifest")
text = text.replace(
    manifest_marker,
    manifest_marker + '\n  echo "palera1n-runtime-tag=v2.3"\n  echo "palera1n-runtime-sha256=037c2b398bc13bab277ae9abb841ae3c5c5bc89e22332bbcbcd8d04b68214292"',
    1,
)
text = text.replace(
    'sha256sum "$STAGE"/*.exe "$STAGE"/resources/*.bin > "$STAGE/native-SHA256SUMS.txt"',
    'find "$STAGE" -type f ! -name native-SHA256SUMS.txt -print0 | sort -z | xargs -0 sha256sum > "$STAGE/native-SHA256SUMS.txt"',
    1,
)

path.write_text(text, encoding="utf-8", newline="\n")
PY

chmod +x "$GENERATED_SCRIPT"
exec bash "$GENERATED_SCRIPT"
