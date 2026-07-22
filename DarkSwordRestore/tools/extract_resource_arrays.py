#!/usr/bin/env python3
"""Extract the embedded turdus module arrays into runtime binary files."""

from __future__ import annotations

import argparse
import pathlib
import re
import sys

HEX = re.compile(r"0x([0-9a-fA-F]{1,2})")


def extract(source: pathlib.Path, symbol: str, destination: pathlib.Path) -> int:
    text = source.read_text(encoding="utf-8", errors="ignore")
    pattern = re.compile(
        r"(?:const\s+)?(?:unsigned\s+char|uint8_t)\s+"
        + re.escape(symbol)
        + r"\s*\[\s*\]\s*=\s*\{(.*?)\};",
        re.DOTALL,
    )
    match = pattern.search(text)
    if match is None:
        raise ValueError(f"array {symbol} was not found in {source}")

    values = bytes(int(value, 16) for value in HEX.findall(match.group(1)))
    if not values:
        raise ValueError(f"array {symbol} in {source} contained no bytes")

    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(values)
    return len(values)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source_root", type=pathlib.Path)
    parser.add_argument("output_root", type=pathlib.Path)
    args = parser.parse_args()

    mappings = (
        ("src/stuff/sep_racer_bin.c", "sep_racer_bin", "sep_racer.bin"),
        ("src/stuff/kpf_bin.c", "kpf_bin", "kpf.bin"),
        ("src/stuff/cpf_bin.c", "cpf_bin", "cpf.bin"),
        ("src/stuff/overlay_bin.c", "overlay_bin", "overlay.bin"),
        ("src/stuff/union_bin.c", "union_bin", "union.bin"),
    )

    for source_name, symbol, output_name in mappings:
        source = args.source_root / source_name
        if not source.exists():
            print(f"missing required source: {source}", file=sys.stderr)
            return 2
        try:
            count = extract(source, symbol, args.output_root / output_name)
        except ValueError as error:
            print(str(error), file=sys.stderr)
            return 3
        print(f"{output_name}: {count} bytes")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
