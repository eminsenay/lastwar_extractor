from __future__ import annotations

import unicodedata
from dataclasses import dataclass, field
from difflib import SequenceMatcher
from pathlib import Path
from typing import Any, Iterable

from avatars import AvatarStore, fingerprint_from_bbox
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
    avatar_bbox: tuple[int, int, int, int] | None = None
    avatar_fingerprint: str | None = None
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
    """Match extracted observations to active members.

    Matching happens in two phases:
      1. deterministic identity signals: visible ID, exact name, saved alias
      2. avatar-assisted matching for unresolved observations

    Avatar auto-matches are intentionally conservative. Lower-confidence avatar
    matches are shown as suggestions and still require manual confirmation.
    """

    def __init__(
        self,
        members: Iterable[Member],
        alias_store: AliasStore,
        avatar_store: AvatarStore | None = None,
        avatar_auto_threshold: float = 0.92,
        avatar_min_margin: float = 0.06,
        avatar_suggestion_threshold: float = 0.78,
    ):
        self.members = list(members)
        self.alias_store = alias_store
        self.avatar_store = avatar_store
        self.avatar_auto_threshold = avatar_auto_threshold
        self.avatar_min_margin = avatar_min_margin
        self.avatar_suggestion_threshold = avatar_suggestion_threshold

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
        obs.issue = None
        return obs

    def match_deterministic(self, obs: Observation) -> Observation:
        """Apply only trusted ID/name/alias rules. Does not use avatar similarity."""
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

        self._set_fuzzy_suggestions(obs)
        obs.issue = "No deterministic ID/name/alias match."
        return obs

    def _set_fuzzy_suggestions(self, obs: Observation) -> None:
        norm = normalize_name(obs.raw_name)
        fuzzy: list[tuple[int, str, float]] = []
        if norm:
            scored: list[tuple[float, int]] = []
            for member_id, candidate in self.name_choices.items():
                score = SequenceMatcher(None, norm, candidate).ratio()
                scored.append((score, member_id))
            scored.sort(reverse=True)
            for score, member_id in scored[:3]:
                member = self.by_id.get(int(member_id), [None])[0]
                if member is not None:
                    fuzzy.append((member.member_id, member.name, score))
        obs.alternatives = fuzzy

    def learn_avatar(self, obs: Observation) -> bool:
        """Persist an avatar reference only from an already trusted identity match."""
        if (
            self.avatar_store is None
            or obs.matched_member_id is None
            or not obs.avatar_fingerprint
            or obs.match_method not in {"id", "exact_name", "saved_alias", "manual", "avatar_auto"}
        ):
            return False
        return self.avatar_store.save_reference(
            obs.matched_member_id,
            obs.avatar_fingerprint,
            source_file=obs.source_file,
            match_method=obs.match_method,
        )

    def match_avatar(self, obs: Observation) -> Observation:
        """Use stored avatar fingerprints to resolve or suggest an unmatched member."""
        if obs.matched_member_id is not None or self.avatar_store is None or not obs.avatar_fingerprint:
            return obs

        matches = self.avatar_store.best_matches(
            obs.avatar_fingerprint,
            member_ids=self.by_id.keys(),
            limit=5,
        )
        if not matches:
            return obs

        best = matches[0]
        second_score = matches[1].score if len(matches) > 1 else 0.0
        margin = best.score - second_score
        member_candidates = self.by_id.get(best.member_id, [])

        if (
            len(member_candidates) == 1
            and best.score >= self.avatar_auto_threshold
            and margin >= self.avatar_min_margin
        ):
            member = member_candidates[0]
            self._set_match(obs, member, "avatar_auto", best.score)
            # The new observation is another valid reference for that identity.
            self.learn_avatar(obs)
            return obs

        avatar_alternatives: list[tuple[int, str, float]] = []
        for match in matches:
            if match.score < self.avatar_suggestion_threshold:
                continue
            members = self.by_id.get(match.member_id, [])
            if len(members) == 1:
                avatar_alternatives.append((match.member_id, members[0].name, match.score))

        # Merge avatar and name suggestions, keeping the strongest score per member.
        merged: dict[int, tuple[str, float]] = {
            member_id: (name, score) for member_id, name, score in obs.alternatives
        }
        for member_id, name, score in avatar_alternatives:
            current = merged.get(member_id)
            if current is None or score > current[1]:
                merged[member_id] = (name, score)
        obs.alternatives = sorted(
            [(mid, name, score) for mid, (name, score) in merged.items()],
            key=lambda x: x[2],
            reverse=True,
        )[:5]

        if best.score >= self.avatar_suggestion_threshold:
            obs.issue = (
                f"Avatar suggests member {best.member_id} at {best.score:.0%} "
                f"(margin {margin:.0%}); manual review required."
            )
        return obs

    def match(self, obs: Observation) -> Observation:
        """Convenience single-observation matching.

        For a weekly batch, prefer the two-pass pattern used by the desktop app:
        deterministic matches -> learn avatar references -> avatar matching.
        """
        self.match_deterministic(obs)
        if obs.matched_member_id is not None:
            self.learn_avatar(obs)
            return obs
        return self.match_avatar(obs)

    def manual_assign(self, obs: Observation, member_id: int, remember_alias: bool = True) -> Observation:
        candidates = self.by_id.get(member_id, [])
        if len(candidates) != 1:
            raise ValueError(f"Member ID {member_id} is not uniquely active")
        member = candidates[0]
        self._set_match(obs, member, "manual", 1.0)
        if remember_alias:
            norm = normalize_name(obs.raw_name)
            if norm:
                self.alias_store.save_alias(norm, obs.raw_name, member_id)
        self.learn_avatar(obs)
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
            obs, avatar_issue = _make_observation(result.image_path, extraction.detected_day, row, False)
            observations.append(obs)
            if avatar_issue:
                issues.append(avatar_issue)
        if extraction.pinned_row is not None:
            obs, avatar_issue = _make_observation(
                result.image_path, extraction.detected_day, extraction.pinned_row, True
            )
            observations.append(obs)
            if avatar_issue:
                issues.append(avatar_issue)
        for warning in extraction.warnings:
            issues.append(f"{result.image_path.name}: model warning: {warning}")
    return observations, issues


def _make_observation(path: Path, day: str, row: Any, pinned: bool) -> tuple[Observation, str | None]:
    bbox_obj = getattr(row, "avatar_bbox", None)
    bbox: tuple[int, int, int, int] | None = None
    if bbox_obj is not None:
        if hasattr(bbox_obj, "as_tuple"):
            bbox = bbox_obj.as_tuple()
        elif isinstance(bbox_obj, dict):
            try:
                bbox = (
                    int(bbox_obj["x"]), int(bbox_obj["y"]),
                    int(bbox_obj["width"]), int(bbox_obj["height"]),
                )
            except Exception:
                bbox = None

    avatar_fingerprint = fingerprint_from_bbox(path, bbox)
    avatar_issue = None
    if bbox is not None and avatar_fingerprint is None:
        avatar_issue = f"{path.name}: could not fingerprint avatar for {row.raw_name!r} at rank {row.rank}."

    return Observation(
        source_file=path.name,
        day=day,
        rank=row.rank,
        raw_player_id=row.player_id,
        raw_name=row.raw_name,
        points=row.points,
        extraction_confidence=row.extraction_confidence,
        is_pinned_row=pinned,
        avatar_bbox=bbox,
        avatar_fingerprint=avatar_fingerprint,
    ), avatar_issue


def build_weekly_data(
    observations: list[Observation],
    members: list[Member],
    base_issues: list[str] | None = None,
) -> WeeklyData:
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

    observed_days = sorted(
        {o.day for o in observations},
        key=lambda d: DAY_ORDER.index(d) if d in DAY_ORDER else 99,
    )
    missing_by_day: dict[str, list[Member]] = {}
    for day in observed_days:
        matched_ids = {member_id for member_id, d in scores if d == day}
        missing_by_day[day] = [m for m in members if m.member_id not in matched_ids]

    return WeeklyData(observations, scores, issues, missing_by_day)
