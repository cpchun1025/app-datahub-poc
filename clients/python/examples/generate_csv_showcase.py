#!/usr/bin/env python3
"""Generate synthetic CSV snapshots for the optional local file-ingestion showcase."""

from __future__ import annotations

import argparse
import csv
import io
import os
import random
import tempfile
from pathlib import Path

SPOT_HEADER = ("InstrumentId", "ProviderSymbol", "Price", "Currency", "SourceUpdatedAtUtc", "PriceKind")
COMPOSITION_HEADER = ("IndexId", "InstrumentId", "Weight", "EffectiveDate")


def datasets(seed: int, scenario: str) -> dict[str, list[tuple[str, ...]]]:
    rng = random.Random(seed)
    instruments = [f"hk-{number:04d}" for number in range(5, 25)]
    prices = {
        instrument: f"{rng.uniform(10, 500):.8f}"
        for instrument in instruments
    }
    if scenario == "next":
        prices["hk-0005"] = "123.45678901"
        prices["hk-0025"] = f"{rng.uniform(10, 500):.8f}"
        del prices["hk-0024"]
        instruments = [instrument for instrument in instruments if instrument != "hk-0024"]
        instruments.append("hk-0025")

    spot = [
        (instrument, instrument.removeprefix("hk-") + ".HK", prices[instrument], "HKD",
         "2026-03-01T10:00:00+08:00", "Last")
        for instrument in instruments
    ]
    if scenario == "malformed":
        spot[0] = (*spot[0][:2], "not-a-decimal", *spot[0][3:])

    index_a = [(instrument, f"{rng.uniform(0.02, 0.12):.6f}") for instrument in instruments[:12]]
    index_b = [(instrument, f"{rng.uniform(0.02, 0.12):.6f}") for instrument in instruments[8:]]
    if scenario == "next":
        index_a = [(instrument, "0.200000") if instrument == "hk-0005" else (instrument, weight)
                   for instrument, weight in index_a]
        index_b = [(instrument, weight) for instrument, weight in index_b if instrument != "hk-0013"]

    composition = [
        (index_id, instrument, weight, "2026-03-01")
        for index_id, members in (("DEMO-HK-LARGE", index_a), ("DEMO-HK-GROWTH", index_b))
        for instrument, weight in members
    ]
    result = {"spot.csv": [SPOT_HEADER, *spot]}
    if scenario != "malformed":
        result["composition.csv"] = [COMPOSITION_HEADER, *composition]
    return result


def publish(path: Path, rows: list[tuple[str, ...]]) -> None:
    buffer = io.StringIO(newline="")
    writer = csv.writer(buffer, lineterminator="\r\n")
    writer.writerows(rows)
    encoded = buffer.getvalue().encode("utf-8")
    temporary: str | None = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=f".{path.name}.", suffix=".tmp",
                                         delete=False) as output:
            temporary = output.name
            output.write(encoded)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        if temporary is not None and os.path.exists(temporary):
            os.unlink(temporary)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", required=True, type=Path,
                        help="Explicit directory for spot.csv and composition.csv")
    parser.add_argument("--scenario", choices=("initial", "next", "malformed"), default="initial")
    parser.add_argument("--seed", type=int, default=20260301)
    args = parser.parse_args()
    output_dir = args.output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    for name, rows in datasets(args.seed, args.scenario).items():
        publish(output_dir / name, rows)
        print(f"Published {name} ({len(rows) - 1} rows) [{args.scenario}]")


if __name__ == "__main__":
    main()
