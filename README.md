# lawar4

A desktop application for Windows that extracts weekly Last War: Survival Game leaderboard screenshots using a vision LLM, matches extracted player scores to an alliance roster, enables manual review of ambiguous matches, and exports a formatted multi-sheet Excel workbook.

## Requirements

- Windows 10 (build 17763+) or Windows 11
- .NET SDK 10.0+
- MAUI Windows workload: `dotnet workload install maui-windows`

## Build & run

```powershell
dotnet build lawar4/lawar4.csproj -c Debug
dotnet build lawar4/lawar4.csproj -c Release   # optimized

# Run the built executable
& .\lawar4\bin\Debug\net10.0-windows10.0.19041.0\win-x64\lawar4.exe
```

> A local `nuget.config` scopes package restore to nuget.org only.

## Configuration & data storage

- **API key security**: Entered in the Settings step and stored encrypted using Windows `SecureStorage` (DPAPI-backed). It is never written to disk in plain text and never committed.
- **Application state & cache**: Endpoint settings, roster configuration, confirmed aliases, extraction cache, and avatar reference fingerprints persist in `config.json` and SQLite (`app.sqlite3`) under `%LOCALAPPDATA%\LastWarWeeklyExtractor\`.
- **Provider routing**: The `openai` provider with the `chat` API style uses the official OpenAI .NET SDK. Other endpoints (Gemini OpenAI-compatible endpoints, local models like LM Studio / Ollama, custom servers, or `responses` style) use a direct HTTP client.
- **Rate limiting & caching**: Includes a configurable request rate limiter (1–30 RPM, default 28 RPM) and SHA-256 content-based caching to avoid re-extracting identical screenshots.

## Workflow

1. **Settings** — Configure AI provider, base URL, model name, API style, RPM limit, caching preference, and API key.
2. **Roster** — Load the active member roster from a local `.xlsx` workbook or a public Google Sheet URL.
3. **Screenshots** — Import leaderboard images or folders, run rate-limited AI extraction with day detection, and parse player ranks/scores.
4. **Review** — Inspect extracted rows; deterministic matches (player ID, exact name, saved alias) match automatically, while fuzzy name and avatar fingerprint matches provide suggestions for one-click manual confirmation.
5. **Export** — Generate a comprehensive Excel workbook containing sheets for Weekly Scores, Observations, Issues, Aliases, and Run Info.

## Project layout

- `Models/` — Domain models (`Member`, `Observation`, `ScreenshotExtraction`, `AppConfig`)
- `Services/` — Core business logic including storage (`Storage.cs`), roster loading (`MembersLoader.cs`), vision extraction (`ExtractorService.cs`, `ExtractionPrompt.cs`), avatar hashing (`Fingerprinter.cs`, `AvatarStore.cs`), matching algorithms (`Matcher.cs`), rate limiting (`RequestRateLimiter.cs`), and Excel export (`ExcelExporter.cs`)
- `ViewModels/` — MVVM state machine and presentation logic (`MainViewModel.cs`, `ObservationItem.cs`)
- `Views/` — Workflow UI page (`MainPage.xaml`), converters, and styling resources
