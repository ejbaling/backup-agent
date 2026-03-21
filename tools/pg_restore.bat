@echo off
REM Fake pg_restore for local testing
REM This script supports: pg_restore --list <filename>

setlocal EnableDelayedExpansion
set file=
set prev=
for %%A in (%*) do (
  if "!prev!"=="--list" (
    set file=%%~A
  )
  set prev=%%~A
)

if "%file%"=="" (
  echo Missing --list file argument
  exit /b 1
)

echo Mock pg_restore --list output for %file%
echo 1234; TABLE; public; example

exit /b 0
