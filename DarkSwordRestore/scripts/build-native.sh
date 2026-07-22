#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
REPO_ROOT="$(cd "${PROJECT_ROOT}/.." && pwd)"
BUILD_ROOT="${PROJECT_ROOT}/build"
OUT_ROOT="${BUILD_ROOT}/native-stage"
LOG_PATH="${BUILD_ROOT}/native-build.log"
RESOURCE_URL="https://sep.lol/files/resources/resourcesV3/735c0d45a6ceb9f51c160f165c641c39ecbef4374fe0532daae1bdecd666389207918f2f839ada8b340e16a92fec2643/resource.tar.zst"
RESOURCE_SHA384="735c0d45a6ceb9f51c160f165c641c39ecbef4374fe0532daae1bdecd666389207918f2f839ada8b340e16a92fec2643"

as_msys_path() {
  local value="$1"
  if [[ "${value}" =~ ^[A-Za-z]:[\\/] ]]; then
    cygpath -u "${value}"
  else
    printf '%s\n' "${value}"
  fi
}

if [[ -n "${RUNNER_TEMP:-}" ]]; then
  TEMP_ROOT="$(as_msys_path "${RUNNER_TEMP}")"
else
  TEMP_ROOT="${PROJECT_ROOT}/.native-build"
fi
WORK_ROOT="${TEMP_ROOT}/darksword-native"

mkdir -p "${BUILD_ROOT}"
: > "${LOG_PATH}"
exec > >(tee -a "${LOG_PATH}") 2>&1

on_error() {
  local status=$?
  echo
  echo "========== DARKSWORD NATIVE BUILD FAILURE =========="
  echo "exit_status=${status}"
  echo "log=${LOG_PATH}"
  echo "last 160 lines:"
  tail -n 160 "${LOG_PATH}" || true
  exit "${status}"
}
trap on_error ERR

export PATH="/mingw64/bin:/usr/bin:${PATH}"
export PKG_CONFIG_PATH="/mingw64/lib/pkgconfig:/mingw64/share/pkgconfig"
export CFLAGS="${CFLAGS:-} -O2"
export MAKEFLAGS="-j2"

printf 'project_root=%s\n' "${PROJECT_ROOT}"
printf 'repo_root=%s\n' "${REPO_ROOT}"
printf 'runner_temp_raw=%s\n' "${RUNNER_TEMP:-unset}"
printf 'work_root=%s\n' "${WORK_ROOT}"

rm -rf "${WORK_ROOT}" "${OUT_ROOT}"
mkdir -p "${WORK_ROOT}" "${OUT_ROOT}/resources"

pacman -S --needed --noconfirm \
  base-devel git autoconf automake-wrapper libtool make pkgconf python \
  mingw-w64-x86_64-gcc \
  mingw-w64-x86_64-pkgconf \
  mingw-w64-x86_64-curl \
  mingw-w64-x86_64-libzip \
  mingw-w64-x86_64-zlib \
  mingw-w64-x86_64-openssl \
  mingw-w64-x86_64-libusb \
  mingw-w64-x86_64-readline \
  mingw-w64-x86_64-libplist \
  mingw-w64-x86_64-libimobiledevice-glue \
  mingw-w64-x86_64-libusbmuxd \
  mingw-w64-x86_64-libimobiledevice \
  mingw-w64-x86_64-libirecovery \
  mingw-w64-x86_64-zstd \
  mingw-w64-x86_64-xz \
  mingw-w64-x86_64-bzip2

build_autotools() {
  local repository="$1"
  local ref="$2"
  local directory="$3"
  shift 3

  echo "========== building ${directory} =========="
  git clone --depth 1 --branch "${ref}" "${repository}" "${WORK_ROOT}/${directory}"
  pushd "${WORK_ROOT}/${directory}"
  ./autogen.sh --prefix=/mingw64 "$@"
  make
  make install
  popd
}

# Dependencies that are either newer than the MSYS2 package or not shipped there.
build_autotools "https://github.com/libimobiledevice/libtatsu.git" master libtatsu
build_autotools "https://github.com/turdus-m3rula/libfragmentzip.git" non-libgeneral libfragmentzip

# Clone the turdus-enabled restore source before generating its embedded resources.
echo "========== preparing turdus idevicerestore =========="
git clone --depth 1 --branch sephaxx \
  https://github.com/turdus-m3rula/idevicerestore_fork.git \
  "${WORK_ROOT}/idevicerestore"

# The source fork intentionally references generated resource arrays. Download the
# official sep.lol archive, verify its SHA-384, then create those arrays locally.
RESOURCE_ARCHIVE="${WORK_ROOT}/resource.tar.zst"
RESOURCE_UNPACK="${WORK_ROOT}/resource-unpack"
rm -rf "${RESOURCE_UNPACK}"
mkdir -p "${RESOURCE_UNPACK}"

echo "========== downloading verified turdus resources =========="
curl --fail --location --retry 3 --retry-all-errors \
  "${RESOURCE_URL}" \
  --output "${RESOURCE_ARCHIVE}"
printf '%s  %s\n' "${RESOURCE_SHA384}" "${RESOURCE_ARCHIVE}" | sha384sum --check -
tar --zstd -tf "${RESOURCE_ARCHIVE}"
tar --zstd -xf "${RESOURCE_ARCHIVE}" -C "${RESOURCE_UNPACK}"

python - "${RESOURCE_UNPACK}" "${WORK_ROOT}/idevicerestore/src/stuff" "${OUT_ROOT}/resources" <<'PY'
from __future__ import annotations

import pathlib
import re
import shutil
import sys

source_root = pathlib.Path(sys.argv[1])
stuff_root = pathlib.Path(sys.argv[2])
output_root = pathlib.Path(sys.argv[3])
stuff_root.mkdir(parents=True, exist_ok=True)
output_root.mkdir(parents=True, exist_ok=True)

specs: list[tuple[str, tuple[str, ...], str | None]] = [
    ("Pongo_bin", ("pongo", "pongoos"), None),
    ("cpf_bin", ("cpf",), None),
    ("kpf_bin", ("kpf",), "kpf.bin"),
    ("sep_racer_bin", ("sep_racer", "sepracer"), "sep_racer.bin"),
    ("overlay_bin", ("overlay",), None),
    ("union_bin", ("union",), None),
]

all_files = [path for path in source_root.rglob("*") if path.is_file()]
if not all_files:
    raise SystemExit(f"Official resource archive extracted no files under {source_root}")

print("Official resource archive files:")
for path in all_files:
    print("  ", path.relative_to(source_root))


def normalized(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "", value.lower())


def values_from_c(source: pathlib.Path) -> bytes:
    text = source.read_text(encoding="utf-8", errors="ignore")
    values = bytes(int(value, 16) for value in re.findall(r"0x([0-9a-fA-F]{1,2})", text))
    if not values:
        raise SystemExit(f"No byte array values found in {source}")
    return values


def find_existing_array(symbol: str) -> pathlib.Path | None:
    wanted = f"{symbol}.c".lower()
    return next((path for path in all_files if path.name.lower() == wanted), None)


def find_raw(aliases: tuple[str, ...]) -> pathlib.Path:
    alias_norms = [normalized(alias) for alias in aliases]
    candidates: list[tuple[int, pathlib.Path]] = []
    for path in all_files:
        if path.suffix.lower() in {".c", ".h", ".txt", ".md", ".json", ".plist"}:
            continue
        name = normalized(path.name)
        if not any(alias in name for alias in alias_norms):
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
    print(f"Selected {selected.relative_to(source_root)} for {aliases}")
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
            chunk = data[offset : offset + 16]
            handle.write("    " + ", ".join(f"0x{value:02x}" for value in chunk) + ",\n")
        handle.write(f"}};\nunsigned int {symbol}_len = {len(data)};\n")
    print(f"Generated {source.name}: {len(data)} bytes")


for symbol, aliases, runtime_name in specs:
    existing_source = find_existing_array(symbol)
    if existing_source is not None:
        data = values_from_c(existing_source)
        shutil.copy2(existing_source, stuff_root / f"{symbol}.c")
        existing_header = existing_source.with_suffix(".h")
        if existing_header.exists():
            shutil.copy2(existing_header, stuff_root / f"{symbol}.h")
        elif not (stuff_root / f"{symbol}.h").exists():
            write_array(symbol, data)
        print(f"Copied provided C array for {symbol}: {len(data)} bytes")
    else:
        raw = find_raw(aliases)
        data = raw.read_bytes()
        if not data:
            raise SystemExit(f"Official resource is empty: {raw}")
        write_array(symbol, data)

    if runtime_name:
        runtime = output_root / runtime_name
        runtime.write_bytes(data)
        print(f"Wrote runtime resource {runtime.name}: {len(data)} bytes")
PY

# Build the restore engine only after every referenced resource source exists.
echo "========== building turdus idevicerestore =========="
pushd "${WORK_ROOT}/idevicerestore"
./autogen.sh \
  --prefix=/mingw64 \
  --with-turdusmerula=yes \
  --without-limera1n \
  --without-libhfsplus
make
cp src/idevicerestore.exe "${OUT_ROOT}/turdus_merula.exe"
popd

# Build Windows openra1n (checkm8 + PongoOS) through libusb.
echo "========== building openra1n =========="
git clone --depth 1 https://github.com/mineek/openra1n.git "${WORK_ROOT}/openra1n"
pushd "${WORK_ROOT}/openra1n"
make LIBUSB=1
cp openra1n.exe "${OUT_ROOT}/openra1n.exe"
popd

# Build the small native Pongo command/resource sender used by the app.
echo "========== building DarkSword Pongo bridge =========="
gcc -std=c11 -O2 -Wall -Wextra \
  $(pkg-config --cflags libusb-1.0) \
  "${PROJECT_ROOT}/native/pongo-bridge/pongo_bridge.c" \
  -o "${OUT_ROOT}/darksword-pongo.exe" \
  $(pkg-config --libs libusb-1.0)

copy_existing_tool() {
  local name="$1"
  local found
  found="$(find "${REPO_ROOT}" -type f -iname "${name}" -not -path '*/DarkSwordRestore/build/*' -print -quit || true)"
  if [[ -z "${found}" ]]; then
    echo "Required existing Windows helper was not found: ${name}" >&2
    return 3
  fi
  cp "${found}" "${OUT_ROOT}/${name}"
}

copy_existing_tool wdi-simple.exe
cp /mingw64/bin/libusb-1.0.dll "${OUT_ROOT}/libusb-1.0.dll"

copy_runtime_dlls() {
  local binary="$1"
  while IFS= read -r dll; do
    [[ -f "${dll}" ]] && cp -n "${dll}" "${OUT_ROOT}/"
  done < <(ldd "${binary}" 2>/dev/null | awk '$3 ~ /^\/mingw64\/bin\// {print $3}')
}

for pass in 1 2 3 4; do
  for binary in "${OUT_ROOT}"/*.exe "${OUT_ROOT}"/*.dll; do
    [[ -f "${binary}" ]] || continue
    copy_runtime_dlls "${binary}"
  done
done

{
  echo "resource-sha384=${RESOURCE_SHA384}"
  echo "turdus idevicerestore: $(git -C "${WORK_ROOT}/idevicerestore" rev-parse HEAD)"
  echo "openra1n: $(git -C "${WORK_ROOT}/openra1n" rev-parse HEAD)"
  echo "libtatsu: $(git -C "${WORK_ROOT}/libtatsu" rev-parse HEAD)"
  echo "libfragmentzip: $(git -C "${WORK_ROOT}/libfragmentzip" rev-parse HEAD)"
} > "${OUT_ROOT}/native-build-manifest.txt"

for required in \
  turdus_merula.exe \
  openra1n.exe \
  darksword-pongo.exe \
  wdi-simple.exe \
  libusb-1.0.dll \
  resources/sep_racer.bin \
  resources/kpf.bin; do
  [[ -s "${OUT_ROOT}/${required}" ]] || {
    echo "Missing or empty native output: ${required}" >&2
    exit 4
  }
done

sha256sum "${OUT_ROOT}"/*.exe "${OUT_ROOT}"/resources/*.bin > "${OUT_ROOT}/native-SHA256SUMS.txt"
find "${OUT_ROOT}" -maxdepth 2 -type f -printf '%P\n' | sort
trap - ERR
echo "DarkSword native Windows toolchain built successfully."
