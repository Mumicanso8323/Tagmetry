@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem ============================================================
rem  tool.bat (v6 - MSBuild Temp fix + reliable failure stop)
rem
rem  Fixes compared to v5:
rem   1) NEVER set variable named "TMP" (it shadows the system TMP env var)
rem      MSBuild uses TMP/TEMP for its temp directory. If TMP becomes a file path,
rem      MSBuild crashes with:
rem        Cannot create '...\Temp\tagmetry_tool_....log' because it already exists.
rem   2) Treat ANY non-zero exit code as failure (including negative HRESULTs),
rem      and stop the script immediately.
rem ============================================================

chcp 65001 >nul 2>&1

set "ROOT=%~dp0"
pushd "%ROOT%" || (echo [ERROR] cannot cd to repo root & exit /b 1)

if not exist "log"  mkdir "log"  >nul 2>&1
if not exist "dist" mkdir "dist" >nul 2>&1

call :make_ts
set "LOGFILE=%ROOT%log\tool_%TS%.log"

set "MODE=%~1"
set "INTERACTIVE=0"
set "NOPAUSE=0"
for %%A in (%*) do if /i "%%~A"=="--nopause" set "NOPAUSE=1"

>>"%LOGFILE%" echo ============================================================
>>"%LOGFILE%" echo [INFO] Start : %DATE% %TIME%
>>"%LOGFILE%" echo [INFO] ROOT  : "%ROOT%"
>>"%LOGFILE%" echo [INFO] ARGS  : %*
>>"%LOGFILE%" echo [INFO] TEMP  : "%TEMP%"
>>"%LOGFILE%" echo [INFO] TMP   : "%TMP%"
>>"%LOGFILE%" echo [INFO] LOG   : "%LOGFILE%"
>>"%LOGFILE%" echo ============================================================
>>"%LOGFILE%" echo.

echo [INFO] ROOT    = "%ROOT%"
echo [INFO] LOGFILE = "%LOGFILE%"
echo [INFO] ARGS    = %*
echo [INFO] TEMP    = "%TEMP%"
echo [INFO] TMP     = "%TMP%"
echo ------------------------------------------------------------

if "%MODE%"=="" goto :interactive
if /i "%MODE%"=="help" goto :usage_pause
if /i "%MODE%"=="/?"   goto :usage_pause
if /i "%MODE%"=="-h"   goto :usage_pause
if /i "%MODE%"=="--help" goto :usage_pause
goto :after_mode

:interactive
set "INTERACTIVE=1"
call :usage
echo.
choice /c DPQ /n /m "Select: [D]ebug  [P]ublish  [Q]uit : "
if errorlevel 3 goto :quit
if errorlevel 2 set "MODE=publish"
if errorlevel 1 set "MODE=debug"

:after_mode
echo [INFO] MODE    = "%MODE%"
>>"%LOGFILE%" echo [INFO] mode="%MODE%"

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERROR] dotnet not found in PATH.
  >>"%LOGFILE%" echo [ERROR] dotnet not found in PATH.
  goto :fail
)

if /i "%MODE%"=="debug"   goto :debug
if /i "%MODE%"=="publish" goto :publish

echo [ERROR] Unknown mode: "%MODE%"
>>"%LOGFILE%" echo [ERROR] Unknown mode: "%MODE%"
goto :usage_pause


:debug
call :must dotnet --version
call :must dotnet restore "%ROOT%Tagmetry.sln"
call :must dotnet build "%ROOT%Tagmetry.sln" -c Debug
call :must dotnet run --project "%ROOT%src\Tagmetry.Web\Tagmetry.Web.csproj" -c Debug
goto :ok


:publish
call :must dotnet --version
call :must dotnet restore "%ROOT%Tagmetry.sln"
call :must dotnet build "%ROOT%Tagmetry.sln" -c Release

set "OUTDIR=%ROOT%dist\web"
if exist "%OUTDIR%" rmdir /s /q "%OUTDIR%" >nul 2>&1
mkdir "%OUTDIR%" >nul 2>&1

rem keep this as ONE line to avoid caret parsing surprises
call :must dotnet publish "%ROOT%src\Tagmetry.Web\Tagmetry.Web.csproj" -c Release -r win-x64 --self-contained true -o "%OUTDIR%" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false

if exist "%ROOT%third_party" (
  echo [INFO] Copy third_party -> dist\web\third_party
  >>"%LOGFILE%" echo [INFO] Copy third_party -> dist\web\third_party
  xcopy "%ROOT%third_party" "%OUTDIR%\third_party\" /E /I /Y >> "%LOGFILE%" 2>&1
)

echo [INFO] Published to: "%OUTDIR%"
>>"%LOGFILE%" echo [INFO] Published to: "%OUTDIR%"
goto :ok


:must
call :run %*
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" goto :fail
exit /b 0


:run
set "_TMPFILE=%TEMP%\tagmetry_tooltmp_%RANDOM%_%RANDOM%.txt"

echo.
echo [RUN] %*
>>"%LOGFILE%" echo.
>>"%LOGFILE%" echo [RUN] %*

rem Capture output (IMPORTANT: do NOT set TMP env var)
%* > "%_TMPFILE%" 2>&1
set "RC=%ERRORLEVEL%"

type "%_TMPFILE%"
type "%_TMPFILE%" >> "%LOGFILE%"
del /q "%_TMPFILE%" >nul 2>&1

if "%RC%"=="0" exit /b 0
echo [ERROR] command failed (rc=%RC%): %*
>>"%LOGFILE%" echo [ERROR] command failed (rc=%RC%): %*
rem normalize to 1 so IF ERRORLEVEL works consistently
exit /b 1


:make_ts
set "RAW=%DATE%%TIME%"
set "DIGITS="
for /l %%i in (0,1,80) do (
  set "CH=!RAW:~%%i,1!"
  for %%d in (0 1 2 3 4 5 6 7 8 9) do (
    if "!CH!"=="%%d" set "DIGITS=!DIGITS!!CH!"
  )
)
set "TS=!DIGITS:~0,8!_!DIGITS:~8,6!"
if "!TS!"=="_" set "TS=unknown"
exit /b 0


:usage
echo Usage:
echo   tool.bat debug
echo   tool.bat publish
echo.
echo Options:
echo   --nopause   : do not pause at end in interactive mode
echo.
echo Logs:
echo   "%ROOT%log\tool_*.log"
>>"%LOGFILE%" echo [INFO] Usage shown.
exit /b 0


:usage_pause
call :usage
if "%INTERACTIVE%"=="1" if "%NOPAUSE%"=="0" pause
goto :end


:quit
echo [INFO] quit.
>>"%LOGFILE%" echo [INFO] quit.
if "%INTERACTIVE%"=="1" if "%NOPAUSE%"=="0" pause
goto :end


:fail
echo ------------------------------------------------------------
echo [FAIL] log: "%LOGFILE%"
>>"%LOGFILE%" echo ------------------------------------------------------------
>>"%LOGFILE%" echo [FAIL] log: "%LOGFILE%"
if "%INTERACTIVE%"=="1" if "%NOPAUSE%"=="0" pause
goto :end


:ok
echo ------------------------------------------------------------
echo [OK] done. log: "%LOGFILE%"
>>"%LOGFILE%" echo ------------------------------------------------------------
>>"%LOGFILE%" echo [OK] done. log: "%LOGFILE%"
if "%INTERACTIVE%"=="1" if "%NOPAUSE%"=="0" pause

:end
popd
exit /b 0
