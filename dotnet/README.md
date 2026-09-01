# Last War Weekly Extractor — .NET MAUI (Windows)

A native C# .NET MAUI desktop rewrite of the original Python + Tauri application. It extracts
weekly Last War leaderboard screenshots with a vision LLM, matches observations to member
records, and exports a weekly scores workbook.

This project is self-contained under `dotnet/` so the legacy Python/Tauri app can be removed
once this version is verified.

## Requirements

- Windows 10 (build 17763+) / Windows 11
- .NET SDK 10.0+
- MAUI Windows workload: `dotnet workload install maui-windows`

## Build & run

```powershell
cd dotnet
dotnet build LastWarExtractor/LastWarExtractor.csproj -c Debug
dotnet build LastWarExtractor/LastWarExtractor.csproj -c Release   # optimized

# Run the built executable
& .\LastWarExtractor\bin\Debug\net10.0-windows10.0.19041.0\win-x64\LastWarExtractor.exe
```

> A local `nuget.config` scopes restore to nuget.org only.

## Configuration & data

- The **API key** is entered in the Settings screen and stored encrypted via Windows
  `SecureStorage` (DPAPI-backed). It is never written to disk in plain text and never committed.
- Endpoint/roster settings persist in `config.json` and the SQLite database (`app.sqlite3`) under
  `%LOCALAPPDATA%\...\LastWarWeeklyExtractor\`.
- Provider routing: the `openai` provider with the `chat` API style uses the official OpenAI .NET
  SDK; every other provider/style (Gemini OpenAI-compatible endpoint, local, custom, and the
  `responses` style) uses a direct HTTP client.

## Workflow

1. **Settings** — provider, base URL, model, API style, RPM, cache, and API key.
2. **Roster** — load the active member list from a local `.xlsx` or a Google Sheet URL.
3. **Screenshots** — add images or a folder, then extract (rate-limited, cached).
4. **Review** — resolve unmatched observations; suggestions come from name/alias/avatar signals.
5. **Export** — write the multi-sheet weekly workbook.

## Project layout

- `Models/` — domain models and config
- `Services/` — storage (SQLite), members loading (ClosedXML), extraction (rate limiter, retry,
  schema), avatar fingerprinting (OpenCvSharp + ImageSharp), matcher, Excel export, and the
  in-process `WorkflowService`
- `ViewModels/` — MVVM view models
- `Views/` — the workflow page, converters, and styles
