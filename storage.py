from __future__ import annotations

import sqlite3
from pathlib import Path


class AliasStore:
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
                CREATE TABLE IF NOT EXISTS aliases (
                    normalized_alias TEXT PRIMARY KEY,
                    alias TEXT NOT NULL,
                    member_id INTEGER NOT NULL,
                    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                )
                """
            )

    def get_member_id(self, normalized_alias: str) -> int | None:
        with self._connect() as con:
            row = con.execute(
                "SELECT member_id FROM aliases WHERE normalized_alias = ?",
                (normalized_alias,),
            ).fetchone()
            return int(row[0]) if row else None

    def save_alias(self, normalized_alias: str, alias: str, member_id: int) -> None:
        with self._connect() as con:
            con.execute(
                """
                INSERT INTO aliases(normalized_alias, alias, member_id, updated_at)
                VALUES (?, ?, ?, CURRENT_TIMESTAMP)
                ON CONFLICT(normalized_alias) DO UPDATE SET
                    alias=excluded.alias,
                    member_id=excluded.member_id,
                    updated_at=CURRENT_TIMESTAMP
                """,
                (normalized_alias, alias, member_id),
            )

    def list_aliases(self) -> list[tuple[str, int]]:
        with self._connect() as con:
            return [(str(a), int(mid)) for a, mid in con.execute(
                "SELECT alias, member_id FROM aliases ORDER BY alias"
            ).fetchall()]


class ExtractionCache:
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
                CREATE TABLE IF NOT EXISTS extraction_cache (
                    cache_key TEXT PRIMARY KEY,
                    result_json TEXT NOT NULL,
                    source_name TEXT NOT NULL,
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                )
                """
            )

    def get(self, cache_key: str) -> str | None:
        with self._connect() as con:
            row = con.execute(
                "SELECT result_json FROM extraction_cache WHERE cache_key = ?",
                (cache_key,),
            ).fetchone()
            return str(row[0]) if row else None

    def put(self, cache_key: str, source_name: str, result_json: str) -> None:
        with self._connect() as con:
            con.execute(
                """
                INSERT INTO extraction_cache(cache_key, result_json, source_name, created_at)
                VALUES (?, ?, ?, CURRENT_TIMESTAMP)
                ON CONFLICT(cache_key) DO UPDATE SET
                    result_json=excluded.result_json,
                    source_name=excluded.source_name,
                    created_at=CURRENT_TIMESTAMP
                """,
                (cache_key, result_json, source_name),
            )

    def clear(self) -> None:
        with self._connect() as con:
            con.execute("DELETE FROM extraction_cache")
