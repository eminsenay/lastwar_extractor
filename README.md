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
- A valid OpenAI API key in the environment
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
```

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
matcher.py             # Member matching, deduplication, and weekly aggregation
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

If the app reports that `OPENAI_API_KEY` is not set, make sure your `.env` file exists and contains a valid key.

### Google Sheets load failure

If a spreadsheet URL cannot be downloaded anonymously, switch to the Local Excel workbook option and provide the `.xlsx` file directly.

### Members worksheet not found

The workbook must contain a worksheet named `Members`. Confirm the sheet name in the UI and ensure the required columns include `ID`, `Name`, and `Rank`.

### Duplicate or ambiguous matches

Use the Review tab to inspect unresolved or conflicting rows and assign the correct member manually when needed.
