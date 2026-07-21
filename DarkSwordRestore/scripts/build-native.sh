#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
REPO_ROOT="$(cd "${PROJECT_ROOT}/.." && pwd)"
WORK_ROOT="${RUNNER_TEMP:-${PROJECT_ROOT}/.native-build}/darksword-native"
BUILD_ROOT="${PROJECT_ROOT}/build"
OUT_ROOT="${BUILD_ROOT}/native-stage"
LOG_PATH="${BUILD_ROOT}/native-build.log"

mkdir -p "${BUILD_ROOT}"
: > "${LOG_PATH}"
exec > >(tee -a "${LOG_PATH}") 2>&1

on_error() {
  local status=$?
  echo
  echo "========== DARKSWORD NATIVE BUILD FAILURE =========="
  echo "exit_status=${status}"
  echo "log=${LOG_PATH}"
  echo "last 120 lines:"
  tail -n 120 "${LOG_PATH}" || true
  exit "${status}"
}
trap on_error ERR

export PATH="/mingw64/bin:/usr/bin:${PATH}"
export PKG_CONFIG_PATH="/mingw64/lib/pkgconfig:/mingw64/share/pkgconfig"
export CFLAGS="${CFLAGS:-} -O2"
export MAKEFLAGS="-j2"

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

  git clone --depth 1 --branch "${ref}" "${repository}" "${WORK_ROOT}/${directory}"
  pushd "${WORK_ROOT}/${directory}"
  ./autogen.sh --prefix=/mingw64 "$@"
  make
  make install
  popd
}

# These dependencies are built from their current upstream source so the
# workflow does not depend on another repository retaining a named artifact.
build_autotools "https://github.com/libimobiledevice/libtatsu.git" master libtatsu
build_autotools "https://github.com/turdus-m3rula/libfragmentzip.git" non-libgeneral libfragmentzip

# Build the turdus-enabled restore engine for MinGW64.
git clone --depth 1 --branch sephaxx \
  https://github.com/turdus-m3rula/idevicerestore_fork.git \
  "${WORK_ROOT}/idevicerestore"
pushd "${WORK_ROOT}/idevicerestore"
./autogen.sh \
  --prefix=/mingw64 \
  --with-turdusmerula=yes \
  --without-limera1n \
  --without-libhfsplus
make
cp src/idevicerestore.exe "${OUT_ROOT}/turdus_merula.exe"
popd

# Extract the Pongo modules embedded in the fork's generated C arrays.
python "${PROJECT_ROOT}/tools/extract_resource_arrays.py" \
  "${WORK_ROOT}/idevicerestore" \
  "${OUT_ROOT}/resources"

# Build Windows openra1n (checkm8 + PongoOS) through libusb.
git clone --depth 1 https://github.com/mineek/openra1n.git "${WORK_ROOT}/openra1n"
pushd "${WORK_ROOT}/openra1n"
make LIBUSB=1
cp openra1n.exe "${OUT_ROOT}/openra1n.exe"
popd

# Build the small native Pongo command/resource sender used by the app.
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
  local executable="$1"
  while IFS= read -r dll; do
    [[ -f "${dll}" ]] && cp -n "${dll}" "${OUT_ROOT}/"
  done < <(ldd "${executable}" 2>/dev/null | awk '$3 ~ /^\/mingw64\/bin\// {print $3}')
}

for pass in 1 2 3; do
  for executable in "${OUT_ROOT}"/*.exe; do
    [[ -f "${executable}" ]] || continue
    copy_runtime_dlls "${executable}"
  done
  for library in "${OUT_ROOT}"/*.dll; do
    [[ -f "${library}" ]] || continue
    copy_runtime_dlls "${library}"
  done
done

{
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

find "${OUT_ROOT}" -maxdepth 2 -type f -printf '%P\n' | sort
trap - ERR
echo "DarkSword native Windows toolchain built successfully."
