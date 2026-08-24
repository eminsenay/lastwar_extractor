from __future__ import annotations

import base64
import json
import sqlite3
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

import cv2
import numpy as np
from PIL import Image, ImageOps


# Multiple center-crop scales make the hash less sensitive to decorative avatar
# frames and small bounding-box errors. ORB features provide the stronger signal
# across screenshots with different UI scale/resolution.
CROP_SCALES = (0.60, 0.72, 0.84)
HASH_WIDTH = 17
HASH_HEIGHT = 16
HASH_BITS = (HASH_WIDTH - 1) * HASH_HEIGHT
ORB_SIZE = 160
ORB_FEATURES = 300


@dataclass(frozen=True, slots=True)
class AvatarMatch:
    member_id: int
    score: float
    samples: int


def _center_crop(image: Image.Image, fraction: float) -> Image.Image:
    width, height = image.size
    crop_w = max(1, int(width * fraction))
    crop_h = max(1, int(height * fraction))
    left = max(0, (width - crop_w) // 2)
    top = max(0, (height - crop_h) // 2)
    return image.crop((left, top, left + crop_w, top + crop_h))


def _dhash(image: Image.Image) -> int:
    gray = ImageOps.grayscale(image).resize((HASH_WIDTH, HASH_HEIGHT), Image.Resampling.LANCZOS)
    pixels = list(gray.getdata())
    value = 0
    bit = 0
    for y in range(HASH_HEIGHT):
        row = y * HASH_WIDTH
        for x in range(HASH_WIDTH - 1):
            if pixels[row + x] > pixels[row + x + 1]:
                value |= 1 << bit
            bit += 1
    return value


def _orb_descriptors(image: Image.Image) -> np.ndarray | None:
    # Trim the outer frame before feature detection. Avatar frame cosmetics change
    # more often than the central profile image itself.
    inner = _center_crop(image, 0.76).resize((ORB_SIZE, ORB_SIZE), Image.Resampling.LANCZOS)
    gray = np.asarray(ImageOps.grayscale(inner), dtype=np.uint8)
    orb = cv2.ORB_create(nfeatures=ORB_FEATURES, fastThreshold=5)
    _keypoints, descriptors = orb.detectAndCompute(gray, None)
    return descriptors


def fingerprint_image(image: Image.Image) -> str:
    """Return a persistent multi-signal avatar signature as compact JSON text."""
    hashes = []
    for scale in CROP_SCALES:
        crop = _center_crop(image, scale)
        hashes.append(f"{_dhash(crop):0{HASH_BITS // 4}x}")

    descriptors = _orb_descriptors(image)
    payload: dict[str, object] = {"v": 2, "hashes": hashes}
    if descriptors is not None and len(descriptors):
        payload["orb_rows"] = int(descriptors.shape[0])
        payload["orb"] = base64.b64encode(descriptors.tobytes()).decode("ascii")
    return json.dumps(payload, separators=(",", ":"))


def _decode_fingerprint(value: str) -> tuple[list[int], np.ndarray | None]:
    try:
        payload = json.loads(value)
        # Backward compatibility with the first development build, which stored
        # only a JSON list of perceptual hashes.
        if isinstance(payload, list):
            return [int(x, 16) for x in payload], None
        hashes = [int(x, 16) for x in payload.get("hashes", [])]
        encoded = payload.get("orb")
        rows = int(payload.get("orb_rows", 0) or 0)
        descriptors = None
        if encoded and rows > 0:
            raw = base64.b64decode(encoded)
            arr = np.frombuffer(raw, dtype=np.uint8)
            if arr.size == rows * 32:
                descriptors = arr.reshape(rows, 32).copy()
        return hashes, descriptors
    except Exception:
        return [], None


def _hash_similarity(ah: list[int], bh: list[int]) -> float:
    if not ah or not bh:
        return 0.0
    scores: list[float] = []
    for i, av in enumerate(ah):
        local = []
        for j, bv in enumerate(bh):
            if abs(i - j) <= 1:
                distance = (av ^ bv).bit_count()
                local.append(1.0 - distance / HASH_BITS)
        if local:
            scores.append(max(local))
    return sum(scores) / len(scores) if scores else 0.0


def _orb_similarity(a: np.ndarray | None, b: np.ndarray | None) -> float | None:
    if a is None or b is None or len(a) < 8 or len(b) < 8:
        return None
    matcher = cv2.BFMatcher(cv2.NORM_HAMMING)
    pairs = matcher.knnMatch(a, b, k=2)
    good = [m for m, n in pairs if m.distance < 0.75 * n.distance]
    denominator = max(1, min(len(a), len(b)))
    good_ratio = len(good) / denominator
    # In tests on the supplied game screenshots, same-avatar crops commonly
    # produce ~0.25-0.40 while unrelated avatars are around 0.00-0.08. Map that
    # useful range to a confidence-like 0..1 signal.
    return min(1.0, good_ratio / 0.35)


def fingerprint_similarity(a: str, b: str) -> float:
    ah, ao = _decode_fingerprint(a)
    bh, bo = _decode_fingerprint(b)
    hash_score = _hash_similarity(ah, bh)
    orb_score = _orb_similarity(ao, bo)
    if orb_score is None:
        return hash_score
    # ORB is the primary cross-resolution signal; dHash stabilizes low-texture
    # avatars and near-identical crops.
    return 0.82 * orb_score + 0.18 * hash_score


def crop_avatar_from_normalized_bbox(
    image_path: Path,
    bbox: tuple[int, int, int, int],
) -> Image.Image:
    """Crop an avatar from a 0..1000 normalized (x, y, width, height) bbox."""
    x, y, w, h = bbox
    with Image.open(image_path) as source:
        image = ImageOps.exif_transpose(source).convert("RGB")
        width, height = image.size
        left = max(0, min(width - 1, round(x * width / 1000)))
        top = max(0, min(height - 1, round(y * height / 1000)))
        right = max(left + 1, min(width, round((x + w) * width / 1000)))
        bottom = max(top + 1, min(height, round((y + h) * height / 1000)))
        return image.crop((left, top, right, bottom)).copy()


def fingerprint_from_bbox(
    image_path: Path,
    bbox: tuple[int, int, int, int] | None,
) -> str | None:
    if bbox is None:
        return None
    _x, _y, w, h = bbox
    if w < 10 or h < 10:
        return None
    try:
        avatar = crop_avatar_from_normalized_bbox(image_path, bbox)
        if min(avatar.size) < 12:
            return None
        return fingerprint_image(avatar)
    except Exception:
        return None


class AvatarStore:
    """Persistent avatar reference fingerprints keyed by active member ID."""

    def __init__(self, db_path: Path):
        self.db_path = db_path
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        self._init_db()

    def _connect(self):
        return sqlite3.connect(self.db_path)

    def _init_db(self) -> None:
        with self._connect() as con:
            con.execute(
                """
                CREATE TABLE IF NOT EXISTS avatar_fingerprints (
                    member_id INTEGER NOT NULL,
                    fingerprint TEXT NOT NULL,
                    source_file TEXT NOT NULL,
                    match_method TEXT NOT NULL,
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    PRIMARY KEY(member_id, fingerprint)
                )
                """
            )
            con.execute(
                "CREATE INDEX IF NOT EXISTS idx_avatar_member ON avatar_fingerprints(member_id)"
            )

    def save_reference(
        self,
        member_id: int,
        fingerprint: str | None,
        source_file: str = "",
        match_method: str = "",
    ) -> bool:
        if not fingerprint:
            return False
        with self._connect() as con:
            before = con.total_changes
            con.execute(
                """
                INSERT OR IGNORE INTO avatar_fingerprints(
                    member_id, fingerprint, source_file, match_method, created_at
                ) VALUES (?, ?, ?, ?, CURRENT_TIMESTAMP)
                """,
                (member_id, fingerprint, source_file, match_method),
            )
            return con.total_changes > before

    def fingerprints_for_member(self, member_id: int) -> list[str]:
        with self._connect() as con:
            return [str(row[0]) for row in con.execute(
                "SELECT fingerprint FROM avatar_fingerprints WHERE member_id = ?",
                (member_id,),
            ).fetchall()]

    def best_matches(
        self,
        fingerprint: str,
        member_ids: Iterable[int] | None = None,
        limit: int = 5,
    ) -> list[AvatarMatch]:
        allowed = set(member_ids) if member_ids is not None else None
        grouped: dict[int, list[str]] = {}
        with self._connect() as con:
            rows = con.execute(
                "SELECT member_id, fingerprint FROM avatar_fingerprints"
            ).fetchall()
        for member_id, stored in rows:
            mid = int(member_id)
            if allowed is not None and mid not in allowed:
                continue
            grouped.setdefault(mid, []).append(str(stored))

        matches: list[AvatarMatch] = []
        for member_id, samples in grouped.items():
            score = max(fingerprint_similarity(fingerprint, sample) for sample in samples)
            matches.append(AvatarMatch(member_id, score, len(samples)))
        matches.sort(key=lambda m: m.score, reverse=True)
        return matches[:limit]

    def stats(self) -> tuple[int, int]:
        with self._connect() as con:
            members = con.execute(
                "SELECT COUNT(DISTINCT member_id) FROM avatar_fingerprints"
            ).fetchone()[0]
            samples = con.execute("SELECT COUNT(*) FROM avatar_fingerprints").fetchone()[0]
        return int(members), int(samples)

    def clear(self) -> None:
        with self._connect() as con:
            con.execute("DELETE FROM avatar_fingerprints")
