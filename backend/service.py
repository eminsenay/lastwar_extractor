from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import sys
import threading
from typing import Any, Callable

from avatars import AvatarStore
from excel_export import export_weekly_workbook
from extractor import ExtractionResult, ScreenshotExtraction, extract_many, _is_local_endpoint
from matcher import MemberMatcher, Observation, observations_from_extractions, WeeklyData, build_weekly_data
from members import Member, MemberLoadResult, load_members_from_google_sheet, load_members_from_xlsx
from storage import AliasStore, ExtractionCache


SUPPORTED_IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp", ".gif"}
PROMPT_CACHE_VERSION = "weekly-extractor-v4-day-confidence-player-line-2026-08-27"
DEFAULT_MODEL = "gpt-5.6-terra"
DEFAULT_BASE_URL = "https://api.openai.com/v1"


def _coerce_rpm(value: Any, fallback: int) -> int:
    try:
        return int(str(value).strip())
    except (TypeError, ValueError):
        return fallback


def _env_config() -> dict[str, Any]:
    """Defaults from the environment/.env, matching the legacy desktop app."""
    api_style = os.getenv("OPENAI_API_STYLE", "responses").strip().casefold()
    return {
        "model": os.getenv("OPENAI_MODEL", "").strip() or DEFAULT_MODEL,
        "baseUrl": os.getenv("OPENAI_BASE_URL", "").strip() or DEFAULT_BASE_URL,
        "apiStyle": api_style if api_style in {"responses", "chat"} else "responses",
        "requestsPerMinute": _coerce_rpm(os.getenv("OPENAI_RPM"), 28),
    }


def _mask_secret(value: str) -> str:
    if len(value) <= 8:
        return "*" * len(value)
    return f"{value[:4]}{'*' * (len(value) - 8)}{value[-4:]}"


@dataclass(slots=True)
class WorkflowState:
    members: list[Member] = field(default_factory=list)
    member_source: str = ""
    member_warnings: list[str] = field(default_factory=list)
    screenshot_paths: list[Path] = field(default_factory=list)
    extraction_results: list[ExtractionResult] = field(default_factory=list)
    observations: list[Observation] = field(default_factory=list)
    base_issues: list[str] = field(default_factory=list)


class WorkflowService:
    """UI-neutral workflow state shared by desktop frontends."""

    def __init__(self, app_dir: Path | None = None):
        resolved_app_dir = app_dir or (Path.home() / ".lastwar_weekly_extractor")
        self.alias_store = AliasStore(resolved_app_dir / "app.sqlite3")
        self.avatar_store = AvatarStore(resolved_app_dir / "app.sqlite3")
        self.extraction_cache = ExtractionCache(resolved_app_dir / "app.sqlite3")
        self.config_path = resolved_app_dir / "config.json"
        self.state = WorkflowState()
        self.matcher: MemberMatcher | None = None
        self.model = DEFAULT_MODEL
        self.base_url = DEFAULT_BASE_URL
        self.api_style = "responses"
        self.requests_per_minute = 28
        self.use_cache = True
        self.roster_source_type = "xlsx"
        self.roster_xlsx_path = ""
        self.roster_google_sheet_url = ""
        self.roster_sheet_name = "Members"
        self._apply_config({**_env_config(), **self._load_saved_config()})
        self._extraction_thread: threading.Thread | None = None
        self._cancel_event: threading.Event | None = None
        self._lock = threading.RLock()

    @property
    def extracting(self) -> bool:
        return self._extraction_thread is not None and self._extraction_thread.is_alive()

    def _load_saved_config(self) -> dict[str, Any]:
        try:
            saved = json.loads(self.config_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return {}
        return saved if isinstance(saved, dict) else {}

    def _save_config(self) -> None:
        self.config_path.parent.mkdir(parents=True, exist_ok=True)
        self.config_path.write_text(
            json.dumps({
                "model": self.model,
                "baseUrl": self.base_url,
                "apiStyle": self.api_style,
                "requestsPerMinute": self.requests_per_minute,
                "useCache": self.use_cache,
                "rosterSourceType": self.roster_source_type,
                "rosterXlsxPath": self.roster_xlsx_path,
                "rosterGoogleSheetUrl": self.roster_google_sheet_url,
                "rosterSheetName": self.roster_sheet_name,
            }, indent=2),
            encoding="utf-8",
        )

    def _apply_config(self, payload: dict[str, Any]) -> None:
        model = str(payload.get("model", self.model)).strip()
        base_url = str(payload.get("baseUrl", self.base_url)).strip()
        api_style = str(payload.get("apiStyle", self.api_style)).strip().casefold()
        rpm = _coerce_rpm(payload.get("requestsPerMinute"), self.requests_per_minute)
        if not model or not base_url:
            raise ValueError("model and baseUrl are required")
        if api_style not in {"responses", "chat"}:
            raise ValueError("apiStyle must be 'responses' or 'chat'")
        if not 1 <= rpm <= 30:
            raise ValueError("requestsPerMinute must be between 1 and 30")
        self.model, self.base_url, self.api_style = model, base_url, api_style
        self.requests_per_minute = rpm
        self.use_cache = bool(payload.get("useCache", self.use_cache))

        roster_source_type = str(payload.get("rosterSourceType", self.roster_source_type)).strip().casefold()
        if roster_source_type in {"xlsx", "google_sheet"}:
            self.roster_source_type = roster_source_type
        if "rosterXlsxPath" in payload:
            self.roster_xlsx_path = str(payload.get("rosterXlsxPath", self.roster_xlsx_path)).strip()
        if "rosterGoogleSheetUrl" in payload:
            self.roster_google_sheet_url = str(payload.get("rosterGoogleSheetUrl", self.roster_google_sheet_url)).strip()
        if "rosterSheetName" in payload:
            self.roster_sheet_name = str(payload.get("rosterSheetName", self.roster_sheet_name)).strip() or "Members"

    def set_config(self, payload: dict[str, Any]) -> dict[str, Any]:
        if self.extracting:
            raise RuntimeError("Cannot change configuration during extraction")
        self._apply_config(payload)
        self._save_config()
        return self.snapshot()

    def load_members(self, source_type: str, source: str, sheet_name: str = "Members") -> dict[str, Any]:
        source = str(source).strip()
        sheet_name = str(sheet_name).strip() or "Members"
        if source_type == "xlsx":
            result = load_members_from_xlsx(Path(source), sheet_name)
            self.roster_source_type = "xlsx"
            self.roster_xlsx_path = source
            self.roster_sheet_name = sheet_name
        elif source_type == "google_sheet":
            result = load_members_from_google_sheet(source, sheet_name)
            self.roster_source_type = "google_sheet"
            self.roster_google_sheet_url = source
            self.roster_sheet_name = sheet_name
        else:
            raise ValueError("source_type must be 'xlsx' or 'google_sheet'")
        self._apply_member_result(result)
        self._save_config()
        return self.snapshot()

    def add_screenshots(self, raw_paths: list[str]) -> dict[str, Any]:
        collected = list(self.state.screenshot_paths)
        for raw_path in raw_paths:
            path = Path(raw_path).expanduser().resolve()
            if path.is_dir():
                collected.extend(
                    child for child in sorted(path.iterdir())
                    if child.is_file() and child.suffix.casefold() in SUPPORTED_IMAGE_EXTENSIONS
                )
            elif path.is_file() and path.suffix.casefold() in SUPPORTED_IMAGE_EXTENSIONS:
                collected.append(path)
            else:
                raise ValueError(f"Unsupported or missing screenshot path: {raw_path}")
        self.state.screenshot_paths = list(dict.fromkeys(collected))
        return self.snapshot()

    def clear_screenshots(self) -> dict[str, Any]:
        if self.extracting:
            raise RuntimeError("Cancel extraction before clearing screenshots")
        self.state.screenshot_paths.clear()
        self.state.extraction_results.clear()
        self.state.observations.clear()
        self.state.base_issues.clear()
        return self.snapshot()

    def start_extraction(self, operation_id: str, emit: Callable[[dict[str, Any]], None]) -> None:
        if self.extracting:
            raise RuntimeError("An extraction is already running")
        if not self.state.members:
            raise ValueError("Load members before extracting screenshots")
        if not self.state.screenshot_paths:
            raise ValueError("Add screenshots before extracting")

        paths = list(self.state.screenshot_paths)
        self.state.extraction_results = []
        self.state.observations = []
        self.state.base_issues = []
        self._cancel_event = threading.Event()
        self._extraction_thread = threading.Thread(
            target=self._extract_worker,
            args=(operation_id, paths, emit, self._cancel_event),
            daemon=True,
            name="lastwar-extraction",
        )
        self._extraction_thread.start()

    def cancel_extraction(self) -> None:
        if self._cancel_event is not None:
            self._cancel_event.set()

    def assign_observation(
        self, observation_id: str, member_id: int, remember_alias: bool = True
    ) -> dict[str, Any]:
        if self.matcher is None:
            raise ValueError("Load members before assigning observations")
        for index, observation in enumerate(self.state.observations):
            if _observation_id(index, observation) == observation_id:
                self.matcher.manual_assign(observation, member_id, remember_alias)
                return self.snapshot()
        raise KeyError(f"Unknown observation: {observation_id}")

    def export(self, output_path: str) -> dict[str, Any]:
        """Export workflow state to Excel workbook."""
        if not self.state.members:
            raise ValueError("Load members before exporting")
        if not self.state.observations:
            raise ValueError("Extract screenshots before exporting")
        path = Path(output_path).expanduser().resolve()
        if path.suffix.casefold() != ".xlsx":
            path = path.with_suffix(".xlsx")
        path.parent.mkdir(parents=True, exist_ok=True)
        weekly = build_weekly_data(self.state.observations, self.state.members, self.state.base_issues)
        export_weekly_workbook(path, self.state.members, weekly, self.alias_store, self.state.member_source)
        return {"path": str(path), "message": f"Exported to {path.name}"}

    def _extract_worker(
        self,
        operation_id: str,
        paths: list[Path],
        emit: Callable[[dict[str, Any]], None],
        cancel_event: threading.Event,
    ) -> None:
        try:
            cached: dict[Path, ExtractionResult] = {}
            uncached: list[Path] = []
            for path in paths:
                raw = self.extraction_cache.get(self._cache_key(path)) if self.use_cache else None
                if raw:
                    try:
                        extraction = ScreenshotExtraction.model_validate_json(raw)
                        cached[path] = ExtractionResult(path, extraction, None)
                    except Exception:
                        uncached.append(path)
                else:
                    uncached.append(path)

            completed = 0
            for path in paths:
                if path in cached:
                    completed += 1
                    emit({
                        "type": "event", "event": "extraction_progress", "operationId": operation_id,
                        "payload": {"completed": completed, "total": len(paths), "path": str(path), "cached": True},
                    })

            fresh_by_path: dict[Path, ExtractionResult] = {}

            def progress(done: int, total: int, result: ExtractionResult) -> None:
                nonlocal completed
                completed += 1
                fresh_by_path[result.image_path] = result
                if result.extraction and not result.error:
                    self.extraction_cache.put(
                        self._cache_key(result.image_path), result.image_path.name,
                        result.extraction.model_dump_json(),
                    )
                emit({
                    "type": "event", "event": "extraction_progress", "operationId": operation_id,
                    "payload": {
                        "completed": completed, "total": len(paths), "path": str(result.image_path),
                        "cached": False, "error": result.error,
                        "detectedDay": result.extraction.detected_day if result.extraction else None,
                        "rowCount": len(result.extraction.rows) if result.extraction else 0,
                    },
                })

            if uncached and not cancel_event.is_set():
                extract_many(
                    uncached, model=self.model, base_url=self.base_url, api_style=self.api_style,
                    requests_per_minute=self.requests_per_minute, progress=progress,
                    cancel_event=cancel_event,
                )
            self.state.extraction_results = [cached.get(path) or fresh_by_path[path] for path in paths if path in cached or path in fresh_by_path]
            self._rebuild_matches()
            event_name = "cancelled" if cancel_event.is_set() else "extraction_finished"
            emit({"type": "event", "event": event_name, "operationId": operation_id, "payload": self.snapshot()})
        except Exception as exc:
            encoded = (f"extraction error: {exc}\n").encode("utf-8", errors="backslashreplace")
            stream = getattr(sys.stderr, "buffer", None)
            if stream is not None:
                stream.write(encoded)
                stream.flush()
            else:
                sys.stderr.write(encoded.decode("ascii"))
                sys.stderr.flush()
            emit({
                "type": "event", "event": "error", "operationId": operation_id,
                "payload": {"code": "extraction_failed", "message": str(exc)},
            })
        finally:
            self._extraction_thread = None
            self._cancel_event = None

    def _cache_key(self, path: Path) -> str:
        digest = hashlib.sha256()
        digest.update(path.read_bytes())
        digest.update(self.model.encode("utf-8"))
        digest.update(self.base_url.encode("utf-8"))
        digest.update(self.api_style.encode("ascii"))
        digest.update(PROMPT_CACHE_VERSION.encode("ascii"))
        return digest.hexdigest()

    def snapshot(self) -> dict[str, Any]:
        avatar_members, avatar_samples = self.avatar_store.stats()
        return {
            "config": self._config_snapshot(),
            "members": [_member_to_dict(member) for member in self.state.members],
            "memberSource": self.state.member_source,
            "memberWarnings": list(self.state.member_warnings),
            "screenshots": [str(path) for path in self.state.screenshot_paths],
            "extractions": [_extraction_result_to_dict(result) for result in self.state.extraction_results],
            "observations": [
                _observation_to_dict(index, observation)
                for index, observation in enumerate(self.state.observations)
            ],
            "issues": list(self.state.base_issues),
            "summary": {
                "memberCount": len(self.state.members),
                "screenshotCount": len(self.state.screenshot_paths),
                "observationCount": len(self.state.observations),
                "unmatchedCount": sum(o.matched_member_id is None for o in self.state.observations),
                "failedFileCount": sum(r.error is not None for r in self.state.extraction_results),
                "avatarMemberCount": avatar_members,
                "avatarSampleCount": avatar_samples,
            },
        }

    def _config_snapshot(self) -> dict[str, Any]:
        api_key = os.getenv("OPENAI_API_KEY", "").strip()
        return {
            "model": self.model,
            "baseUrl": self.base_url,
            "apiStyle": self.api_style,
            "requestsPerMinute": self.requests_per_minute,
            "useCache": self.use_cache,
            "apiKeyPresent": bool(api_key),
            "apiKeyHint": _mask_secret(api_key) if api_key else "",
            "apiKeyRequired": not _is_local_endpoint(self.base_url),
            "rosterSourceType": self.roster_source_type,
            "rosterXlsxPath": self.roster_xlsx_path,
            "rosterGoogleSheetUrl": self.roster_google_sheet_url,
            "rosterSheetName": self.roster_sheet_name,
        }

    def _apply_member_result(self, result: MemberLoadResult) -> None:
        self.state.members = result.members
        self.state.member_source = result.source_description
        self.state.member_warnings = result.warnings
        self.matcher = MemberMatcher(self.state.members, self.alias_store, self.avatar_store)
        if self.state.extraction_results:
            self._rebuild_matches()

    def _rebuild_matches(self) -> None:
        if self.matcher is None:
            return
        self.state.observations, self.state.base_issues = observations_from_extractions(
            self.state.extraction_results
        )
        for observation in self.state.observations:
            self.matcher.match_deterministic(observation)
        for observation in self.state.observations:
            if observation.matched_member_id is not None:
                self.matcher.learn_avatar(observation)
        for observation in self.state.observations:
            if observation.matched_member_id is None:
                self.matcher.match_avatar(observation)


def _member_to_dict(member: Member) -> dict[str, Any]:
    return {
        "id": member.member_id,
        "name": member.name,
        "rank": member.rank,
        "joinedOn": member.joined_on.isoformat() if isinstance(member.joined_on, datetime) else None,
        "totalHeroPower": member.total_hero_power,
    }


def _extraction_result_to_dict(result: ExtractionResult) -> dict[str, Any]:
    return {
        "path": str(result.image_path),
        "error": result.error,
        "extraction": result.extraction.model_dump(mode="json") if result.extraction else None,
    }


def _observation_to_dict(index: int, observation: Observation) -> dict[str, Any]:
    return {
    "id": _observation_id(index, observation),
        "sourceFile": observation.source_file,
        "day": observation.day,
        "rank": observation.rank,
        "rawPlayerId": observation.raw_player_id,
        "rawName": observation.raw_name,
        "points": observation.points,
        "extractionConfidence": observation.extraction_confidence,
        "isPinnedRow": observation.is_pinned_row,
        "matchedMemberId": observation.matched_member_id,
        "matchedMemberName": observation.matched_member_name,
        "matchMethod": observation.match_method,
        "matchConfidence": observation.match_confidence,
        "issue": observation.issue,
        "alternatives": [
            {"memberId": member_id, "name": name, "score": score}
            for member_id, name, score in observation.alternatives
        ],
    }


def _observation_id(index: int, observation: Observation) -> str:
    identity = "|".join([
        observation.source_file,
        observation.day,
        str(observation.rank),
        str(observation.is_pinned_row),
        str(observation.raw_player_id or ""),
        observation.raw_name,
    ])
    digest = hashlib.sha256(identity.encode("utf-8")).hexdigest()[:16]
    return f"observation-{digest}-{index}"
