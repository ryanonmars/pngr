@echo off
cd /d "%~dp0"
if exist "artifacts\windows\TraceScope.exe" (
  "artifacts\windows\TraceScope.exe" --data-dir "%CD%\artifacts\data" %*
) else (
  dotnet run --project src\TraceScope.App -- --data-dir "%CD%\artifacts\data" %*
)
