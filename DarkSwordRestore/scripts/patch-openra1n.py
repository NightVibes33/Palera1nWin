from __future__ import annotations

from pathlib import Path
import sys


PWNED_DFU_ARGUMENT = "--pwned-dfu-only"
OLD_MARKER = b"YOLO:checkra1n"
NEW_MARKER = b"PWND:[yolo]" + (b"\0" * 3)


def patch_payload_markers(source_path: Path) -> None:
    payload_directory = source_path.parent / "payloads"
    payloads = sorted(payload_directory.glob("yolo_*.bin"))
    if not payloads:
        raise SystemExit(f"No openra1n yolo payloads were found under {payload_directory}")

    patched: list[str] = []
    for payload in payloads:
        data = payload.read_bytes()
        occurrences = data.count(OLD_MARKER)
        if occurrences > 1:
            raise SystemExit(f"Unexpected duplicate pwned-DFU marker in {payload}: {occurrences}")
        if occurrences == 0:
            continue
        payload.write_bytes(data.replace(OLD_MARKER, NEW_MARKER, 1))
        patched.append(payload.name)

    if "yolo_s8003.bin" not in patched:
        raise SystemExit("The reviewed A9 s8003 payload did not contain the expected YOLO:checkra1n marker.")
    print(f"Patched pwned-DFU marker in {len(patched)} openra1n payload(s): {', '.join(patched)}")


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: patch-openra1n.py <openra1n.c>")

    path = Path(sys.argv[1])
    source = path.read_text(encoding="utf-8")

    old_marker_declaration = 'static const char *pwnd_str = " YOLO:checkra1n";'
    new_marker_declaration = 'static const char *pwnd_str = " PWND:[yolo]";'
    if source.count(old_marker_declaration) != 1:
        raise SystemExit("Pinned openra1n pwned-DFU marker declaration changed.")
    source = source.replace(old_marker_declaration, new_marker_declaration, 1)

    old_main = """int main(int argc, char **argv) {
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
    new_main = f"""int main(int argc, char **argv) {{
\tLOG_RAINBOW("-=-=- openra1n -=-=-");
\tbool pwned_dfu_only = false;
\tif(argc == 2 && strcmp(argv[1], "{PWNED_DFU_ARGUMENT}") == 0) {{
\t\tpwned_dfu_only = true;
\t}} else if(argc != 1) {{
\t\tLOG_ERROR("Usage: %s [{PWNED_DFU_ARGUMENT}]", argv[0]);
\t\treturn EXIT_FAILURE;
\t}}
\tusb_handle_t handle;
\tusb_timeout = 5;
\tusb_abort_timeout_min = 0;
\tLOG_INFO("Waiting for DFU mode device");
\tif(!gaster_checkm8(&handle)) {{
\t\tLOG_ERROR("checkm8 did not reach pwned DFU");
\t\treturn EXIT_FAILURE;
\t}}
\tif(pwned_dfu_only) {{
\t\tLOG_INFO("Pwned DFU ready with PWND:[yolo]; PongoOS was not uploaded");
\t\treturn EXIT_SUCCESS;
\t}}
\tsleep_ms(3000);
\tcheckm8_boot_pongo(&handle);
\tLOG_INFO("PongoOS payload sent; Windows may temporarily show the device as disconnected while 05AC:4141 enumerates");
\treturn EXIT_SUCCESS;
}}"""

    occurrences = source.count(old_main)
    if occurrences != 1:
        raise SystemExit(
            f"Pinned openra1n main changed; expected one reviewed block, found {occurrences}."
        )

    patch_payload_markers(path)
    path.write_text(source.replace(old_main, new_main, 1), encoding="utf-8", newline="\n")
    print("Patched openra1n with separate verified pwned-DFU and PongoOS modes.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
