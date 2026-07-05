#!/usr/bin/env python3
"""Regenerate Resources/Data/oui.txt.gz from the official IEEE MA-L registry.

Usage:
    python3 scripts/update-oui.py

Downloads https://standards-oui.ieee.org/oui/oui.csv, keeps only MA-L
(24-bit / 6 hex-char) assignments — which is what OuiLookup.cs looks up —
dedupes, sorts, and writes a compact "OUI|Vendor" text file compressed with
gzip. Run this occasionally to pick up newly registered vendors.
"""
import csv
import gzip
import re
import urllib.request
from pathlib import Path

SOURCE_URL = "https://standards-oui.ieee.org/oui/oui.csv"
OUTPUT = Path(__file__).parent.parent / "Resources" / "Data" / "oui.txt.gz"
MAX_VENDOR_LEN = 60


def fetch_csv() -> str:
    with urllib.request.urlopen(SOURCE_URL, timeout=30) as resp:
        return resp.read().decode("utf-8")


def parse(csv_text: str) -> list[tuple[str, str]]:
    rows: list[tuple[str, str]] = []
    seen: set[str] = set()
    reader = csv.reader(csv_text.splitlines())
    next(reader, None)  # header
    for row in reader:
        if len(row) < 3:
            continue
        registry, assignment, org = row[0], row[1].strip().upper(), row[2]
        if registry != "MA-L":
            continue
        if not re.fullmatch(r"[0-9A-F]{6}", assignment):
            continue
        org = re.sub(r"\s+", " ", org).strip().strip('"')
        if not org or assignment in seen:
            continue
        seen.add(assignment)
        rows.append((assignment, org[:MAX_VENDOR_LEN].rstrip()))
    rows.sort(key=lambda r: r[0])
    return rows


def main() -> None:
    rows = parse(fetch_csv())
    text = "\n".join(f"{oui}|{org}" for oui, org in rows) + "\n"
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_bytes(gzip.compress(text.encode("utf-8"), compresslevel=9))
    print(f"Wrote {len(rows)} OUIs -> {OUTPUT} ({OUTPUT.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
