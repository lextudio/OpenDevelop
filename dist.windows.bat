@echo off
rem dist.windows.bat - thin wrapper, the Windows counterpart of dist.macos.sh. All packaging
rem logic lives in the cross-platform dist.ps1; this only locates pwsh and translates the
rem POSIX-style flags so both platforms are driven the same way from a shell.
rem
rem Usage: dist.windows.bat [--skip-publish] [--debug]
rem   --skip-publish  reuse existing publish output (faster iteration on payload/zip)
rem   --debug         package the Debug configuration instead of Release
rem
rem PowerShell-native flags still work as-is: dist.windows.bat -Configuration Debug

setlocal enabledelayedexpansion

set "REPO_ROOT=%~dp0"

rem Windows PowerShell 5.1 (powershell.exe) is deliberately NOT accepted as a fallback: dist.ps1
rem branches on $IsWindows and uses ?. / ternary syntax, both of which are PowerShell 7+ only.
set "PWSH="
for %%I in (pwsh.exe) do set "PWSH=%%~$PATH:I"
if not defined PWSH (
    for %%C in (
        "%ProgramFiles%\PowerShell\7\pwsh.exe"
        "%ProgramFiles(x86)%\PowerShell\7\pwsh.exe"
        "%LocalAppData%\Microsoft\PowerShell\7\pwsh.exe"
    ) do (
        if not defined PWSH if exist "%%~C" set "PWSH=%%~C"
    )
)
if not defined PWSH (
    echo dist.windows.bat: cannot find pwsh ^(PowerShell 7+^). Install it with: winget install Microsoft.PowerShell 1>&2
    exit /b 1
)

rem Explicit map, mirroring dist.macos.sh: PowerShell does NOT bind "-skip-publish" to
rem "-SkipPublish" (the dashes must go), and --debug collides with PowerShell's COMMON -Debug
rem parameter, so dist.ps1 exposes -Configuration instead. Anything else is passed through with a
rem single leading dash and left to PowerShell's own strict parameter binding to accept or reject.
set "ARGS="
:parse
if "%~1"=="" goto run
set "ARG=%~1"
if /i "!ARG!"=="--skip-publish" (
    set "ARGS=!ARGS! -SkipPublish"
) else if /i "!ARG!"=="--debug" (
    set "ARGS=!ARGS! -Configuration Debug"
) else if "!ARG:~0,2!"=="--" (
    set "ARGS=!ARGS! -!ARG:~2!"
) else (
    set "ARGS=!ARGS! !ARG!"
)
shift
goto parse

:run
rem -File (not -Command) so the exit code of dist.ps1 reaches the caller unchanged; its smoke-test
rem failure path exits 1 and CI/callers must see that.
"%PWSH%" -NoProfile -File "%REPO_ROOT%dist.ps1"!ARGS!
exit /b %ERRORLEVEL%
