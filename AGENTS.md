# AGENTS.md

## Project overview

Native C# **.NET MAUI** desktop app (Windows) that extracts weekly Last War: Survival Game leaderboard screenshots with a vision LLM, matches observations to a member roster, and exports a weekly scores workbook. The whole app lives under [lawar4/](lawar4/).

Start with [README.md](README.md) for build commands, configuration, the 5-step workflow, and project layout. The root [README.md](README.md) is the active project guide; do not rely on any older Python app documentation.

## Essential commands

Run from the repository root:

- Build (debug): `dotnet build lawar4/lawar4.csproj -c Debug`
- Build (release): `dotnet build lawar4/lawar4.csproj -c Release`
- Run: `& .\lawar4\bin\Debug\net10.0-windows10.0.19041.0\win-x64\lawar4.exe`
- One-time workload: `dotnet workload install maui-windows`

Requires .NET SDK 10.0+ and Windows 10 build 17763+. A local `nuget.config` scopes restore to nuget.org only.

## Architecture

Flow: [MauiProgram.cs](lawar4/MauiProgram.cs) (DI) → `MainViewModel` → `WorkflowService` → `Services/`.

- [Services/WorkflowService.cs](lawar4/Services/WorkflowService.cs): in-process orchestration and app state (config, members, screenshots, observations). No sidecar / JSON-RPC.
- [Services/ExtractorService.cs](lawar4/Services/ExtractorService.cs): rate limiter, retry/backoff, provider backends, payload sanitization and validation.
- [Services/Matcher.cs](lawar4/Services/Matcher.cs): `MemberMatcher` (deterministic matching) and `WeeklyBuilder` (dedup + weekly aggregation).
- [Services/Storage.cs](lawar4/Services/Storage.cs): `AppDatabase`, `AliasStore`, `ExtractionCache` over `app.sqlite3` (also `avatar_fingerprints`).
- [Services/AvatarStore.cs](lawar4/Services/AvatarStore.cs) + [Services/Fingerprinter.cs](lawar4/Services/Fingerprinter.cs): local dHash + ORB avatar fingerprints (no extra LLM calls).
- [Services/MembersLoader.cs](lawar4/Services/MembersLoader.cs), [Services/ExcelExporter.cs](lawar4/Services/ExcelExporter.cs), [Services/ExtractionPrompt.cs](lawar4/Services/ExtractionPrompt.cs), [Services/SecretStore.cs](lawar4/Services/SecretStore.cs), [Services/RequestRateLimiter.cs](lawar4/Services/RequestRateLimiter.cs).
- UI: [ViewModels/MainViewModel.cs](lawar4/ViewModels/MainViewModel.cs) (5-step state machine), [Views/MainPage.xaml](lawar4/Views/MainPage.xaml), models in [Models/](lawar4/Models).

## Conventions to preserve

- **Deterministic, conservative matching** (`MemberMatcher.MatchDeterministic`): visible player ID → exact normalized name (`TextUtil.NormalizeName`) → saved alias → fuzzy suggestions for manual review only. Never auto-assign fuzzy matches; store candidates in `Observation.Alternatives`. Avatar auto-assign only when top score ≥ 0.92 and margin ≥ 0.06.
- **Respect the rate limiter** (`RequestRateLimiter`): 1–30 RPM, default 28. Each retry consumes a slot. Do not bypass it in retries or background work.
- **Conservative duplicate handling** (`WeeklyBuilder.Build`, grouped by `(MemberId, Day)`): keep the highest score on conflict and record an issue.
- **Extraction cache key** = `SHA256(image_bytes ‖ model ‖ baseUrl ‖ apiStyle ‖ ExtractionPrompt.CacheVersion)`. Bump `CacheVersion` (a string) whenever the schema or prompt changes to invalidate old entries.
- **API key handling**: entered in Settings, stored via MAUI `SecureStorage` (DPAPI-backed). Never write it to `config.json`, never log it, never hard-code credentials.
- **Provider routing** (`ExtractorService`): `openai` + `chat` style → OpenAI .NET SDK; everything else (Gemini, local, custom, or `responses` style) → direct HTTP client. These code paths differ significantly.
- **MVVM**: use CommunityToolkit.Mvvm source generators — `ObservableObject` with `[ObservableProperty]` and `[RelayCommand]`; `Nullable` is enabled.
- **XAML**: `MauiXamlInflator=SourceGen` (compile-time). Keep enabled unless debugging inflation.

## Data & paths

- Config (readable JSON) and SQLite DB live under `%LOCALAPPDATA%\...\LastWarWeeklyExtractor\` (`config.json`, `app.sqlite3`).
- Config stores provider, model, base URL, API style, RPM, cache flag, and roster source — never the API key.

## Editing guidance

- Keep changes narrow and aligned to the module boundaries above (orchestration in `WorkflowService`, extraction in `ExtractorService`, matching in `Matcher`).
- For tasks touching screenshot parsing, rate limiting, or member matching, review the extractor/matcher/storage flow together rather than editing one file in isolation.
- Prefer existing patterns over new abstractions unless clearly warranted.
