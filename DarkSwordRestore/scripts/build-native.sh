#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
REPO_ROOT="$(cd "${PROJECT_ROOT}/.." && pwd)"
WORK_ROOT="${RUNNER_TEMP:-${PROJECT_ROOT}/.native-build}/darksword-native"
OUT_ROOT="${PROJECT_ROOT}/artifacts/native"

export PATH="/mingw64/bin:/usr/bin:${PATH}"
export PKG_CONFIG_PATH="/mingw64/lib/pkgconfig:/mingw64/share/pkgconfig"
export CFLAGS="${CFLAGS:-} -O2"
export MAKEFLAGS="-j2"

rm -rf "${WORK_ROOT}" "${OUT_ROOT}"
mkdir -p "${WORK_ROOT}" "${OUT_ROOT}" "${PROJECT_ROOT}/toolchain/resources"

pacman -S --needed --noconfirm \
  base-devel git autoconf automake libtool make pkgconf python \
  mingw-w64-x86_64-toolchain \
  mingw-w64-x86_64-autotools \
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
  mingw-w64-x86_64-libirecovery

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

# Dependencies not currently shipped as official MSYS2 packages.
build_autotools "https://github.com/libimobiledevice/libtatsu.git" master libtatsu
build_autotools "https://github.com/turdus-m3rula/libfragmentzip.git" non-libgeneral libfragmentzip

# Build the turdus-enabled restore engine.
git clone --depth 1 --branch sephaxx \
  https://github.com/turdus-m3rula/idevicerestore_fork.git \
  "${WORK_ROOT}/idevicerestore"
pushd "${WORK_ROOT}/idevicerestore"
./autogen.sh --prefix=/mingw64 --with-turdusmerula=yes --without-limera1n
make
cp src/idevicerestore.exe "${OUT_ROOT}/turdus_merula.exe"
popd

# Build Windows openra1n from source through the libusb backend.
git clone --depth 1 https://github.com/mineek/openra1n.git "${WORK_ROOT}/openra1n"
pushd "${WORK_ROOT}/openra1n"
make LIBUSB=1
cp openra1n.exe "${OUT_ROOT}/openra1n.exe"
popd

cp /mingw64/bin/irecovery.exe "${OUT_ROOT}/irecovery.exe"
cp /mingw64/bin/libusb-1.0.dll "${OUT_ROOT}/libusb-1.0.dll"

copy_existing_tool() {
  local name="$1"
  local found
  found="$(find "${REPO_ROOT}" -type f -iname "${name}" -not -path '*/artifacts/*' -print -quit || true)"
  if [[ -z "${found}" ]]; then
    echo "Required existing Windows helper was not found: ${name}" >&2
    exit 3
  fi
  cp "${found}" "${OUT_ROOT}/${name}"
}

copy_existing_tool gaster.exe
copy_existing_tool wdi-simple.exe

# Convert the fork's generated C arrays into the files consumed by PongoTransport.
python "${PROJECT_ROOT}/tools/extract_resource_arrays.py" \
  "${WORK_ROOT}/idevicerestore" \
  "${PROJECT_ROOT}/toolchain/resources"
cp "${PROJECT_ROOT}/toolchain/resources/"*.bin "${OUT_ROOT}/"

copy_runtime_dlls() {
  local executable="$1"
  while IFS= read -r dll; do
    [[ -f "${dll}" ]] && cp -n "${dll}" "${OUT_ROOT}/"
  done < <(ldd "${executable}" | awk '/=> \/mingw64\/bin\// {print $3}')
}

copy_runtime_dlls "${OUT_ROOT}/turdus_merula.exe"
copy_runtime_dlls "${OUT_ROOT}/openra1n.exe"
copy_runtime_dlls "${OUT_ROOT}/irecovery.exe"

# Record exactly what was built for the release package.
{
  echo "turdus idevicerestore: $(git -C "${WORK_ROOT}/idevicerestore" rev-parse HEAD)"
  echo "openra1n: $(git -C "${WORK_ROOT}/openra1n" rev-parse HEAD)"
  echo "libtatsu: $(git -C "${WORK_ROOT}/libtatsu" rev-parse HEAD)"
  echo "libfragmentzip: $(git -C "${WORK_ROOT}/libfragmentzip" rev-parse HEAD)"
} > "${OUT_ROOT}/native-build-manifest.txt"

find "${OUT_ROOT}" -maxdepth 1 -type f -printf '%f\n' | sort
