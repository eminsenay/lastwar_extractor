# Last War Weekly Extractor

A desktop Python application for extracting weekly leaderboard screenshots, matching the results to a member roster, reviewing ambiguous matches, and exporting a clean weekly score workbook.

## Overview

This project helps you process weekly Last War leaderboard screenshots from a mobile game client and convert them into a structured workbook you can review and share. The workflow is built around a PySide6 desktop UI and focuses on:

- extracting leaderboard rows from screenshots using a vision model
- identifying the correct weekday tab from each screenshot
- matching rows to the active member list from a local workbook or Google Sheet
- handling manual review for unclear or renamed names
- de-duplicating overlapping observations across screenshots
- exporting a final workbook with weekly scores, observations, issues, aliases, and run metadata

## Key features

- Batch import of screenshots for an entire week
- Automatic day detection per screenshot
- Local caching of successful extractions to avoid re-processing identical images
- Rate-limited API usage with a built-in 1–30 requests/minute limit
- Support for member data from:
  - a local Excel workbook
  - a public Google Sheets export URL
- Deterministic matching order:
  1. visible player ID
  2. exact normalized name
  3. saved alias
  4. manual review/fuzzy suggestions only
- Alias memory stored locally for repeatable matching in future runs
- Duplicate score resolution by keeping the highest value and recording an issue
- Export to a workbook with sheets for:
  - Weekly Scores
  - Observations
  - Issues
  - Aliases
  - Run Info

## Requirements

- Python 3.11 or newer
- Either an OpenAI-compatible vision endpoint, or a local vision model server
- Access to the screenshots to process
- A member workbook or a public Google Sheets URL containing a worksheet named Members

## Installation

Create and activate a virtual environment:

```bash
python -m venv .venv
```

On Windows:

```powershell
.venv\Scripts\activate
pip install -r requirements.txt
copy .env.example .env
```

On macOS/Linux:

```bash
source .venv/bin/activate
pip install -r requirements.txt
cp .env.example .env
```

Set your environment variable in `.env`:

```env
OPENAI_API_KEY=your_api_key_here
```

Optional configuration values:

```env
OPENAI_MODEL=gpt-5.6-terra
OPENAI_BASE_URL=https://api.openai.com/v1
OPENAI_API_STYLE=responses
```

## Using a local Hugging Face model

The app can use a vision model downloaded from Hugging Face through a local
OpenAI-compatible server. LM Studio is one practical option on Windows:

1. Install LM Studio and download a vision-capable model such as
  `Qwen2.5-VL-3B-Instruct` from Hugging Face. Use the 7B variant if your GPU
  has enough memory; local vision models need substantially more memory than
  text-only models.
2. Load the model in LM Studio and start its local server on port `1234`.
3. In the app Setup tab, set:
  - **Vision model**: the model identifier shown by LM Studio
  - **API base URL**: `http://127.0.0.1:1234/v1`
  - **API style**: `Chat Completions API (local)`
  - **Requests / minute**: a value appropriate for your machine, often `1`
    or `2`
4. Extract screenshots as usual. No `OPENAI_API_KEY` is needed for a
  `localhost` or `127.0.0.1` endpoint.

The same setup works with the CLI:

```powershell
python prototype_extract.py .\screenshots `
  --model <model-id-from-lm-studio> `
  --base-url http://127.0.0.1:1234/v1 `
  --api-style chat --rpm 2
```

For `.env`-based configuration, use:

```env
OPENAI_MODEL=<model-id-from-lm-studio>
OPENAI_BASE_URL=http://127.0.0.1:1234/v1
OPENAI_API_STYLE=chat
OPENAI_RPM=2
```

The local model must support image input and JSON output. Local structured
output support varies by runtime; if the server rejects the JSON schema,
update LM Studio or choose a vision model/runtime with structured-output
support. The app still validates every response locally with Pydantic.

## Running the app

Start the desktop application:

```bash
python app.py
```

Windows users can also use:

```bat
run.bat
```

## Typical workflow

### 1. Setup

- Choose the member source as either a local Excel workbook or a Google Sheet URL
- Ensure the sheet name is `Members`
- Load the roster

### 2. Import screenshots

- Add one or multiple screenshots, or a folder containing screenshots
- Click Extract all to process the batch
- The app will reuse cached extraction results when the image content, model, and prompt match

### 3. Review matches

- Automatically matched rows move forward without manual effort
- Unresolved rows remain open for review
- If the app cannot determine a member confidently, you can assign the row manually and optionally save the raw name as an alias

### 4. Export workbook

- Save the final weekly workbook as an `.xlsx` file
- The export includes score data for each active member by day, plus issue tracking and alias history

## Matching behavior

The matching logic is intentionally conservative:

- Player IDs win when visible and uniquely map to an active member
- Exact normalized names are matched next
- Saved aliases are used when a known prior name has been confirmed
- Fuzzy suggestions are shown only for manual review and are never silently assigned

This keeps incorrect matches from being introduced when names are slightly altered, translated, or changed over time.

## Duplicate handling

When the same member appears multiple times in a given day across screenshots, the app groups observations by member and day. If the values disagree, it keeps the highest score and records the conflict in the Issues sheet instead of silently choosing the wrong value.

## Rate limiting and reliability

The app is configured to respect a safe request budget under the OpenAI usage cap by defaulting to 28 requests/minute, with a hard upper limit of 30. The limiter is applied before every API request attempt, including retries, to keep work consistent and predictable.

## Project structure

```text
app.py                 # PySide6 desktop application entry point
extractor.py           # Screenshot extraction and API orchestration
matcher.py             # Member matching, avatar resolution, deduplication, weekly aggregation
avatars.py             # Local avatar cropping, fingerprints, similarity, and reference store
members.py             # Member workbook / Google Sheet loading
excel_export.py        # Workbook export logic
storage.py             # SQLite-backed alias and cache storage
test_offline.py        # Local validation/regression checks
requirements.txt       # Python dependencies
.env.example           # Example environment configuration
build_windows.bat      # Windows build helper for packaging
run.bat                # Windows shortcut to launch the app
```

## Development notes

- The desktop workflow is the primary product path
- Matching logic is intentionally deterministic and conservative
- Alias data is stored in a local app data directory instead of the repository itself
- Extraction results are cached by image content so repeated runs do not consume unnecessary API budget

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for the full text.

## Troubleshooting

### Missing API key

For a cloud endpoint, make sure your `.env` file exists and contains a valid
`OPENAI_API_KEY`. Local `localhost` and `127.0.0.1` endpoints do not require
one.

### Google Sheets load failure

If a spreadsheet URL cannot be downloaded anonymously, switch to the Local Excel workbook option and provide the `.xlsx` file directly.

### Members worksheet not found

The workbook must contain a worksheet named `Members`. Confirm the sheet name in the UI and ensure the required columns include `ID`, `Name`, and `Rank`.

### Duplicate or ambiguous matches

Use the Review tab to inspect unresolved or conflicting rows and assign the correct member manually when needed.

## Avatar-assisted matching

The desktop app uses avatar/profile images as an additional identity signal for screenshots where the visible player ID or current name is not enough.

### How it works

The extraction schema asks the vision model for an approximate `avatar_bbox` for every extracted row. The box uses normalized screenshot coordinates (`0..1000`), so it works across different screenshot resolutions and UI languages.

The app then performs avatar matching **locally**:

1. Match trusted identities first using visible player ID, exact canonical name, or a saved alias.
2. Crop those trusted players' avatars from the screenshots and save local avatar fingerprints in the same app SQLite database used for aliases/cache.
3. After all trusted rows in the weekly batch have been processed, compare unresolved rows with the stored avatar references.
4. Auto-assign only when the avatar score is high and clearly separated from the second-best candidate.
5. Otherwise show the avatar candidate in Review and require manual confirmation.
6. A manual assignment also teaches the avatar library for future weeks.

The matcher combines ORB image features with a multi-scale perceptual hash. The center of the avatar is emphasized so decorative frames have less influence.

This is intentionally a **two-pass weekly match**, so screenshot ordering does not matter. For example, an ID-bearing Tuesday/Thursday screenshot can teach an avatar that is then used to recognize a renamed player in a Saturday screenshot from the same batch.

Avatar comparison does **not** make additional LLM/API requests, so it does not consume extra requests under the 30 RPM API limit. The existing request limiter still defaults to 28 RPM.

### Local data

Avatar references are stored locally under the app data directory together with aliases and extraction cache. They are not written back to the Members spreadsheet.

Changing the extraction schema to include avatar bounding boxes also bumps the extraction cache version, so old cached results without avatar geometry are not accidentally reused by the desktop app.

### Matching priority

The effective matching order is now:

1. visible player ID
2. exact normalized member name
3. saved alias
4. high-confidence avatar auto-match
5. avatar + fuzzy-name suggestions for manual review

Fuzzy name matching alone still never auto-assigns a player.

## Avatar feature dependencies

The avatar matcher adds:

- `Pillow` for image cropping/resizing
- `opencv-python-headless` for ORB feature extraction/matching
- `numpy` for descriptor storage/processing

They are included in `requirements.txt`.


## Local-model compatibility

The day is inferred primarily from the **selected tab position**, not the written language.

Local-model compatibility safeguards are applied after JSON parsing:

- `day_confidence` is always normalized to the `0.0..1.0` range. If a model mistakenly returns the weekday index (for example `6` for Saturday), the app converts a matching index to `1.0` and records a warning.
- Ranking rows explicitly separate the first/upper **player-name** line (`raw_name`) from the second/lower **alliance/role** line (`alliance_name`). The prompt and schema reinforce this distinction, and an unambiguous swapped pair is repaired automatically.
