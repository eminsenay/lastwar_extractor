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
   IMPORTANT: day_confidence is a confidence/probability from 0.0 to 1.0.
   It is NOT the weekday position/number. For example, for Saturday return
   detected_day="saturday" and day_confidence=1.0 (or another value <= 1.0), NEVER 6.
4. Extract every sufficiently visible ranking row in the main scrolling list.
5. Each leaderboard entry has TWO text lines beside the avatar:
   - FIRST / UPPER line: the PLAYER NAME (and, on some screenshots, a numeric player ID before it).
   - SECOND / LOWER line: the ALLIANCE / ROLE text, e.g. "[EfC] Elite Force Commander".
   These are different fields. NEVER return the second/lower alliance line as raw_name.
   For each row extract:
   - rank
   - player ID only if it is explicitly visible before/near the player name on the FIRST line
   - raw_name: ONLY the player name from the FIRST / UPPER line, exactly as visible
   - alliance_name: ONLY the SECOND / LOWER alliance or role line, exactly as visible; null if absent/unreadable
   - points as an integer with separators removed
   - avatar_bbox: the tight bounding box of the player's square/circular avatar,
     using normalized screenshot coordinates from 0 to 1000 for x, y, width, height.
     Return null only when the avatar is not sufficiently visible.
6. A highlighted/pinned alliance/self row may appear separately at the bottom.
   Extract it into pinned_row and DO NOT include it again in rows.
7. Never infer a missing player ID from rank, avatar, name, or prior knowledge.
8. Preserve Unicode characters in player names.
9. Do not translate player names.
10. Do not confuse alliance/role text with the player name. Capture the second line in alliance_name,
    but NEVER copy it into raw_name.
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
- raw_name must come from the FIRST / UPPER text line next to the avatar.
- alliance_name must come from the SECOND / LOWER text line next to the avatar.
- If the visible text is "Player123" on the first line and "[EfC] Elite Force Commander" on the second,
  raw_name MUST be "Player123" and alliance_name MUST be "[EfC] Elite Force Commander".
- day_confidence MUST be between 0.0 and 1.0 and MUST NEVER contain the weekday index (1-6).
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
        "day_confidence": {
            "type": "number", "minimum": 0, "maximum": 6,
            "description": "Confidence probability should be 0.0 to 1.0. Values 2-6 are accepted only for compatibility with local models that mistakenly emit the weekday index; the app normalizes them before validation."
        },
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
                    "raw_name": {
                        "type": "string", "minLength": 1,
                        "description": "PLAYER NAME from the FIRST/UPPER text line beside the avatar. Never the alliance/role line."
                    },
                    "alliance_name": {
                        "anyOf": [{"type": "string"}, {"type": "null"}],
                        "description": "Alliance/role text from the SECOND/LOWER line, e.g. [EfC] Elite Force Commander; null if absent/unreadable."
                    },
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
                    "rank", "player_id", "raw_name", "alliance_name", "points", "extraction_confidence", "avatar_bbox"
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
                        "raw_name": {
                            "type": "string", "minLength": 1,
                            "description": "PLAYER NAME from the FIRST/UPPER text line beside the avatar. Never the alliance/role line."
                        },
                        "alliance_name": {
                            "anyOf": [{"type": "string"}, {"type": "null"}],
                            "description": "Alliance/role text from the SECOND/LOWER line; null if absent/unreadable."
                        },
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
                        "rank", "player_id", "raw_name", "alliance_name", "points", "extraction_confidence", "avatar_bbox"
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
    alliance_name: str | None = None
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


DAY_INDEX = {
    "monday": 1,
    "tuesday": 2,
    "wednesday": 3,
    "thursday": 4,
    "friday": 5,
    "saturday": 6,
}


def _looks_like_alliance_text(value: Any) -> bool:
    if not isinstance(value, str):
        return False
    text = " ".join(value.casefold().split())
    return (
        "elite force commander" in text
        or "alliance" in text
        or (text.startswith("[") and "]" in text)
    )


def _sanitize_model_payload(payload: dict[str, Any]) -> dict[str, Any]:
    """Repair common schema mistakes from OpenAI-compatible local models.

    Some local models confuse the weekday index with day_confidence (for example,
    returning 6 for Saturday), or swap the two visible text lines in a ranking row.
    Keep these repairs deterministic and record them as model warnings.
    """
    warnings = payload.get("warnings")
    if not isinstance(warnings, list):
        warnings = []
        payload["warnings"] = warnings

    # day_confidence must be a probability in [0, 1], never the 1-6 weekday index.
    raw_conf = payload.get("day_confidence")
    try:
        conf = float(raw_conf)
    except (TypeError, ValueError):
        conf = 0.0
        warnings.append(f"Invalid day_confidence {raw_conf!r}; normalized to 0.0.")
    else:
        if conf > 1.0:
            day = str(payload.get("detected_day", "")).casefold()
            expected_index = DAY_INDEX.get(day)
            if conf.is_integer() and 1 <= int(conf) <= 6:
                if expected_index == int(conf):
                    normalized = 1.0
                    reason = "weekday index matching detected_day"
                else:
                    normalized = 0.0
                    reason = "weekday index conflicting with detected_day"
                warnings.append(
                    f"day_confidence {raw_conf!r} looked like a {reason}; normalized to {normalized:.1f}."
                )
                conf = normalized
            else:
                warnings.append(f"day_confidence {raw_conf!r} was outside 0..1; clamped to 1.0.")
                conf = 1.0
        elif conf < 0.0:
            warnings.append(f"day_confidence {raw_conf!r} was below 0; clamped to 0.0.")
            conf = 0.0
    payload["day_confidence"] = conf

    def sanitize_row(row: Any, label: str) -> None:
        if not isinstance(row, dict):
            return
        # Backward/local-model compatibility: schema now asks for this explicitly,
        # but accepting missing alliance_name keeps slightly non-compliant models usable.
        row.setdefault("alliance_name", None)
        raw_name = row.get("raw_name")
        alliance_name = row.get("alliance_name")
        # If a model swapped the two lines, repair it when the swap is unambiguous.
        if _looks_like_alliance_text(raw_name) and isinstance(alliance_name, str) and not _looks_like_alliance_text(alliance_name):
            row["raw_name"], row["alliance_name"] = alliance_name, raw_name
            warnings.append(f"{label}: swapped player/alliance text lines returned by the model.")
        elif _looks_like_alliance_text(raw_name):
            warnings.append(
                f"{label}: raw_name still looks like alliance/role text; player name could not be recovered automatically."
            )

    for index, row in enumerate(payload.get("rows") or [], start=1):
        sanitize_row(row, f"row {index}")
    if payload.get("pinned_row") is not None:
        sanitize_row(payload["pinned_row"], "pinned_row")
    return payload


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
            payload = _sanitize_model_payload(payload)
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
        if _looks_like_alliance_text(row.raw_name):
            warnings.append(
                f"Rank {row.rank} raw_name still looks like alliance/role text: {row.raw_name!r}."
            )
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
