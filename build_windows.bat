@echo off
setlocal
python -m pip install pyinstaller
if errorlevel 1 goto :error
pyinstaller --noconfirm --windowed --name LastWarWeeklyExtractor app.py
if errorlevel 1 goto :error
echo.
echo Built: dist\LastWarWeeklyExtractor\LastWarWeeklyExtractor.exe
exit /b 0
:error
echo Build failed.
pause
exit /b 1
