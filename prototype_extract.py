#!/usr/bin/env python3
"""CLI wrapper preserving the user's reliable prototype workflow."""
from __future__ import annotations

import argparse
import csv
import json
import os
import sys
from pathlib import Path

from dotenv import load_dotenv

from extractor import basic_sanity_checks, extract_many

load_dotenv()


def collect_images(inputs: list[str]) -> list[Path]:
    paths: list[Path] = []
    allowed = {".png", ".jpg", ".jpeg", ".webp", ".gif"}
    for raw in inputs:
        p = Path(raw)
        if p.is_dir():
            paths.extend(sorted(c for c in p.iterdir() if c.is_file() and c.suffix.lower() in allowed))
        elif p.is_file() and p.suffix.lower() in allowed:
            paths.append(p)
        else:
            raise FileNotFoundError(f"Not a supported image or folder: {raw}")
    return list(dict.fromkeys(p.resolve() for p in paths))


def main() -> int:
    parser = argparse.ArgumentParser(description="Extract Last War ranking data from screenshots.")
    parser.add_argument("inputs", nargs="+")
    parser.add_argument("--output-dir", default="output")
    parser.add_argument("--model", default=os.getenv("OPENAI_MODEL", "gpt-5.6-terra"))
    parser.add_argument("--base-url", default=os.getenv("OPENAI_BASE_URL", "https://api.openai.com/v1"))
    parser.add_argument("--api-style", choices=["responses", "chat"], default=os.getenv("OPENAI_API_STYLE", "responses"))
    parser.add_argument("--rpm", type=int, default=int(os.getenv("OPENAI_RPM", "28")))
    args = parser.parse_args()

    local_endpoint = any(host in args.base_url.casefold() for host in ("localhost", "127.0.0.1", "::1"))
    if not os.getenv("OPENAI_API_KEY") and not local_endpoint:
        print("OPENAI_API_KEY is not set. Set it in the environment or .env.", file=sys.stderr)
        return 2

    images = collect_images(args.inputs)
    print(f"Processing {len(images)} screenshot(s) at <= {args.rpm} RPM ...")

    def progress(done, total, result):
        if result.error:
            print(f"[{done}/{total}] {result.image_path.name}: ERROR {result.error}")
        else:
            e = result.extraction
            print(f"[{done}/{total}] {result.image_path.name}: {e.detected_day}, {len(e.rows)} rows")

    results = extract_many(
        images, model=args.model, base_url=args.base_url,
        requests_per_minute=args.rpm, progress=progress, api_style=args.api_style
    )
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    payload = []
    for result in results:
        if result.error:
            payload.append({"source_file": result.image_path.name, "error": result.error})
            continue
        e = result.extraction
        payload.append({
            "source_file": result.image_path.name,
            **e.model_dump(),
            "local_validation_warnings": basic_sanity_checks(e),
        })
    (output_dir / "extraction_results.json").write_text(
        json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    with (output_dir / "extraction_rows.csv").open("w", newline="", encoding="utf-8-sig") as f:
        writer = csv.DictWriter(f, fieldnames=[
            "source_file", "day", "ui_language", "rank", "player_id", "raw_name",
            "alliance_name", "points", "extraction_confidence", "is_pinned_row"
        ])
        writer.writeheader()
        for result in results:
            if result.error or not result.extraction:
                continue
            e = result.extraction
            for row in e.rows + ([e.pinned_row] if e.pinned_row else []):
                writer.writerow({
                    "source_file": result.image_path.name,
                    "day": e.detected_day,
                    "ui_language": e.ui_language,
                    "rank": row.rank,
                    "player_id": row.player_id,
                    "raw_name": row.raw_name,
                    "alliance_name": getattr(row, "alliance_name", None),
                    "points": row.points,
                    "extraction_confidence": row.extraction_confidence,
                    "is_pinned_row": row is e.pinned_row,
                })
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
