#!/usr/bin/env bash
set -euo pipefail

ROOT="${GITHUB_WORKSPACE:?}/DarkSwordRestore"
BUILD="$ROOT/build/native"
STAGE="$ROOT/build/native-stage"
DEPS="$ROOT/build/deps"
DEPS_EXTRACT="$ROOT/build/deps-extract"
LOG="$ROOT/build/native-build.log"
RESOURCE_URL="https://sep.lol/files/resources/resourcesV3/735c0d45a6ceb9f51c160f165c641c39ecbef4374fe0532daae1bdecd666389207918f2f839ada8b340e16a92fec2643/resource.tar.zst"
RESOURCE_SHA384="735c0d45a6ceb9f51c160f165c641c39ecbef4374fe0532daae1bdecd666389207918f2f839ada8b340e16a92fec2643"

mkdir -p "$BUILD" "$STAGE/resources" "$DEPS_EXTRACT"
exec > >(tee "$LOG") 2>&1
set -x

export PATH="/mingw64/bin:/usr/bin:$PATH"
export PKG_CONFIG_PATH="/mingw64/lib/pkgconfig:/mingw64/share/pkgconfig"

# Install the official libimobiledevice Windows artifacts downloaded by Actions.
find "$DEPS" -type f -name '*.tar' -print0 | while IFS= read -r -d '' archive; do
  tar -C "$DEPS_EXTRACT" -xf "$archive"
done
cp -rf "$DEPS_EXTRACT"/* / || true

# Build libfragmentzip, which is not distributed by MSYS2.
cd "$BUILD"
rm -rf libfragmentzip
git clone --depth 1 --branch non-libgeneral https://github.com/turdus-m3rula/libfragmentzip.git
cd libfragmentzip
./autogen.sh --prefix=/mingw64
make -j2
make install

# Clone the LGPL turdus idevicerestore fork.
cd "$BUILD"
rm -rf idevicerestore
git clone --depth 1 --branch sephaxx https://github.com/turdus-m3rula/idevicerestore_fork.git idevicerestore

# Download the official module payload archive linked by sep.lol.
RESOURCE_ARCHIVE="$BUILD/resource.tar.zst"
RESOURCE_UNPACK="$BUILD/resource-unpack"
rm -rf "$RESOURCE_UNPACK"
mkdir -p "$RESOURCE_UNPACK"
curl --fail --location --retry 3 --retry-all-errors "$RESOURCE_URL" --output "$RESOURCE_ARCHIVE"
printf '%s  %s\n' "$RESOURCE_SHA384" "$RESOURCE_ARCHIVE" | sha384sum --check -
tar --zstd -tf "$RESOURCE_ARCHIVE"
tar --zstd -xf "$RESOURCE_ARCHIVE" -C "$RESOURCE_UNPACK"

# Convert official raw modules into the C arrays expected by the source fork.
python - "$RESOURCE_UNPACK" "$BUILD/idevicerestore/src/stuff" "$STAGE/resources" <<'PY'
import pathlib
import re
import shutil
import sys

source_root = pathlib.Path(sys.argv[1])
stuff_root = pathlib.Path(sys.argv[2])
output_root = pathlib.Path(sys.argv[3])
stuff_root.mkdir(parents=True, exist_ok=True)
output_root.mkdir(parents=True, exist_ok=True)

specs = [
    ("Pongo_bin", ("pongo", "pongoos"), None),
    ("cpf_bin", ("cpf",), None),
    ("kpf_bin", ("kpf",), "kpf.bin"),
    ("sep_racer_bin", ("sep_racer", "sepracer"), "sep_racer.bin"),
    ("overlay_bin", ("overlay",), None),
    ("union_bin", ("union",), None),
]

all_files = [path for path in source_root.rglob("*") if path.is_file()]
print("Official resource archive files:")
for path in all_files:
    print("  ", path.relative_to(source_root))


def normalized(path: pathlib.Path) -> str:
    return re.sub(r"[^a-z0-9]+", "", path.name.lower())


def find_raw(aliases: tuple[str, ...]) -> pathlib.Path:
    alias_norms = [re.sub(r"[^a-z0-9]+", "", alias.lower()) for alias in aliases]
    candidates = []
    for path in all_files:
        if path.suffix.lower() in {".c", ".h", ".txt", ".md", ".json", ".plist"}:
            continue
        name = normalized(path)
        matches = [alias for alias in alias_norms if alias in name]
        if not matches:
            continue
        score = 0
        if path.suffix.lower() in {".bin", ".raw", ".img4", ".im4p"}:
            score += 100
        if any(name == alias or name == alias + "bin" for alias in alias_norms):
            score += 200
        score -= len(path.name)
        candidates.append((score, path))
    if not candidates:
        raise SystemExit(f"No official resource matched aliases: {aliases}")
    candidates.sort(key=lambda item: item[0], reverse=True)
    selected = candidates[0][1]
    print(f"Selected {selected} for {aliases}")
    return selected


def write_array(symbol: str, data: bytes) -> None:
    header = stuff_root / f"{symbol}.h"
    source = stuff_root / f"{symbol}.c"
    if not header.exists():
        header.write_text(
            f"#ifndef ___{symbol}_H\n#define ___{symbol}_H\n"
            f"extern unsigned char {symbol}[];\n"
            f"extern unsigned int {symbol}_len;\n#endif\n",
            encoding="utf-8",
        )
    with source.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write(f'#include "{symbol}.h"\n\nunsigned char {symbol}[] = {{\n')
        for offset in range(0, len(data), 16):
            chunk = data[offset:offset + 16]
            handle.write("    " + ", ".join(f"0x{value:02x}" for value in chunk) + ",\n")
        handle.write(f"}};\nunsigned int {symbol}_len = {len(data)};\n")
    print(f"Generated {source.name}: {len(data)} bytes")


for symbol, aliases, runtime_name in specs:
    existing_source = next((path for path in all_files if path.name.lower() == f"{symbol}.c".lower()), None)
    if existing_source is not None:
        shutil.copy2(existing_source, stuff_root / existing_source.name)
        matching_header = existing_source.with_suffix(".h")
        if matching_header.exists():
            shutil.copy2(matching_header, stuff_root / matching_header.name)
        print(f"Copied provided C array for {symbol}")
        continue

    raw = find_raw(aliases)
    data = raw.read_bytes()
    if not data:
        raise SystemExit(f"Official resource is empty: {raw}")
    write_array(symbol, data)
    if runtime_name:
        (output_root / runtime_name).write_bytes(data)
PY

# Build the turdus-enabled restore executable.
cd "$BUILD/idevicerestore"
PKG_CONFIG_PATH="$PKG_CONFIG_PATH" ./autogen.sh \
  --prefix=/mingw64 \
  --with-turdusmerula=yes \
  --without-libhfsplus \
  --without-limera1n
make -j2
cp src/idevicerestore.exe "$STAGE/turdus_merula.exe"

# Build openra1n's native Windows libusb backend.
cd "$BUILD"
rm -rf openra1n
git clone --depth 1 https://github.com/mineek/openra1n.git
cd openra1n
make LIBUSB=1
cp openra1n.exe "$STAGE/openra1n.exe"

# Build DarkSword's native Pongo resource/command bridge.
gcc -std=c11 -O2 -Wall -Wextra \
  $(pkg-config --cflags libusb-1.0) \
  "$ROOT/native/pongo-bridge/pongo_bridge.c" \
  -o "$STAGE/darksword-pongo.exe" \
  $(pkg-config --libs libusb-1.0)

# Collect transitive MinGW runtime DLLs until the set stabilizes.
for pass in 1 2 3 4; do
  for binary in "$STAGE"/*.exe "$STAGE"/*.dll; do
    test -f "$binary" || continue
    ldd "$binary" 2>/dev/null | awk '$3 ~ /^\/mingw64\/bin\// { print $3 }' | while read -r dependency; do
      cp -n "$dependency" "$STAGE/" || true
    done
  done
done

# Record exact source revisions and output hashes.
{
  echo "resource-sha384=$RESOURCE_SHA384"
  echo "idevicerestore=$(git -C "$BUILD/idevicerestore" rev-parse HEAD)"
  echo "openra1n=$(git -C "$BUILD/openra1n" rev-parse HEAD)"
  echo "libfragmentzip=$(git -C "$BUILD/libfragmentzip" rev-parse HEAD)"
} > "$STAGE/native-build-manifest.txt"

sha256sum "$STAGE"/*.exe "$STAGE"/resources/*.bin > "$STAGE/native-SHA256SUMS.txt"
find "$STAGE" -maxdepth 2 -type f -printf '%P\n' | sort
