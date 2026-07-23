#!/usr/bin/env bash
set -Eeuo pipefail

# The Windows openra1n stage has already placed the selected device in PongoOS and
# the launcher attached that exact 05ac:4141 bus to WSL. palera1n probes for a
# checkra1n-compatible handoff helper on some builds; returning success here tells it
# not to run a second, competing checkm8 process inside WSL.
if ! lsusb 2>/dev/null | grep -qi '05ac:4141'; then
  echo '[Palera1nWin] PongoOS 05ac:4141 is not visible in WSL; refusing fake checkra1n success.' >&2
  exit 66
fi

echo '[Palera1nWin] Windows openra1n/PongoOS handoff verified; skipping duplicate checkm8.' >&2
exit 0
