# AGENTS.md

## Project overview

This repository is a desktop Python app for extracting weekly Last War leaderboard screenshots, matching observations to member records, and exporting a workbook of weekly scores.

Start with the project docs in [README.md](README.md), then the runtime entry points in [app.py](app.py) and [prototype_extract.py](prototype_extract.py).

## Essential commands

- Create environment: `python -m venv .venv`
- Activate on Windows: `.venv\Scripts\activate`
- Install dependencies: `pip install -r requirements.txt`
- Configure environment: copy `.env.example` to `.env` and set `OPENAI_API_KEY`
- Run desktop app: `python app.py`
- Run CLI extractor: `python prototype_extract.py ./screenshots --output-dir ./output --rpm 28`

## Environment and configuration

- Target Python: 3.11+
- Runtime defaults are documented in [README.md](README.md)
- API configuration is read from environment variables, not hard-coded in the app
- Keep the app under the 30 requests/minute limit; the default safety margin is 28 RPM

## Architecture

- [app.py](app.py): PySide6 UI, worker threads, setup/import/review/export workflow, settings persistence
- [extractor.py](extractor.py): screenshot extraction and API request orchestration
- [matcher.py](matcher.py): member matching, deduplication, and weekly score aggregation
- [members.py](members.py): loading member data from local XLSX or Google Sheets URLs
- [storage.py](storage.py): SQLite-backed alias and extraction cache storage
- [excel_export.py](excel_export.py): workbook export for the final analysis files
- [test_offline.py](test_offline.py): offline validation and regression checks

## Conventions to preserve

- Respect the request limiter and avoid bypassing rate limits in retries or background work
- Prefer deterministic matching order: visible player ID, normalized exact name, saved alias, fuzzy manual review only
- Do not silently assign fuzzy matches; unresolved names should remain for human review
- Keep duplicate handling conservative: keep the highest score when duplicates disagree and record an issue
- Preserve the CLI prototype workflow alongside the desktop app
- Prefer caching extraction results by file content/model/prompt to reduce API usage
- Store manually confirmed aliases locally in the user app data directory, not in the repo

## Editing guidance

- Keep changes narrow and aligned to the existing structure above
- Follow the current naming and separation of concerns: UI in [app.py](app.py), extraction logic in [extractor.py](extractor.py), matching logic in [matcher.py](matcher.py)
- If you change extraction or matching behavior, validate with the smallest relevant local check instead of broad suite assumptions
- When touching env/API behavior, keep values configurable and documented in [README.md](README.md)
- Do not hard-code OpenAI credentials into source files

## Notes for agents

- This repo is not a large multi-service app; the highest-value context is the README and the module boundaries listed above
- If a task involves screenshot parsing, rate limiting, or member matching, inspect the extractor/matcher/member flow together rather than editing a single file in isolation
- Prefer existing patterns in the codebase over creating new abstractions unless the change is clearly warranted
