from __future__ import annotations

import base64
import json
import mimetypes
import os
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

from dotenv import load_dotenv
from openai import OpenAI
from pydantic import BaseModel, Field, ValidationError, field_validator

load_dotenv()

PROMPT = r"""
You are extracting ranking data from a mobile game screenshot.

Return only data matching the supplied JSON schema.

TASK
1. Determine which weekday tab is selected.
2. Normalize the selected day to exactly one of:
   monday, tuesday, wednesday, thursday, friday, saturday.
3. Do NOT rely only on the written language of the tab.
   The UI language may be English, Turkish, Arabic, or mixed.
   Prefer the VISUAL POSITION of the selected tab:
   1=Monday, 2=Tuesday, 3=Wednesday,
   4=Thursday, 5=Friday, 6=Saturday.
4. Extract every sufficiently visible ranking row in the main scrolling list.
5. For each row extract:
   - rank
   - player ID only if it is explicitly visible before/near the player name
   - player name exactly as visible
   - points as an integer with separators removed
   - avatar_bbox: the tight bounding box of the player's square/circular avatar,
     using normalized screenshot coordinates from 0 to 1000 for x, y, width, height.
     Return null only when the avatar is not sufficiently visible.
6. A highlighted/pinned alliance/self row may appear separately at the bottom.
   Extract it into pinned_row and DO NOT include it again in rows.
7. Never infer a missing player ID from rank, avatar, name, or prior knowledge.
8. Preserve Unicode characters in player names.
9. Do not translate player names.
10. Ignore alliance text such as "[EfC] Elite Force Commander".
11. Ignore banners, timers, announcements, buttons, headers, and unrelated UI text.
12. If a row is too obscured to read its score reliably, omit it rather than inventing data.
13. If text is ambiguous:
    - return the best visible reading,
    - lower extraction_confidence,
    - add a short warning.
14. Rank means leaderboard position, not player ID.
15. UI language is descriptive only and may be:
    english, turkish, arabic, mixed, unknown.

Important quality rules:
- Copy all digits carefully.
- Distinguish player ID from leaderboard rank.
- Do not "correct" unusual spellings.
- Do not fabricate IDs for screenshots that do not display IDs.
- avatar_bbox must contain only the avatar/profile image and its decorative frame, not the rank or name.
- Coordinates are normalized to the ENTIRE supplied screenshot: top-left=(0,0), bottom-right=(1000,1000).
"""

SCHEMA: dict[str, Any] = {
    "type": "object",
    "additionalProperties": False,
    "properties": {
        "detected_day": {
            "type": "string",
            "enum": [
                "monday", "tuesday", "wednesday",
                "thursday", "friday", "saturday"
            ],
        },
        "day_confidence": {"type": "number", "minimum": 0, "maximum": 1},
        "ui_language": {
            "type": "string",
            "enum": ["english", "turkish", "arabic", "mixed", "unknown"],
        },
        "rows": {
            "type": "array",
            "items": {
                "type": "object",
                "additionalProperties": False,
                "properties": {
                    "rank": {"type": "integer", "minimum": 1},
                    "player_id": {
                        "anyOf": [
                            {"type": "integer", "minimum": 1},
                            {"type": "null"},
                        ]
                    },
                    "raw_name": {"type": "string", "minLength": 1},
                    "points": {"type": "integer", "minimum": 0},
                    "extraction_confidence": {
                        "type": "number", "minimum": 0, "maximum": 1
                    },
                    "avatar_bbox": {
                        "anyOf": [
                            {"type": "null"},
                            {
                                "type": "object",
                                "additionalProperties": False,
                                "properties": {
                                    "x": {"type": "integer", "minimum": 0, "maximum": 1000},
                                    "y": {"type": "integer", "minimum": 0, "maximum": 1000},
                                    "width": {"type": "integer", "minimum": 1, "maximum": 1000},
                                    "height": {"type": "integer", "minimum": 1, "maximum": 1000}
                                },
                                "required": ["x", "y", "width", "height"]
                            }
                        ]
                    },
                },
                "required": [
                    "rank", "player_id", "raw_name", "points", "extraction_confidence", "avatar_bbox"
                ],
            },
        },
        "pinned_row": {
            "anyOf": [
                {"type": "null"},
                {
                    "type": "object",
                    "additionalProperties": False,
                    "properties": {
                        "rank": {"type": "integer", "minimum": 1},
                        "player_id": {
                            "anyOf": [
                                {"type": "integer", "minimum": 1},
                                {"type": "null"},
                            ]
                        },
                        "raw_name": {"type": "string", "minLength": 1},
                        "points": {"type": "integer", "minimum": 0},
                        "extraction_confidence": {
                            "type": "number", "minimum": 0, "maximum": 1
                        },
                        "avatar_bbox": {
                            "anyOf": [
                                {"type": "null"},
                                {
                                    "type": "object",
                                    "additionalProperties": False,
                                    "properties": {
                                        "x": {"type": "integer", "minimum": 0, "maximum": 1000},
                                        "y": {"type": "integer", "minimum": 0, "maximum": 1000},
                                        "width": {"type": "integer", "minimum": 1, "maximum": 1000},
                                        "height": {"type": "integer", "minimum": 1, "maximum": 1000}
                                    },
                                    "required": ["x", "y", "width", "height"]
                                }
                            ]
                        },
                    },
                    "required": [
                        "rank", "player_id", "raw_name", "points", "extraction_confidence", "avatar_bbox"
                    ],
                },
            ]
        },
        "warnings": {"type": "array", "items": {"type": "string"}},
    },
    "required": [
        "detected_day", "day_confidence", "ui_language", "rows", "pinned_row", "warnings"
    ],
}


class AvatarBBox(BaseModel):
    x: int = Field(ge=0, le=1000)
    y: int = Field(ge=0, le=1000)
    width: int = Field(ge=1, le=1000)
    height: int = Field(ge=1, le=1000)

    def as_tuple(self) -> tuple[int, int, int, int]:
        return self.x, self.y, self.width, self.height


class ExtractedRow(BaseModel):
    rank: int = Field(ge=1)
    player_id: int | None = Field(default=None, ge=1)
    raw_name: str = Field(min_length=1)
    points: int = Field(ge=0)
    extraction_confidence: float = Field(ge=0, le=1)
    avatar_bbox: AvatarBBox | None = None

    @field_validator("raw_name")
    @classmethod
    def keep_name_nonblank(cls, value: str) -> str:
        if not value.strip():
            raise ValueError("raw_name may not be blank")
        return value


class ScreenshotExtraction(BaseModel):
    detected_day: str
    day_confidence: float = Field(ge=0, le=1)
    ui_language: str
    rows: list[ExtractedRow]
    pinned_row: ExtractedRow | None
    warnings: list[str]


@dataclass(slots=True)
class ExtractionResult:
    image_path: Path
    extraction: ScreenshotExtraction | None
    error: str | None = None


class RequestRateLimiter:
    """Thread-safe request-start limiter.

    A limit of 28 RPM is used by default to provide headroom under a 30 RPM cap.
    Each retry also consumes a slot.
    """

    def __init__(self, requests_per_minute: int = 28):
        if requests_per_minute < 1 or requests_per_minute > 30:
            raise ValueError("requests_per_minute must be between 1 and 30")
        self.requests_per_minute = requests_per_minute
        self.min_interval = 60.0 / requests_per_minute
        self._lock = threading.Lock()
        self._last_start = 0.0

    def wait(self, cancel_event: threading.Event | None = None) -> None:
        with self._lock:
            now = time.monotonic()
            delay = max(0.0, self.min_interval - (now - self._last_start))
            if delay:
                if cancel_event is None:
                    time.sleep(delay)
                else:
                    if cancel_event.wait(delay):
                        raise RuntimeError("Cancelled")
            self._last_start = time.monotonic()


def encode_image_as_data_url(path: Path) -> str:
    mime, _ = mimetypes.guess_type(path.name)
    if mime not in {"image/png", "image/jpeg", "image/webp", "image/gif"}:
        raise ValueError(f"Unsupported image type: {path}")
    data = base64.b64encode(path.read_bytes()).decode("ascii")
    return f"data:{mime};base64,{data}"


def make_client(api_key: str | None = None, base_url: str | None = None) -> OpenAI:
    resolved_base_url = base_url or os.getenv("OPENAI_BASE_URL", "https://api.openai.com/v1")
    key = api_key or os.getenv("OPENAI_API_KEY")
    if not key:
        key = "local" if _is_local_endpoint(resolved_base_url) else None
    if not key:
        raise RuntimeError("OPENAI_API_KEY is not set")
    # Disable SDK auto-retries so our rate limiter governs every request attempt.
    return OpenAI(api_key=key, base_url=resolved_base_url, max_retries=0)


def _is_local_endpoint(base_url: str) -> bool:
    return any(host in base_url.casefold() for host in ("localhost", "127.0.0.1", "::1"))


def _retry_delay(exc: Exception, attempt: int) -> float:
    # Respect Retry-After when exposed by the SDK response; otherwise exponential backoff.
    response = getattr(exc, "response", None)
    if response is not None:
        headers = getattr(response, "headers", {}) or {}
        retry_after = headers.get("retry-after") or headers.get("Retry-After")
        try:
            if retry_after is not None:
                return max(1.0, float(retry_after))
        except (TypeError, ValueError):
            pass
    return min(30.0, 2.0 ** attempt)


def _is_retryable(exc: Exception) -> bool:
    status = getattr(exc, "status_code", None)
    if status in {408, 409, 429, 500, 502, 503, 504}:
        return True
    name = exc.__class__.__name__.lower()
    return any(token in name for token in ("ratelimit", "timeout", "connection", "internalserver"))


def extract_one(
    client: OpenAI,
    model: str,
    image_path: Path,
    limiter: RequestRateLimiter,
    cancel_event: threading.Event | None = None,
    max_attempts: int = 4,
    api_style: str = "responses",
) -> ScreenshotExtraction:
    image_url = encode_image_as_data_url(image_path)

    last_exc: Exception | None = None
    for attempt in range(max_attempts):
        if cancel_event is not None and cancel_event.is_set():
            raise RuntimeError("Cancelled")
        limiter.wait(cancel_event)
        try:
            if api_style == "chat":
                response = client.chat.completions.create(
                    model=model,
                    messages=[{"role": "user", "content": [
                        {"type": "text", "text": PROMPT},
                        {"type": "image_url", "image_url": {"url": image_url, "detail": "high"}},
                    ]}],
                    response_format={"type": "json_schema", "json_schema": {
                        "name": "ranking_screenshot_extraction",
                        "strict": True,
                        "schema": SCHEMA,
                    }},
                )
                output_text = response.choices[0].message.content or ""
            elif api_style == "responses":
                response = client.responses.create(
                    model=model,
                    input=[{
                        "role": "user",
                        "content": [
                            {"type": "input_text", "text": PROMPT},
                            {"type": "input_image", "image_url": image_url, "detail": "high"},
                        ],
                    }],
                    text={"format": {
                        "type": "json_schema",
                        "name": "ranking_screenshot_extraction",
                        "strict": True,
                        "schema": SCHEMA,
                    }},
                )
                output_text = response.output_text
            else:
                raise ValueError("api_style must be 'responses' or 'chat'")
            try:
                payload = json.loads(output_text)
            except json.JSONDecodeError as exc:
                raise RuntimeError(
                    f"Model returned non-JSON output for {image_path.name}: "
                    f"{output_text[:500]!r}"
                ) from exc
            try:
                return ScreenshotExtraction.model_validate(payload)
            except ValidationError as exc:
                raise RuntimeError(
                    f"Local validation failed for {image_path.name}:\n{exc}"
                ) from exc
        except Exception as exc:  # SDK error subclasses vary by compatible endpoint.
            last_exc = exc
            if attempt >= max_attempts - 1 or not _is_retryable(exc):
                raise
            delay = _retry_delay(exc, attempt + 1)
            if cancel_event is None:
                time.sleep(delay)
            elif cancel_event.wait(delay):
                raise RuntimeError("Cancelled") from exc

    assert last_exc is not None
    raise last_exc


def basic_sanity_checks(result: ScreenshotExtraction) -> list[str]:
    warnings: list[str] = []
    ranks = [row.rank for row in result.rows]
    if len(ranks) != len(set(ranks)):
        warnings.append("Duplicate leaderboard ranks detected in the same screenshot.")
    if ranks and any(b <= a for a, b in zip(ranks, ranks[1:])):
        warnings.append("Leaderboard ranks are not strictly increasing.")

    seen_ids: set[int] = set()
    for row in result.rows:
        if row.player_id is not None:
            if row.player_id in seen_ids:
                warnings.append(f"Player ID {row.player_id} appears more than once in the screenshot.")
            seen_ids.add(row.player_id)
    return warnings


def extract_many(
    image_paths: list[Path],
    model: str,
    base_url: str | None = None,
    api_key: str | None = None,
    requests_per_minute: int = 28,
    progress: Callable[[int, int, ExtractionResult], None] | None = None,
    cancel_event: threading.Event | None = None,
    api_style: str = "responses",
) -> list[ExtractionResult]:
    client = make_client(api_key=api_key, base_url=base_url)
    limiter = RequestRateLimiter(requests_per_minute)
    results: list[ExtractionResult] = []

    def run_attempt_once(path: Path) -> ExtractionResult:
        try:
            extraction = extract_one(client, model, path, limiter, cancel_event=cancel_event, api_style=api_style)
            return ExtractionResult(path, extraction, None)
        except Exception as exc:
            try:
                extraction = extract_one(client, model, path, limiter, cancel_event=cancel_event, api_style=api_style)
                return ExtractionResult(path, extraction, None)
            except Exception as retry_exc:
                return ExtractionResult(path, None, f"{exc}\nRetry failed: {retry_exc}")

    for index, path in enumerate(image_paths, start=1):
        if cancel_event is not None and cancel_event.is_set():
            break
        result = run_attempt_once(path)
        results.append(result)
        if progress:
            progress(index, len(image_paths), result)
    return results
