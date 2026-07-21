#!/usr/bin/env python3
"""Extract byte arrays from turdus idevicerestore generated *_bin.c files."""

from __future__ import annotations

import argparse
import pathlib
import re
import sys

HEX = re.compile(r"0x([0-9a-fA-F]{1,2})")


def extract(source: pathlib.Path, destination: pathlib.Path) -> int:
    text = source.read_text(encoding="utf-8", errors="ignore")
    values = bytes(int(match, 16) for match in HEX.findall(text))
    if not values:
        raise ValueError(f"no byte values found in {source}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(values)
    return len(values)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source_root", type=pathlib.Path)
    parser.add_argument("output_root", type=pathlib.Path)
    args = parser.parse_args()

    mappings = {
        "src/stuff/sep_racer_bin.c": "sep_racer.bin",
        "src/stuff/kpf_bin.c": "kpf.bin",
        "src/stuff/cpf_bin.c": "cpf.bin",
        "src/stuff/overlay_bin.c": "overlay.bin",
        "src/stuff/union_bin.c": "union.bin",
    }

    for source_name, output_name in mappings.items():
        source = args.source_root / source_name
        if not source.exists():
            print(f"missing required source: {source}", file=sys.stderr)
            return 2
        count = extract(source, args.output_root / output_name)
        print(f"{output_name}: {count} bytes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
