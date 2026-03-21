@echo off
REM Fake pg_dump for local testing
REM This script looks for -f <filename> and writes a small file there, then exits 0

setlocal EnableDelayedExpansion
set outfile=
set prev=
for %%A in (%*) do (
  if "!prev!"=="-f" (
    set outfile=%%~A
  )
  set prev=%%~A
)

if "%outfile%"=="" (
  echo Missing -f output file argument
  exit /b 1
)

REM Ensure directory exists
for %%I in ("%outfile%") do set outdir=%%~dpI
if not exist "%outdir%" (
  mkdir "%outdir%" >nul 2>&1
)

echo Fake pg_dump created at %date% %time% > "%outfile%"
echo Command: %~nx0 %* >> "%outfile%"
echo Command line: %* >> "%outfile%"

exit /b 0
