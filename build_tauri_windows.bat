@echo off
setlocal
set ROOT=%~dp0
cd /d "%ROOT%"

if not exist ".venv\Scripts\python.exe" (
    echo Missing .venv. Create the Python environment first.
    exit /b 1
)
if not exist "frontend\node_modules\.bin\tauri.cmd" (
    echo Missing frontend dependencies. Run: cd frontend ^&^& npm install
    exit /b 1
)
where cargo >nul 2>nul
if errorlevel 1 if exist "%USERPROFILE%\.cargo\bin\cargo.exe" set PATH=%USERPROFILE%\.cargo\bin;%PATH%
where cargo >nul 2>nul
if errorlevel 1 (
    echo Cargo is not on PATH. Install Rust and restart the terminal.
    exit /b 1
)
set ISCC=
where iscc >nul 2>nul
if not errorlevel 1 set ISCC=iscc.exe
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe
if not defined ISCC (
    echo Inno Setup compiler ISCC.exe was not found.
    echo Install Inno Setup and restart the terminal.
    exit /b 1
)

echo Building Python sidecar...
call ".venv\Scripts\python.exe" -m PyInstaller --noconfirm --clean --onefile --name lastwar-backend backend\runner.py
if errorlevel 1 goto :error

if not exist "src-tauri\binaries" mkdir "src-tauri\binaries"
copy /Y "dist\lastwar-backend.exe" "src-tauri\binaries\lastwar-backend-x86_64-pc-windows-msvc.exe" >nul
if errorlevel 1 goto :error

echo Building Tauri application...
call "frontend\node_modules\.bin\tauri.cmd" build --no-bundle
if errorlevel 1 goto :error

echo Building Inno Setup installer...
call "%ISCC%" "installer\LastWarWeeklyExtractor.iss"
if errorlevel 1 goto :error

echo.
echo Built application: src-tauri\target\release\lastwar-weekly-extractor.exe
echo Built installer:   dist\installer\LastWarWeeklyExtractor-Setup.exe
exit /b 0

:error
echo Build failed.
exit /b 1