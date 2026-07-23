from __future__ import annotations

from pathlib import Path
import sys


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: patch-openra1n.py <openra1n.c>")

    path = Path(sys.argv[1])
    source = path.read_text(encoding="utf-8")
    old = """int main(int argc, char **argv) {
\tLOG_RAINBOW("-=-=- openra1n -=-=-");
\tint ret = EXIT_FAILURE;
\tusb_handle_t handle;
\tusb_timeout = 5;
\tusb_abort_timeout_min = 0;
\tLOG_INFO("Waiting for DFU mode device");
\tgaster_checkm8(&handle);
\tsleep_ms(3000);
\tcheckm8_boot_pongo(&handle);
\treturn ret;
}"""
    new = """int main(int argc, char **argv) {
\tLOG_RAINBOW("-=-=- openra1n -=-=-");
\tusb_handle_t handle;
\tusb_timeout = 5;
\tusb_abort_timeout_min = 0;
\tLOG_INFO("Waiting for DFU mode device");
\tif(!gaster_checkm8(&handle)) {
\t\tLOG_ERROR("checkm8 did not reach pwned DFU");
\t\treturn EXIT_FAILURE;
\t}
\tsleep_ms(3000);
\tcheckm8_boot_pongo(&handle);
\tLOG_INFO("PongoOS payload sent; Windows may temporarily show the device as disconnected while 05AC:4141 enumerates");
\treturn EXIT_SUCCESS;
}"""

    occurrences = source.count(old)
    if occurrences != 1:
        raise SystemExit(
            f"Pinned openra1n main changed; expected one reviewed block, found {occurrences}."
        )
    path.write_text(source.replace(old, new, 1), encoding="utf-8", newline="\n")
    print("Patched openra1n to return success only after checkm8 and Pongo payload delivery.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
