#!/usr/bin/env bash
set -euo pipefail

WORKSPACE_UNIX="$(cygpath -u "${GITHUB_WORKSPACE:?}")"
ROOT="$WORKSPACE_UNIX/DarkSwordRestore"
REPO_ROOT="$WORKSPACE_UNIX"
BUILD="$ROOT/build/native"
STAGE="$ROOT/build/native-stage"
LOG="$ROOT/build/native-build.log"
RESOURCE_URL="https://sep.lol/files/resources/resourcesV3/735c0d45a6ceb9f51c160f165c641c39ecbef4374fe0532daae1bdecd666389207918f2f839ada8b340e16a92fec2643/resource.tar.zst"
RESOURCE_SHA384="735c0d45a6ceb9f51c160f165c641c39ecbef4374fe0532daae1bdecd666389207918f2f839ada8b340e16a92fec2643"

rm -rf "$BUILD" "$STAGE"
mkdir -p "$BUILD" "$STAGE/resources" "$(dirname "$LOG")"
exec > >(tee "$LOG") 2>&1
set -x

export PATH="/mingw64/bin:/usr/bin:$PATH"
export PKG_CONFIG_PATH="/mingw64/lib/pkgconfig:/mingw64/share/pkgconfig"
export MAKEFLAGS="-j2"

build_autotools() {
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
}

# These two dependencies are not available as complete MSYS2 packages.
build_autotools "https://github.com/libimobiledevice/libtatsu.git" master libtatsu
build_autotools "https://github.com/turdus-m3rula/libfragmentzip.git" non-libgeneral libfragmentzip

fragmentzip_version="$(pkg-config --modversion libfragmentzip)"
if [[ ! "$fragmentzip_version" =~ ^[0-9]+$ ]] || (( fragmentzip_version < 54 )); then
  echo "libfragmentzip pkg-config version is $fragmentzip_version; turdus requires >=54" >&2
  exit 5
fi

# Clone the LGPL turdus idevicerestore fork.
git clone --depth 1 --branch sephaxx \
  https://github.com/turdus-m3rula/idevicerestore_fork.git \
  "$BUILD/idevicerestore"

# Download the official module archive linked by sep.lol and verify its hash.
RESOURCE_ARCHIVE="$BUILD/resource.tar.zst"
RESOURCE_UNPACK="$BUILD/resource-unpack"
mkdir -p "$RESOURCE_UNPACK"
curl --fail --location --retry 3 --retry-all-errors \
  "$RESOURCE_URL" \
  --output "$RESOURCE_ARCHIVE"
printf '%s  %s\n' "$RESOURCE_SHA384" "$RESOURCE_ARCHIVE" | sha384sum --check -
tar --zstd -tf "$RESOURCE_ARCHIVE"
tar --zstd -xf "$RESOURCE_ARCHIVE" -C "$RESOURCE_UNPACK"

# Convert official raw payloads into the C arrays expected by idevicerestore.
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
print("Official resource archive contents:")
for path in all_files:
    print("  ", path.relative_to(source_root))


def normalize(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "", value.lower())


def bytes_from_c(path: pathlib.Path) -> bytes:
    text = path.read_text(encoding="utf-8", errors="replace")
    values = re.findall(r"0x([0-9a-fA-F]{1,2})", text)
    if not values:
        raise SystemExit(f"No byte array found in {path}")
    return bytes(int(value, 16) for value in values)


def find_raw(aliases: tuple[str, ...]) -> pathlib.Path:
    alias_norms = [normalize(alias) for alias in aliases]
    candidates: list[tuple[int, pathlib.Path]] = []
    for path in all_files:
        suffix = path.suffix.lower()
        if suffix in {".c", ".h", ".txt", ".md", ".json", ".plist"}:
            continue
        name = normalize(path.name)
        if not any(alias in name for alias in alias_norms):
            continue
        score = 0
        if suffix in {".bin", ".raw", ".img4", ".im4p"}:
            score += 100
        if any(name in {alias, alias + "bin", alias + "raw"} for alias in alias_norms):
            score += 200
        score -= len(path.name)
        candidates.append((score, path))
    if not candidates:
        raise SystemExit(f"No official payload matched aliases {aliases}")
    candidates.sort(key=lambda item: item[0], reverse=True)
    selected = candidates[0][1]
    data = selected.read_bytes()
    if data.startswith(b"\x7fELF") or data.startswith(b"MZ"):
        raise SystemExit(f"Selected file is a host object/executable rather than a raw payload: {selected}")
    print(f"Selected {selected.relative_to(source_root)} for {aliases}")
    return selected


def write_array(symbol: str, data: bytes) -> None:
    header = stuff_root / f"{symbol}.h"
    source = stuff_root / f"{symbol}.c"
    if not header.exists():
        guard = "___" + symbol + "_H"
        header.write_text(
            f"#ifndef {guard}\n#define {guard}\n\n"
            f"extern unsigned char {symbol}[];\n"
            f"extern unsigned int {symbol}_len;\n\n#endif\n",
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
    provided_c = next(
        (path for path in all_files if path.name.lower() == f"{symbol}.c".lower()),
        None,
    )
    if provided_c is not None:
        data = bytes_from_c(provided_c)
        shutil.copy2(provided_c, stuff_root / f"{symbol}.c")
        provided_h = provided_c.with_suffix(".h")
        if provided_h.exists():
            shutil.copy2(provided_h, stuff_root / f"{symbol}.h")
        print(f"Copied provided C array for {symbol}: {len(data)} bytes")
    else:
        raw = find_raw(aliases)
        data = raw.read_bytes()
        if not data:
            raise SystemExit(f"Official payload is empty: {raw}")
        write_array(symbol, data)

    if runtime_name:
        (output_root / runtime_name).write_bytes(data)
PY

# Build the turdus-enabled native Windows restore executable.
pushd "$BUILD/idevicerestore"
./autogen.sh \
  --prefix=/mingw64 \
  --with-turdusmerula=yes \
  --without-libhfsplus \
  --without-limera1n
make
cp src/idevicerestore.exe "$STAGE/turdus_merula.exe"
popd

# Build native Windows openra1n/checkm8 through libusb.
git clone --depth 1 https://github.com/mineek/openra1n.git "$BUILD/openra1n"
pushd "$BUILD/openra1n"
make LIBUSB=1
cp openra1n.exe "$STAGE/openra1n.exe"
popd

# Build the DarkSword Pongo resource and command bridge.
gcc -std=c11 -O2 -Wall -Wextra \
  $(pkg-config --cflags libusb-1.0) \
  "$ROOT/native/pongo-bridge/pongo_bridge.c" \
  -o "$STAGE/darksword-pongo.exe" \
  $(pkg-config --libs libusb-1.0)

# Reuse the signed libwdi installer already bundled by Palera1nWin.
WDI_SIMPLE="$(find "$REPO_ROOT" -type f -iname 'wdi-simple.exe' -not -path '*/DarkSwordRestore/build/*' -print -quit || true)"
if [[ -z "$WDI_SIMPLE" ]]; then
  echo "wdi-simple.exe was not found in the parent repository" >&2
  exit 3
fi
cp "$WDI_SIMPLE" "$STAGE/wdi-simple.exe"

# Collect all transitive MinGW runtime DLLs until the set stabilizes.
for pass in 1 2 3 4; do
  for binary in "$STAGE"/*.exe "$STAGE"/*.dll; do
    [[ -f "$binary" ]] || continue
    ldd "$binary" 2>/dev/null | awk '$3 ~ /^\/mingw64\/bin\// { print $3 }' | while read -r dependency; do
      cp -n "$dependency" "$STAGE/" || true
    done
  done
done

# Record exact inputs and output hashes.
{
  echo "resource-sha384=$RESOURCE_SHA384"
  echo "idevicerestore=$(git -C "$BUILD/idevicerestore" rev-parse HEAD)"
  echo "openra1n=$(git -C "$BUILD/openra1n" rev-parse HEAD)"
  echo "libtatsu=$(git -C "$BUILD/libtatsu" rev-parse HEAD)"
  echo "libfragmentzip=$(git -C "$BUILD/libfragmentzip" rev-parse HEAD)"
  echo "libfragmentzip-version=$fragmentzip_version"
} > "$STAGE/native-build-manifest.txt"

sha256sum "$STAGE"/*.exe "$STAGE"/resources/*.bin > "$STAGE/native-SHA256SUMS.txt"
find "$STAGE" -maxdepth 2 -type f -printf '%P\n' | sort
