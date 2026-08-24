from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable

from difflib import SequenceMatcher
from typing import Any

from members import Member
from storage import AliasStore


DAY_ORDER = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday"]


def normalize_name(value: str) -> str:
    text = unicodedata.normalize("NFKC", value).casefold()
    text = text.replace("ı", "i")
    # Keep letters/numbers across scripts; remove decorative punctuation/spacing.
    text = "".join(ch for ch in text if ch.isalnum())
    return text


@dataclass(slots=True)
class Observation:
    source_file: str
    day: str
    rank: int
    raw_player_id: int | None
    raw_name: str
    points: int
    extraction_confidence: float
    is_pinned_row: bool
    matched_member_id: int | None = None
    matched_member_name: str | None = None
    match_method: str = "unmatched"
    match_confidence: float = 0.0
    issue: str | None = None
    alternatives: list[tuple[int, str, float]] = field(default_factory=list)


@dataclass(slots=True)
class WeeklyData:
    observations: list[Observation]
    scores: dict[tuple[int, str], int]
    issues: list[str]
    missing_by_day: dict[str, list[Member]]


class MemberMatcher:
    def __init__(self, members: Iterable[Member], alias_store: AliasStore):
        self.members = list(members)
        self.alias_store = alias_store
        self.by_id: dict[int, list[Member]] = {}
        for m in self.members:
            self.by_id.setdefault(m.member_id, []).append(m)
        self.by_norm: dict[str, list[Member]] = {}
        for m in self.members:
            self.by_norm.setdefault(normalize_name(m.name), []).append(m)
        self.name_choices = {m.member_id: normalize_name(m.name) for m in self.members}

    def _set_match(self, obs: Observation, member: Member, method: str, confidence: float) -> Observation:
        obs.matched_member_id = member.member_id
        obs.matched_member_name = member.name
        obs.match_method = method
        obs.match_confidence = confidence
        return obs

    def match(self, obs: Observation) -> Observation:
        if obs.raw_player_id is not None:
            candidates = self.by_id.get(obs.raw_player_id, [])
            if len(candidates) == 1:
                return self._set_match(obs, candidates[0], "id", 1.0)
            if len(candidates) > 1:
                obs.issue = f"Duplicate active member ID {obs.raw_player_id}; manual review required."
                return obs

        norm = normalize_name(obs.raw_name)
        exact = self.by_norm.get(norm, [])
        if len(exact) == 1:
            return self._set_match(obs, exact[0], "exact_name", 1.0)
        if len(exact) > 1:
            obs.issue = f"Normalized name {obs.raw_name!r} maps to multiple active members."
            return obs

        alias_member_id = self.alias_store.get_member_id(norm)
        if alias_member_id is not None:
            candidates = self.by_id.get(alias_member_id, [])
            if len(candidates) == 1:
                return self._set_match(obs, candidates[0], "saved_alias", 1.0)

        # Fuzzy matching is suggestion-only. It never auto-assigns an identity.
        if norm:
            scored: list[tuple[float, int]] = []
            for member_id, candidate in self.name_choices.items():
                score = SequenceMatcher(None, norm, candidate).ratio()
                scored.append((score, member_id))
            scored.sort(reverse=True)
            obs.alternatives = []
            for score, member_id in scored[:3]:
                member = self.by_id.get(int(member_id), [None])[0]
                if member is not None:
                    obs.alternatives.append((member.member_id, member.name, score))
        obs.issue = "No deterministic ID/name/alias match."
        return obs

    def manual_assign(self, obs: Observation, member_id: int, remember_alias: bool = True) -> Observation:
        candidates = self.by_id.get(member_id, [])
        if len(candidates) != 1:
            raise ValueError(f"Member ID {member_id} is not uniquely active")
        member = candidates[0]
        self._set_match(obs, member, "manual", 1.0)
        obs.issue = None
        if remember_alias:
            norm = normalize_name(obs.raw_name)
            if norm:
                self.alias_store.save_alias(norm, obs.raw_name, member_id)
        return obs


def observations_from_extractions(results: list[Any]) -> tuple[list[Observation], list[str]]:
    observations: list[Observation] = []
    issues: list[str] = []
    for result in results:
        if result.error:
            issues.append(f"{result.image_path.name}: extraction failed: {result.error}")
            continue
        assert result.extraction is not None
        extraction = result.extraction
        for row in extraction.rows:
            observations.append(_make_observation(result.image_path, extraction.detected_day, row, False))
        if extraction.pinned_row is not None:
            observations.append(_make_observation(result.image_path, extraction.detected_day, extraction.pinned_row, True))
        for warning in extraction.warnings:
            issues.append(f"{result.image_path.name}: model warning: {warning}")
    return observations, issues


def _make_observation(path: Path, day: str, row: Any, pinned: bool) -> Observation:
    return Observation(
        source_file=path.name,
        day=day,
        rank=row.rank,
        raw_player_id=row.player_id,
        raw_name=row.raw_name,
        points=row.points,
        extraction_confidence=row.extraction_confidence,
        is_pinned_row=pinned,
    )


def build_weekly_data(observations: list[Observation], members: list[Member], base_issues: list[str] | None = None) -> WeeklyData:
    issues = list(base_issues or [])
    scores: dict[tuple[int, str], int] = {}
    grouped: dict[tuple[int, str], list[Observation]] = {}

    for obs in observations:
        if obs.matched_member_id is None:
            issues.append(
                f"Unmatched: {obs.day} rank {obs.rank} {obs.raw_name!r} ({obs.points:,}) from {obs.source_file}"
            )
            continue
        grouped.setdefault((obs.matched_member_id, obs.day), []).append(obs)

    for key, group in grouped.items():
        values = sorted({o.points for o in group})
        # Scores appear cumulative. For duplicate observations collected minutes apart,
        # preserve the highest value and surface the disagreement for review/audit.
        scores[key] = max(values)
        if len(values) > 1:
            member_id, day = key
            issues.append(
                f"Conflicting duplicate scores for member {member_id} on {day}: "
                f"{', '.join(f'{v:,}' for v in values)}. Highest value used."
            )

    observed_days = sorted({o.day for o in observations}, key=lambda d: DAY_ORDER.index(d) if d in DAY_ORDER else 99)
    missing_by_day: dict[str, list[Member]] = {}
    for day in observed_days:
        matched_ids = {member_id for member_id, d in scores if d == day}
        missing_by_day[day] = [m for m in members if m.member_id not in matched_ids]

    return WeeklyData(observations, scores, issues, missing_by_day)
